using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The modern typed round's gameplay feed: the in-round actions and damager spawns the
/// server passes unchanged between players. Each action's body is layout-checked against its
/// <c>packets/round-feed.yaml</c> structure before it is relayed; the frames arrive on the game or the match relay
/// connection and go to the other players' relay connections, where the round flow subscribes on the game protocol,
/// so relay frames are dispatched there too (the room registry knows both connections of a seat).
/// </summary>
internal sealed class RoundFeedBinding(string protocol, string relayProtocol, IOpcodeTable game, IOpcodeTable relay, PacketTables packets, string build)
    : IInboundMessageBinding<RoundFeedSent>, IMessageSender<RoundFeedReceived>, IInboundMessageBinding<RoundLifeReported>
{
    // The damager-spawn family: each frame mints a combat damager key in the receiving client's per-slot counters,
    // so the spawns have to be relayed byte-identically and in order for any relayed 38283 hit to resolve on the
    // peers. Their layouts are packets/damagers.yaml; the profile supplies the build-specific numbers and marks
    // each row for both connections.
    private static readonly string[] DamagerSpawnNames =
    [
        "gppSplashDamager", "gppMobDropBombOnDead", "gppDropHumilPrevItem", "gppFireBullet3",
        "gppStormDamager", "gppSplashDamagerByDamager", "gppStormDamagerItemId", "gppNearDamager",
    ];

    /// <summary>Maps one damager's game and relay inputs to its relay peer output.</summary>
    private sealed record DamagerRoute(ushort GameInbound, ushort RelayInbound, ushort RelayOutbound);

    public string Protocol => protocol;

    /// <summary>Mounts the feed; every action structure must exist in this build's tables.</summary>
    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireFields(packets, build, "RoundResetConfirmation", "Round feed ownership", ("actor", "u32"));
        var routes = RoundFeedRoute.Load(packets, build, game, relay);
        var damagerSpawns = ResolveDamagerSpawns();
        var gameInputs = damagerSpawns.Select(route => route.GameInbound).ToHashSet();
        var relayInputs = damagerSpawns.Select(route => route.RelayInbound).ToHashSet();
        var relayOutputs = damagerSpawns.Select(route => route.RelayOutbound).ToHashSet();
        if (gameInputs.Count != damagerSpawns.Length || relayInputs.Count != damagerSpawns.Length
            || relayOutputs.Count != damagerSpawns.Length
            || routes.Any(route => gameInputs.Contains(route.GameInbound) || relayInputs.Contains(route.RelayInbound)
            || relayOutputs.Contains(route.Outbound)))
            throw new ConfigException("Round feed action routes collide with the damager family.");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundFeedSent>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundFeedReceived>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundLifeReported>>(this));
        foreach (var route in routes)
        {
            var router = context.Services.Get<IPacketRouter>();
            var messages = context.Services.Get<IMessageRouter>();
            PacketHandler handler = async (session, packet, ct) =>
            {
                var decoded = route.Read(packets, build, packet.Payload.Span);
                if (!await messages.DispatchAsync(protocol, session, decoded.Feed, ct).ConfigureAwait(false))
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
        // cn-774 sub_B58DB0 / tw-454 sub_A78CC0 send UDP envelopes, not TCP gameplay frames.
        // TCP motion belongs to RoundMovementBinding; no unproven transport is registered here.
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

    /// <summary>Sends the native outbound opcode selected by the checked route.</summary>
    public ValueTask SendAsync(ISession session, RoundFeedReceived message, CancellationToken ct) =>
        session.SendAsync(message.Code, message.Payload, ct);

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
