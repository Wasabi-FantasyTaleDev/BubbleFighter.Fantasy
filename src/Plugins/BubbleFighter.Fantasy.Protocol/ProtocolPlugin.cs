using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// Client wire protocol plugin. It publishes the profile from the row, loads the opcode
/// table, mounts the game and relay connection protocols named by that profile, and
/// registers the patch-check handler for the client's mandatory first packet (the table row
/// named by <see cref="ProtocolOptions.PatchRequestName"/>: <c>gcpReqGamePatch</c> on kr-1256
/// and <c>gcpFirstConnect</c> on the legacy builds, builder kr-1256 <c>sub_99DD00</c>) that
/// verifies <c>ClientVersion_Major</c> and replies <c>gspRepGamePatch(0, "")</c>. Connection
/// protocols are named after the profile, <c>&lt;profile&gt;/game</c> and
/// <c>&lt;profile&gt;/relay</c>.
/// </summary>
[Plugin("protocol", Description = "Client wire protocol. Opcode table and version come from the profile row.",
    Requires = [typeof(IConnectionProtocolRegistry), typeof(IPacketRouter), typeof(IClientProfileRegistry), typeof(IOpcodeTableRegistry), typeof(IWireDialectRegistry)],
    Optional = [typeof(ISessionRegistry)],
    ProvidesMany = [typeof(IOpcodeTable)])]
public sealed class ProtocolPlugin : IPlugin
{
    /// <summary>The game connection protocol name of the kr-1256 profile.</summary>
    public const string GameProtocolName = "kr-1256/game";

    /// <summary>The relay connection protocol name of the kr-1256 profile.</summary>
    public const string RelayProtocolName = "kr-1256/relay";

    /// <summary>
    /// The byte length of the <c>gcpReqGamePatch</c> handshake body, whose first
    /// <c>i32</c> is <c>ClientVersion_Major</c>. A body of any other length on
    /// opcode 1 is not this handshake.
    /// </summary>
    private const int PatchHandshakeSize = 96;

    /// <summary>The wide strings before and after the second <c>u32</c> of <c>gcpFirstConnect</c>.</summary>
    private const int FirstConnectLeadingStrings = 7, FirstConnectTrailingStrings = 15;

