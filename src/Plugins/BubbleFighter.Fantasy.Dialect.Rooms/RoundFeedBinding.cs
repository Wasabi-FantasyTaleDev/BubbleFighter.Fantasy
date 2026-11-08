using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The round's gameplay feed for every build: the in-round actions the server passes between players. How a build's
/// feed is read is build data, not family code. Typed routes (<c>round_feed</c> in <c>packets/builds.yaml</c>) are
/// layout-checked against their <c>packets/round-feed.yaml</c> structures and may report actors and life events;
/// the damager-spawn family is relayed byte-identically; and a build with <c>round_feed_passthrough</c> passes every
/// other peer frame (numbers from 0x8000) on unread, from the game connection or the match relay. Who may act for
/// which character is decided once, by the round flow, for all of them.
/// </summary>
internal sealed partial class RoundFeedBinding(string protocol, string relayProtocol, IOpcodeTable game, IOpcodeTable relay, PacketTables packets, string build)
    : IInboundMessageBinding<RoundFeedSent>, IMessageSender<RoundFeedReceived>, IInboundMessageBinding<RoundLifeReported>,
      IInboundMessageBinding<RoundTauntReported>
{
    private const ushort FeedBase = 0x8000;

    // The damager-spawn family: each frame mints a combat damager key in the receiving client's per-slot counters,
    // so the spawns have to be relayed byte-identically and in order for any relayed 38283 hit to resolve on the
    // peers. Their layouts are packets/damagers.yaml; the profile supplies the build-specific numbers and marks
    // each row for both connections.
    private static readonly string[] DamagerSpawnNames =
    [
        "gppSplashDamager", "gppMobDropBombOnDead", "gppDropHumilPrevItem", "gppFireBullet3",
        "gppStormDamager", "gppSplashDamagerByDamager", "gppStormDamagerItemId", "gppNearDamager",
    ];

    private UdpMovementTransport? _udp;
    private ushort _udpFull;
    private ushort _udpOptimized;

    /// <summary>Maps one damager's game and relay inputs to its relay peer output.</summary>
    private sealed record DamagerRoute(ushort GameInbound, ushort RelayInbound, ushort RelayOutbound);

    public string Protocol => protocol;

    /// <summary>Gets whether a client's direct item effect (gppUseItemDirect) also returns to its sender.</summary>
    internal bool EchoDirectItemEffects { get; init; }

    /// <summary>Gets whether a seat's peer frames may arrive on its match relay connection.</summary>
    internal bool AcceptRelayFrames { get; init; }

    /// <summary>Gets the taunt-end request whose second word moves a dead player's wait, or null.</summary>
    internal ushort? TauntEnd { get; init; }

    /// <summary>Gets the authenticated UDP movement protocol, or empty when the build moves over TCP only.</summary>
    internal string UdpProtocol { get; init; } = string.Empty;

    /// <summary>Gets the UDP movement keepalive period.</summary>
    internal int UdpKeepaliveMs { get; init; } = 1000;

    /// <summary>Mounts the feed the build's data states; a build that states neither routes nor pass-through is refused.</summary>
    internal void Mount(IPluginContext context)
    {
        var passthrough = packets.ValuesOf(build).GetValueOrDefault("round_feed_passthrough") is "true";
        var routes = RoundFeedRoute.Load(packets, build, game, relay);
        if (routes.Length == 0 && !passthrough)
            throw new ConfigException($"{build} states neither round_feed routes nor round_feed_passthrough.");
        // Each typed route validates its own reporting identities; legacy death reports have no reset confirmation.
        // A pass-through build already carries the damager family unread and in order with the rest of its peer
        // frames; only a routed build names each spawn.
        var damagerSpawns = passthrough ? [] : ResolveDamagerSpawns();
        var gameInputs = damagerSpawns.Select(route => route.GameInbound).ToHashSet();
        var relayInputs = damagerSpawns.Select(route => route.RelayInbound).ToHashSet();
        var relayOutputs = damagerSpawns.Select(route => route.RelayOutbound).ToHashSet();
        if (gameInputs.Count != damagerSpawns.Length || relayInputs.Count != damagerSpawns.Length
            || relayOutputs.Count != damagerSpawns.Length
            || routes.Any(route => gameInputs.Contains(route.GameInbound) || relayInputs.Contains(route.RelayInbound)
            || relayOutputs.Contains(route.Outbound)))
            throw new ConfigException("Round feed action routes collide with the damager family.");
        var services = context.Services;
        var router = services.Get<IPacketRouter>();
        var messages = services.Get<IMessageRouter>();
        RoundPingBinding.Mount(context, protocol, relayProtocol, game, relay, packets, build);
        if (!string.IsNullOrWhiteSpace(UdpProtocol))
            MountUdp(context, messages);
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFeedSent>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFeedReceived>>(this));
        MountCriticalTransitions(context, router, messages);
        if (routes.Length > 0)
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundLifeReported>>(this));
        if (TauntEnd is not null)
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundTauntReported>>(this));
        foreach (var route in routes)
        {
            PacketHandler handler = async (session, packet, ct) =>
            {
                var decoded = route.Read(packets, build, packet.Payload.Span);
                // Pass-through rounds use game connections unless the shared round selected its native relay.
                var feed = decoded.Feed with { OnRelay = !passthrough };
                if (!await messages.DispatchAsync(protocol, session, feed, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No round feed handler is installed.");
                if (decoded.Life is { } report)
                    await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false);
            };
            context.Effect(router.Register(protocol, route.GameInbound, handler));
            context.Effect(router.Register(relayProtocol, route.RelayInbound, handler));
        }
        // A relayed hit resolves only against a damager the receiving client minted from the same spawn frames,
        // so every spawn goes on unchanged and in order.
        foreach (var route in damagerSpawns)
        {
            // Game and relay C2S spaces may differ. Both inputs become the relay S2C form consumed by peers.
            Register(context, route.GameInbound, route.RelayOutbound, relay: false, game: true);
            Register(context, route.RelayInbound, route.RelayOutbound, relay: true, game: false);
        }
        if (passthrough)
            MountPassthrough(context, router, messages);
        // cn-774 sub_B58DB0 / tw-454 sub_A78CC0 send UDP envelopes, not TCP gameplay frames.
        // TCP motion belongs to RoundMovementBinding; no unproven transport is registered here.
    }

    /// <summary>A feed packet goes out under the number the route selected, over UDP where a movement path is bound.</summary>
    public ValueTask SendAsync(ISession session, RoundFeedReceived message, CancellationToken ct) =>
        _udp is not null && (message.Code == _udpFull || message.Code == _udpOptimized)
            ? _udp.SendAsync(session, message.Code, message.Payload, ct)
            : session.SendAsync(message.Code, message.Payload, ct);

    // Peer frames no route or row claims arrive on the game connection when no relay is offered (demux kr-812
    // sub_80D4C0) and go on unread. Awaiting the dispatch preserves arrival order and applies transport backpressure
    // without accumulating detached forwarding tasks.
    private void MountPassthrough(IPluginContext context, IPacketRouter router, IMessageRouter messages)
    {
        var echoSender = packets.CapabilityOf(build, "round_feed_echo_sender");
        ushort? directItem = EchoDirectItemEffects ? game.C2S("gppUseItemDirect") : null;
        context.Effect(context.Events.OnAsync<PacketReceivedEvent>(async (received, ct) =>
        {
            if (received.HasHandler || received.ProtocolName != protocol || received.Packet.Opcode < FeedBase)
                return;
            await messages.DispatchAsync(protocol, received.Session,
                new RoundFeedSent(received.Packet.Opcode, received.Packet.Payload.ToArray())
                {
                    // Only a build whose sender does not queue its own copy states the echo (packets/builds.yaml).
                    IncludeSender = echoSender || received.Packet.Opcode == directItem,
                }, ct).ConfigureAwait(false);
            // gppHumiliationEnd (cn-324 sender sub_46B190): u32 the taunter's handle, u32 the dead player's. It is
            // passed on like the rest of the feed and also moves the dead player's wait.
            if (received.Packet.Opcode == TauntEnd && received.Packet.Payload.Length == 8)
            {
                await messages.DispatchAsync(protocol, received.Session,
                    new RoundTauntReported(BitConverter.ToInt32(received.Packet.Payload.Span[4..])), ct).ConfigureAwait(false);
            }
        }));
        // With the match relay selected the client sends its gpp frames on the relay (cn-324 sub_6F7F10, kr-812
        // sub_80D4C0); the relay's own rows claim only its handshake and seeds, so every other peer frame is handed to
        // the game protocol as if the seat's game connection carried it, keeping one stream for the observer above.
        if (!AcceptRelayFrames)
            return;
        context.Effect(context.Events.OnAsync<PacketReceivedEvent>(async (received, ct) =>
        {
            if (received.HasHandler || received.ProtocolName != relayProtocol || received.Packet.Opcode < FeedBase
                || !received.Session.TryFeature<IMatchGameLink>(out var link) || link.Game.Connection.IsClosed)
                return;
            await router.DispatchAsync(protocol, link.Game, received.Packet, ct).ConfigureAwait(false);
        }));
    }

    private void MountUdp(IPluginContext context, IMessageRouter messages)
    {
        if (!AcceptRelayFrames)
            throw new ConfigException("UDP movement on the round feed requires the match relay (select_match_relay).");
        _udpFull = relay.S2C("gppWanderLean");
        _udpOptimized = relay.S2C("gppWanderLeanOptimize");
        PacketLayoutContract.RequireFields(packets, build, "MovementReport", "UDP feed actor", ("unknown_4", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "MovementSample", "UDP feed actor", ("unknown_4", "u32"));
        _udp = UdpMovementTransport.Mount(context, UdpProtocol, relayProtocol, packets, build, relay, UdpKeepaliveMs,
            async (session, opcode, body, ct) =>
            {
                var structure = opcode == relay.C2S("gppWanderLean") ? "MovementReport" : "MovementSample";
                var values = PacketCodec.Read(packets, build, structure, body.Span);
                await messages.DispatchAsync(protocol, session, new RoundFeedSent(opcode, body.ToArray())
                {
                    OnRelay = true,
                    // kr-812 sub_8108C0 copies the actor word unchanged into both UDP movement envelopes.
                    ReportedActors = [Convert.ToInt32(values["unknown_4"])],
                }, ct).ConfigureAwait(false);
            });
    }

    private DamagerRoute[] ResolveDamagerSpawns()
    {
        var present = DamagerSpawnNames.Where(name => game.Find(PacketDirection.ClientToServer, name) is not null
            || relay.Find(PacketDirection.ClientToServer, name) is not null
            || relay.Find(PacketDirection.ServerToClient, name) is not null).ToArray();
        if (present.Length == 0)
            return [];
        if (present.Length != DamagerSpawnNames.Length)
            throw new ConfigException($"Build '{build}' declares only {present.Length} of the {DamagerSpawnNames.Length} damager spawn routes.");

        var routes = new List<DamagerRoute>(DamagerSpawnNames.Length);
        foreach (var name in DamagerSpawnNames)
        {
            routes.Add(new(game.C2S(name), relay.C2S(name), relay.S2C(name)));
        }
        return [.. routes];
    }

    private void Register(IPluginContext context, ushort inbound, ushort outbound, bool relay, bool game)
    {
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        PacketHandler handler = async (session, packet, ct) =>
        {
            var message = new RoundFeedSent(outbound, packet.Payload.ToArray()) { OnRelay = true };
            if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No round feed handler is installed.");
        };
        if (game)
            context.Effect(router.Register(protocol, inbound, handler));
        if (relay)
            context.Effect(router.Register(relayProtocol, inbound, handler));
    }
}
