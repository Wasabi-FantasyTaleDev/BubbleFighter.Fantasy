using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Adapts audited native peer discovery and hole-punch controls without enabling a listener.</summary>
[Plugin("dialect.udp-peers", Category = "network", Names = ["en=UDP peer messages", "zh-Hans=UDP 对等消息", "zh-Hant=UDP 對等訊息", "ko=UDP 피어 메시지"], Description = "Native UDP peer setup messages.",
    Requires = [typeof(IOpcodeTableRegistry), typeof(IPacketRouter), typeof(IMessageRouter)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(IUdpPeerDialect), typeof(IInboundMessageBinding<UdpPeerTableAcknowledged>),
        typeof(IInboundMessageBinding<UdpPeerPunchReported>), typeof(IInboundMessageBinding<UdpPeerRelayMembershipChanged>)])]
public sealed class UdpPeersPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<UdpPeersOptions>();
        var identity = context.Row.ClientBuild
            ?? throw new ConfigException("dialect.udp-peers requires an explicit client_build.");
        var build = $"{identity.Region}-{identity.Major}";
        if (options.Protocol != build + "/game")
            throw new ConfigException("UDP peer controls require the owning build's game protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        if (!tables.CapabilityOf(build, "udp_peer_setup"))
            throw new ConfigException($"Native UDP peer setup is not audited for '{build}'.");
        var game = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        var relay = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(build + "/relay");
        if (game.Connection != OpcodeConnection.Game || relay.Connection != OpcodeConnection.Relay
            || game.Profile.Id != build || relay.Profile.Id != build)
            throw new ConfigException("UDP peer controls require the owning game and relay opcode tables.");
        var wire = new UdpPeerWire(tables, build, options.Protocol, game);
        context.Effect(context.Services.RegisterMany<IUdpPeerDialect>(wire));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<UdpPeerTableAcknowledged>>(wire));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<UdpPeerPunchReported>>(wire));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<UdpPeerRelayMembershipChanged>>(wire));
        var packets = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(packets.Register(options.Protocol, game.C2S("gcpReportTableSetHolePunchingEnd"),
            async (session, packet, ct) =>
            {
                PacketCodec.ReadScalars(tables, build, "UdpPeerTableAcknowledged", packet.Payload.Span);
                await messages.DispatchAsync(options.Protocol, session, new UdpPeerTableAcknowledged(), ct).ConfigureAwait(false);
            }));
        context.Effect(packets.Register(options.Protocol, game.C2S("gcpReportHolePunchingEnd"),
            async (session, packet, ct) =>
            {
                var fields = PacketCodec.ReadScalars(tables, build, "UdpPeerPunchReported", packet.Payload.Span);
                await messages.DispatchAsync(options.Protocol, session, new UdpPeerPunchReported(
                    unchecked((uint)Convert.ToInt32(fields["occupied_seats"])),
                    unchecked((uint)Convert.ToInt32(fields["connected_peers"]))), ct).ConfigureAwait(false);
            }));
        RegisterMembership("rcpReqRegisterPeer", true);
        RegisterMembership("rcpReqUnRegisterPeer", false);
        PacketLayoutContract.RequireFields(tables, build, "MovementReport", "UDP peer movement fallback", ("unknown_4", "u32"));
        RegisterMovement(options.Protocol, game, false);
        RegisterMovement(build + "/relay", relay, true);
        return ValueTask.CompletedTask;

        void RegisterMovement(string protocol, IOpcodeTable table, bool onRelay)
        {
            Bind("gppWanderLean", "MovementReport");
            if (tables.CapabilityOf(build, "udp_peer_optimized_movement"))
            {
                PacketLayoutContract.RequireFields(tables, build, "MovementSample", "UDP peer optimized fallback",
                    ("unknown_4", "u32"), ("mask", "u8"));
                Bind("gppWanderLeanOptimize", "MovementSample");
            }

            void Bind(string name, string structure) =>
                context.Effect(packets.Register(protocol, table.C2S(name), async (session, packet, ct) =>
                {
                    var fields = PacketCodec.Read(tables, build, structure, packet.Payload.Span);
                    await messages.DispatchAsync(options.Protocol, session, new RoundFeedSent(relay.S2C(name), packet.Payload.ToArray())
                    {
                        OnRelay = onRelay,
                        // cn-324 sub_464FB0 treats the raw actor word -1 as absent; validation decides delivery.
                        ReportedActors = [Convert.ToInt32(fields["unknown_4"])],
                    }, ct).ConfigureAwait(false);
                }));
        }

        void RegisterMembership(string name, bool relayed)
        {
            context.Effect(packets.Register(build + "/relay", relay.C2S(name), async (session, packet, ct) =>
            {
                var fields = PacketCodec.ReadScalars(tables, build, "UdpPeerRelayMembership", packet.Payload.Span);
                await messages.DispatchAsync(options.Protocol, session, new UdpPeerRelayMembershipChanged(
                    unchecked((uint)Convert.ToInt32(fields["peer_identity"])), relayed), ct).ConfigureAwait(false);
            }));
        }
    }
}

/// <summary>Identifies the owning game scope and packet table location.</summary>
public sealed class UdpPeersOptions
{
    /// <summary>Gets or sets the native game protocol receiving peer controls.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory containing packet structures and build differences.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}

internal sealed class UdpPeerWire : IUdpPeerDialect, IInboundMessageBinding<UdpPeerTableAcknowledged>,
    IInboundMessageBinding<UdpPeerPunchReported>, IInboundMessageBinding<UdpPeerRelayMembershipChanged>
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly IOpcodeTable _opcodes;

