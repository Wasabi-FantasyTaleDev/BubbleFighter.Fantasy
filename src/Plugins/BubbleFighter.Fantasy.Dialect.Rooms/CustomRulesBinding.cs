using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts native custom-option vectors while shared room logic owns authority and mode validation.</summary>
internal sealed class CustomRulesBinding(string protocol, IOpcodeTable opcodes, PacketTables tables,
    string build, ILogger logger) : IInboundMessageBinding<RoomCustomRulesChangeRequested>, IMessageSender<RoomCustomRulesReplied>
{
    /// <summary>Gets the game connection that carries custom room settings.</summary>
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        Validate();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomCustomRulesChangeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomCustomRulesReplied>>(this));
        RoomRoutes.Bind(context, protocol, opcodes, logger, "gcpCustomGameModeSetting_", Read);
    }

    internal void Validate()
    {
        if (!tables.CapabilityOf(build, "room_custom_rules"))
            throw new ConfigException($"Build '{build}' has no audited custom room rule lifecycle.");
        PacketLayoutContract.RequireSignature(tables, build, "CustomGameModeRequest", "Custom room rules",
            "matches_mode_defaults:bool,rules:list<CustomGameModeValue>");
        PacketLayoutContract.RequireSignature(tables, build, "CustomGameModeValue", "Custom room rules",
            "value_id:u16,values:list<u32>");
        PacketLayoutContract.RequireSignature(tables, build, "CustomGameModeReply", "Custom room rules",
            "result:u16,unknown_2:u16,unknown_4:wstr");
        _ = RoomCustomRuleFields.Load(tables, build);
        RequireOpcode(PacketDirection.ClientToServer, "gcpCustomGameModeSetting_");
        RequireOpcode(PacketDirection.ServerToClient, "gspCustomGameModeSetting");
    }

    internal RoomCustomRulesChangeRequested Read(ReadOnlySpan<byte> body)
    {
        // kr-1256 sub_1138B20 names 35 selectors; kr-1256 sub_546150 writes canonical bool32 and unsigned words.
        var fields = PacketCodec.Read(tables, build, "CustomGameModeRequest", body,
            new PacketReadLimits(MaxCollectionElements: 35), requireCanonicalBooleans: true);
        var rows = (object?[])fields["rules"]!;
        var ids = new HashSet<ushort>();
        var result = new RoomCustomRuleValue[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = (Dictionary<string, object?>)rows[index]!;
            var id = (ushort)row["value_id"]!;
            var values = ((object?[])row["values"]!).Select(value => unchecked((uint)(int)value!)).ToArray();
            if (!ids.Add(id) || id == 0 || values.Length == 0)
                throw new PacketFormatException("Custom room rules contain a duplicate, zero selector or empty value list.", 0);
            result[index] = new(id, Array.AsReadOnly(values));
        }
        return new(build, (bool)fields["matches_mode_defaults"]!, Array.AsReadOnly(result));
    }

    internal byte[] Write(RoomCustomRulesReplied message)
    {
        // kr-1256 sub_9D2A60 and cn-774 sub_BCEAF0 initialize then discard the second word and text.
        // Keep their zero/empty defaults at the wire boundary; only the result controls feedback.
        return PacketCodec.Write(tables, build, "CustomGameModeReply", new Dictionary<string, object?>
        { ["result"] = message.Result, ["unknown_2"] = (ushort)0, ["unknown_4"] = string.Empty });
    }

    private void RequireOpcode(PacketDirection direction, string name)
    {
        if (opcodes.All.FirstOrDefault(row => row.Direction == direction && row.Name == name) is not { Evidence: Evidence.Verified })
            throw new ConfigException($"Custom room rules require verified opcode '{name}' for '{build}'.");
    }

    /// <summary>Sends feedback after the feature has published the new full room settings to its members.</summary>
    public ValueTask SendAsync(ISession session, RoomCustomRulesReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspCustomGameModeSetting"), Write(message), ct);
}
