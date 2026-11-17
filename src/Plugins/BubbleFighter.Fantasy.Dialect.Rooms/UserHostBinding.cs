using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Translates sponsor (<c>user_host</c>) prize-room setting, default bounds, and start-status
/// frames for builds carrying the <c>user_host</c> capability and <c>user_host_referee</c> metadata.
/// </summary>
internal sealed class UserHostBinding(
    string protocol,
    IOpcodeTable opcodes,
    PacketTables tables,
    string build,
    ILogger logger,
    bool bindModernStart = false)
    : IUserHostDialectPolicy,
      IInboundMessageBinding<UserHostSettingRequested>,
      IMessageSender<UserHostSettingUpdated>,
      IMessageSender<UserHostDefaultsPublished>,
      IMessageSender<UserHostStartReplied>
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public byte RefereeTeam { get; private set; }

    /// <inheritdoc />
    public int RefereeSlotIndex { get; private set; }

    internal void Validate()
    {
        if (!tables.CapabilityOf(build, "user_host"))
            throw new ConfigException($"Build '{build}' has no audited user_host prize-room capability.");
        if (!TryReadRefereeSeat(tables, build, out var refereeTeam, out var refereeSlot))
            throw new ConfigException($"Build '{build}' requires valid user_host_referee (team, slot_index) metadata in packets/builds.yaml.");
        RefereeTeam = refereeTeam;
        RefereeSlotIndex = refereeSlot;

        PacketLayoutContract.RequireSignature(tables, build, "UserHostSettingRequest", "User-host setting", "action:u32,prize:u32");
        PacketLayoutContract.RequireSignature(tables, build, "UserHostSettingUpdate", "User-host setting", "action:u32,prize:u32,min_prize:u32,max_prize:u32");
        PacketLayoutContract.RequireSignature(tables, build, "UserHostDefaultSetting", "User-host defaults", "min_prize:u32,max_prize:u32");
        PacketLayoutContract.RequireSignature(tables, build, "UserHostStartReply", "User-host start", "status:u32");
        if (!tables.Resolve(build, "GameSetting").Any(field => field.Name == "is_user_host" && field.Type == "bool"))
            throw new ConfigException($"User-host build '{build}' requires bool field GameSetting.is_user_host.");

        RequireOpcode(PacketDirection.ClientToServer, "gcpUserHostStart");
        RequireOpcode(PacketDirection.ClientToServer, "gcpUserHostSetting");
        RequireOpcode(PacketDirection.ServerToClient, "gspUserHostStart");
        RequireOpcode(PacketDirection.ServerToClient, "gspUserHostSetting");
        RequireOpcode(PacketDirection.ServerToClient, "gspUserHostDefaultSetting");
    }

    internal void Mount(IPluginContext context)
    {
        Validate();
        context.Effect(context.Services.RegisterMany<IUserHostDialectPolicy>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<UserHostSettingRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<UserHostSettingUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<UserHostDefaultsPublished>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<UserHostStartReplied>>(this));
        RoomRoutes.Bind(context, protocol, opcodes, logger, "gcpUserHostSetting", ReadSetting);
        if (bindModernStart)
            RoomRoutes.Bind(context, protocol, opcodes, logger, "gcpUserHostStart", ReadModernStart);
        logger.LogInformation("dialect.rooms: user_host of {Build} mounted for {Protocol} (referee team={Team}, slot={Slot}).",
            build, protocol, RefereeTeam, RefereeSlotIndex);
    }

    internal UserHostSettingRequested ReadSetting(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "UserHostSettingRequest", body);
        return new UserHostSettingRequested((int)fields["action"]!, unchecked((uint)(int)fields["prize"]!));
    }

    internal static RoomStartRequested ReadModernStart(ReadOnlySpan<byte> body)
    {
        PlazaRoomWire.ReadUserHostStart(body);
        return new RoomStartRequested { FromUserHostPacket = true };
    }

    /// <summary>Reads <c>GameSetting.is_user_host</c> using the build's packet table.</summary>
    internal static bool ReadIsUserHost(PacketTables tables, string build, ReadOnlySpan<byte> gameSetting)
    {
        if (gameSetting.IsEmpty)
            return false;
        var fields = PacketCodec.Read(tables, build, "GameSetting", gameSetting);
        return fields.TryGetValue("is_user_host", out var flag) && flag is true;
    }

    /// <summary>Updates <c>GameSetting.is_user_host</c> inside an encoded <c>GameSetting</c> block.</summary>
    internal static byte[] WriteIsUserHost(PacketTables tables, string build, ReadOnlySpan<byte> gameSetting, bool isUserHost)
    {
        var fields = PacketCodec.Read(tables, build, "GameSetting", gameSetting);
        if (fields.TryGetValue("is_user_host", out var existing) && existing is bool current && current == isUserHost)
            return gameSetting.ToArray();
        fields["is_user_host"] = isUserHost;
        return PacketCodec.Write(tables, build, "GameSetting", fields);
    }

    internal static bool TryReadRefereeSeat(PacketTables tables, string build, out byte team, out int slotIndex)
    {
        team = 0;
        slotIndex = -1;
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("user_host_referee", out var raw) || raw is not Dictionary<string, object?> map)
            return false;
        if (!TryParseInt(map.GetValueOrDefault("team"), out var parsedTeam) || parsedTeam is <= 0 or > byte.MaxValue)
            return false;
        if (!TryParseInt(map.GetValueOrDefault("slot_index"), out var parsedSlot) || parsedSlot is < 0 or >= 16)
            return false;
        team = (byte)parsedTeam;
        slotIndex = parsedSlot;
        return true;
    }

    private static bool TryParseInt(object? value, out int parsed)
    {
        switch (value)
        {
            case int i:
                parsed = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                parsed = (int)l;
                return true;
            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sVal):
                parsed = sVal;
                return true;
            default:
                parsed = 0;
                return false;
        }
    }

    private void RequireOpcode(PacketDirection direction, string name)
    {
        if (opcodes.Find(direction, name) is null)
            throw new ConfigException($"User-host feature requires opcode '{name}' ({direction}) for '{build}'.");
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, UserHostSettingUpdated message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspUserHostSetting"), PacketCodec.Write(tables, build, "UserHostSettingUpdate", new Dictionary<string, object?>
        {
            ["action"] = message.Action,
            ["prize"] = unchecked((int)message.Prize),
            ["min_prize"] = unchecked((int)message.MinPrize),
            ["max_prize"] = unchecked((int)message.MaxPrize),
        }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, UserHostDefaultsPublished message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspUserHostDefaultSetting"), PacketCodec.Write(tables, build, "UserHostDefaultSetting", new Dictionary<string, object?>
        {
            ["min_prize"] = unchecked((int)message.MinPrize),
            ["max_prize"] = unchecked((int)message.MaxPrize),
        }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, UserHostStartReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspUserHostStart"), PacketCodec.Write(tables, build, "UserHostStartReply", new Dictionary<string, object?>
        {
            ["status"] = message.Status,
        }), ct);
}