    public UdpPeerWire(PacketTables tables, string build, string protocol, IOpcodeTable opcodes)
    {
        _tables = tables;
        _build = build;
        Protocol = protocol;
        _opcodes = opcodes;
        ProtocolDatagramCodec.ValidateHeader(tables, build);
        PacketLayoutContract.RequireSignature(tables, build, "UdpPeerDiscoveryOffer", "UDP peer offer",
            "server_port:u16,probe_identity:u32,unknown_6:u8");
        PacketLayoutContract.RequireSignature(tables, build, "UdpPeerDiscovery", "UDP peer discovery",
            "probe_identity:u32,local_ipv4:u32,local_port:u16");
        PacketLayoutContract.RequireSignature(tables, build, "UdpPeerTableRow", "UDP peer table",
            "slot:u32,peer_identity:u32,local_ipv4:list<u32>,local_port:u16,outer_ipv4:u32,outer_port:u16");
        PacketLayoutContract.RequireSignature(tables, build, "UdpPeerPunchReported", "UDP peer report",
            "occupied_seats:u32,connected_peers:u32");
        PacketLayoutContract.RequireSignature(tables, build, "UdpPeerRelayMembership", "UDP peer membership", "peer_identity:u32");
        foreach (var structure in new[] { "UdpPeerDiscoveryStopped", "UdpPeerTableEnded", "UdpPeerTableAcknowledged",
                     "UdpPeerPunchStarted", "UdpPeerPunchFinished" })
            PacketLayoutContract.RequireEmpty(tables, build, structure, "UDP peer controls");
        foreach (var name in new[] { "gspTryHolePunching", "gspStopUdpDiscovery_", "gspTableSetHolePunching",
                     "gspUdpPeerTableEnd_", "gspUdpPeerPunchStart_", "gspUdpPeerPunchFinish_" })
            _ = opcodes.S2C(name);
    }

    public string Protocol { get; }

    public bool DiscoveryInWaitingRoom => _tables.CapabilityOf(_build, "udp_peer_discovery_in_room");

    public bool IsPeerMovement(ushort nativeOpcode) =>
        nativeOpcode == _opcodes.ForConnection(OpcodeConnection.Relay).S2C("gppWanderLean")
        || (_tables.CapabilityOf(_build, "udp_peer_optimized_movement")
            && nativeOpcode == _opcodes.ForConnection(OpcodeConnection.Relay).S2C("gppWanderLeanOptimize"));

    public UdpPeerDiscovery? ReadDiscovery(ReadOnlyMemory<byte> datagram)
    {
        var frame = ProtocolDatagramCodec.Read(_tables, _build, datagram);
        if (frame.Opcode != _opcodes.ForConnection(OpcodeConnection.Udp).C2S("upTrySendUdpToServer")) return null;
        var values = PacketCodec.ReadScalars(_tables, _build, "UdpPeerDiscovery", frame.Body.Span);
        var address = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(address, unchecked((uint)Convert.ToInt32(values["local_ipv4"])));
        return new(unchecked((uint)Convert.ToInt32(values["probe_identity"])), new IPAddress(address),
            Convert.ToUInt16(values["local_port"]));
    }

    public ValueTask OfferAsync(ISession session, ushort port, uint identity, CancellationToken ct) =>
        SendAsync(session, "gspTryHolePunching", "UdpPeerDiscoveryOffer",
            new() { ["server_port"] = port, ["probe_identity"] = identity }, ct);

    public ValueTask StopDiscoveryAsync(ISession session, CancellationToken ct) =>
        SendAsync(session, "gspStopUdpDiscovery_", "UdpPeerDiscoveryStopped", [], ct);

    public ValueTask SendCandidateAsync(ISession session, UdpPeerCandidate candidate, CancellationToken ct)
    {
        if (candidate.Slot < 0 || candidate.PeerIdentity == 0 || candidate.LocalAddresses.Count > 1
            || candidate.OuterEndPoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || candidate.OuterEndPoint.Port == 0)
            throw new ArgumentException("Invalid native UDP peer candidate.", nameof(candidate));
        return SendAsync(session, "gspTableSetHolePunching", "UdpPeerTableRow", new()
        {
            ["slot"] = candidate.Slot, ["peer_identity"] = candidate.PeerIdentity,
            ["local_ipv4"] = candidate.LocalAddresses.Select(Ipv4Word).Cast<object?>().ToArray(),
            ["local_port"] = candidate.LocalPort, ["outer_ipv4"] = Ipv4Word(candidate.OuterEndPoint.Address),
            ["outer_port"] = candidate.OuterEndPoint.Port,
        }, ct);
    }

    public ValueTask EndTableAsync(ISession session, CancellationToken ct) =>
        SendAsync(session, "gspUdpPeerTableEnd_", "UdpPeerTableEnded", [], ct);

    public ValueTask StartPunchAsync(ISession session, CancellationToken ct) =>
        SendAsync(session, "gspUdpPeerPunchStart_", "UdpPeerPunchStarted", [], ct);

    public ValueTask FinishPunchAsync(ISession session, CancellationToken ct) =>
        SendAsync(session, "gspUdpPeerPunchFinish_", "UdpPeerPunchFinished", [], ct);

    private ValueTask SendAsync(ISession session, string name, string structure, Dictionary<string, object?> values,
        CancellationToken ct) => session.SendAsync(_opcodes.S2C(name), PacketCodec.Write(_tables, _build, structure, values), ct);

    private static uint Ipv4Word(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Native UDP peer addresses must be IPv4.", nameof(address));
        return BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes());
    }
}