    /// <summary>The longest <c>gcpFirstConnect</c> string accepted, in UTF-16 code units.</summary>
    private const int FirstConnectMaxChars = 1024;

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ProtocolOptions>();
        if (options.SendInGameGreeting)
            throw new ConfigException("send_in_game_greeting was removed; set it to false and mount match-connection with its match dialect rows.");
        var build = context.Row.ClientBuild ?? throw new ConfigException("Protocol requires an explicit client_build.");
        var dialect = context.Services.Get<IWireDialectRegistry>().Resolve(build);
        var profile = dialect.Profile;
        var greeting = dialect.BuildServerPreamble((ushort)options.BootstrapStatus, options.BootstrapCommandLine);
        if (greeting.Length != dialect.PreambleLength(PacketDirection.ServerToClient))
            throw new ConfigException($"Dialect '{dialect.Id}' returned an inconsistent server preamble.");
        if (dialect.PreambleLength(PacketDirection.ClientToServer) != 0)
            throw new ConfigException($"Protocol row does not support dialect '{dialect.Id}' client preambles.");
        var runtime = OpcodeTableLoader.Load(options.OpcodeTablePath, profile);
        var catalog = options.UseCatalogCandidates
            ? CatalogOpcodeFallback.Load(options, runtime, context.Logger) : null;
        IOpcodeTable table = catalog is null ? runtime
            : new OpcodeTable(profile, runtime.All.Concat(runtime.ForConnection(OpcodeConnection.Relay).All).Distinct(), catalog.Resolve);
        var sessions = context.Services.TryGet<ISessionRegistry>(out var registry) ? registry : null;
        var versionVerified = new VersionVerifiedScope(options.MaxTrackedConnections);
        context.Effect(versionVerified);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => versionVerified.Remove(e.Connection.Id)));

        context.Effect(context.Services.Get<IOpcodeTableRegistry>().Register(table));
        // Rows that name their packets list IOpcodeTable as optional, which makes them wait
        // for this row, so every table is registered before they resolve names.
        context.Effect(context.Services.RegisterMany<IOpcodeTable>(table));
        context.Effect(context.Services.Get<IClientProfileRegistry>().Register(profile));

        var gameProtocol = profile.Id + "/game";
        var relayProtocol = profile.Id + "/relay";
        var router = context.Services.Get<IPacketRouter>();
        var protocols = context.Services.Get<IConnectionProtocolRegistry>();
        context.Effect(protocols.Register(new WireProtocol(gameProtocol, dialect, greeting, options, router, table, context.Events, sessions, versionVerified, context.Logger, context.Lifetime, requiresPatchHandshake: true)));
        context.Effect(protocols.Register(new WireProtocol(relayProtocol, dialect, greeting, options, router, table.ForConnection(OpcodeConnection.Relay), context.Events, sessions, versionVerified, context.Logger, context.Lifetime, requiresPatchHandshake: false)));

        var centerList = BuildCenterList(options, table);
        var patchRequest = table.C2S(options.PatchRequestName);
        var patchReply = table.S2C("gspRepGamePatch");
        context.Effect(router.Register(gameProtocol, patchRequest, (session, packet, ct) => HandlePatchRequest(session, packet, options, patchReply, centerList, context, ct)));

        context.Logger.LogInformation(
            "Protocol '{ProfileId}' mounted ({Game}, {Relay}); {Count} opcode(s) loaded; version-verified table caps at {Tracked} connections.",
            profile.Id, gameProtocol, relayProtocol, table.All.Count(), options.MaxTrackedConnections);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Encodes <c>gspCenterList</c> from the configured gates: <c>u32 count</c>, then per gate
    /// <c>u32 key, u32 subKey, u32 unknown1, u32 IPv4 (network order), u16 port</c>
    /// (kr-812 reader <c>sub_817960</c> -> <c>sub_845430</c>; 22 bytes for one gate). cn-279 reader 0x44DBC0 consumes the same fields.
    /// The opcode comes from the profile's table.
    /// </summary>
    private static (ushort Opcode, byte[] Body)? BuildCenterList(ProtocolOptions options, IOpcodeTable table)
    {
        if (options.CenterGates.Count == 0)
            return null;
        var opcode = table.S2C("gspCenterList");
        var buffer = new ArrayBufferWriter<byte>(4 + 18 * options.CenterGates.Count);
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(options.CenterGates.Count);
        foreach (var gate in options.CenterGates)
        {
            if (!System.Net.IPAddress.TryParse(gate.Address, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ConfigException($"center_gates address '{gate.Address}' is not an IPv4 address.");
            writer.WriteInt32(gate.Key);
            writer.WriteInt32(gate.SubKey);
            writer.WriteInt32(gate.Unknown1);
            writer.WriteBytes(address.GetAddressBytes());
            writer.WriteUInt16((ushort)gate.Port);
        }
        return (opcode, buffer.WrittenSpan.ToArray());
    }

    /// <summary>
    /// Whether a body reads exactly as <c>gcpFirstConnect</c>: <c>u32 ClientVersion_Major,
    /// 7 wstr, u32, 15 wstr</c>, each wstr a <c>u32</c> character count plus UTF-16LE
    /// (serializers kr-812 <c>sub_845700</c>, id-400 <c>sub_797350</c>, cn-279 <c>sub_44E390</c>). kr-812 leaves every
    /// string empty, which is the 96-byte form; id-400 fills them, so its body is longer.
    /// </summary>
    internal static bool IsFirstConnect(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        try
        {
            reader.ReadUInt32();
            for (var i = 0; i < FirstConnectLeadingStrings; i++)
                SkipString(ref reader);
            reader.ReadUInt32();
            for (var i = 0; i < FirstConnectTrailingStrings; i++)
                SkipString(ref reader);
            return reader.Remaining == 0;
        }
        catch (PacketFormatException)
        {
            return false;
        }

        static void SkipString(ref PacketReader reader)
        {
            var chars = reader.ReadUInt32();
            if (chars > FirstConnectMaxChars)
                throw new PacketFormatException($"gcpFirstConnect string of {chars} characters exceeds {FirstConnectMaxChars}", reader.Offset);
            reader.ReadBytes(checked((int)chars * 2));
        }
    }

    private static async ValueTask HandlePatchRequest(ISession session, InboundPacket packet, ProtocolOptions options, ushort patchReplyOpcode, (ushort Opcode, byte[] Body)? centerList, IPluginContext context, CancellationToken ct)
    {
        var reader = new PacketReader(packet.Payload.Span);
        var reported = reader.Remaining >= sizeof(int) ? reader.ReadInt32() : -1;
        var isHandshake = packet.Payload.Length == PatchHandshakeSize || IsFirstConnect(packet.Payload.Span);
        // The patch handshake is a 96-byte body whose first i32 is
        // ClientVersion_Major. A second connection opened for a match sends its
        // relay authentication on the same opcode number instead - 49 bytes in the
        // reference, and its first i32 is a session key, not a version. Treating
        // that as a version report closes a healthy connection, which is what a
        // live session showed: the match connection was refused with
        // "client version mismatch" and the round never began.
        if (isHandshake && reported != session.Profile.ClientVersionMajor)
        {
            context.Events.Emit(new ClientVersionMismatchEvent(session, reported, session.Profile.ClientVersionMajor));
            context.Logger.LogWarning(
                "Session {SessionId} reported client version {Reported}; expected {Expected}.",
                session.Id, reported, session.Profile.ClientVersionMajor);
            if (options.EnforceClientVersion)
            {
                await session.Connection.CloseAsync("client version mismatch").ConfigureAwait(false);
                return;
            }
        }

        // A body that is not the handshake is not this packet's business. The match
        // connection opens on the game listener with opcode 1 and a relay
        // authentication, and the row that owns relay authentication handles it
        // through the packet-received event. Answering it here with a patch reply
        // would be a reply that connection does not expect.
        if (!isHandshake)
        {
            context.Logger.LogDebug(
                "Session {SessionId} opcode 1 body of {Length} bytes is not the patch handshake; left for the owning row.",
                session.Id, packet.Payload.Length);
            return;
        }

        // gspRepGamePatch: i32 requiresPatch + u32-prefixed UTF-16LE commandLine.
        var buffer = new ArrayBufferWriter<byte>(8);
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        writer.WriteWideStringPrefixed("");
        await session.SendAsync(patchReplyOpcode, buffer.WrittenMemory, ct).ConfigureAwait(false);
        if (centerList is { } list)
            await session.SendAsync(list.Opcode, list.Body, ct).ConfigureAwait(false);
    }
}
