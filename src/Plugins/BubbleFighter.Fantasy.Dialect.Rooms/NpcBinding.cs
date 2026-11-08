using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Validates a round host's native wave-manager and spawner requests and writes their replies. Ordinary rounds
/// and the initial tutorial share this binding; which members receive a reply is the round's decision.
/// </summary>
internal sealed class NpcBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundNpcReported>, IMessageSender<RoundNpcAcknowledged>
{
    private readonly Dictionary<RoundNpcOperation, ushort> _replies = [];
    private ushort _reset;

    internal string RelayProtocol { get; init; } = string.Empty;
    internal IOpcodeTable? RelayTable { get; init; }

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        var peerTransport = false;
        if (packets.ValuesOf(build).TryGetValue("npc_peer_transport_builds", out var rawBuilds))
        {
            if (rawBuilds is not string[] builds || builds.Length == 0
                || builds.Distinct(StringComparer.Ordinal).Count() != builds.Length
                || builds.Any(candidate => !packets.Builds.Contains(candidate, StringComparer.Ordinal)))
                throw new ConfigException("npc_peer_transport_builds must list distinct known client builds.");
            peerTransport = builds.Contains(build, StringComparer.Ordinal);
        }
        if (peerTransport && !string.IsNullOrEmpty(RelayProtocol)
            && (table.Connection != OpcodeConnection.Game || RelayTable is not { Connection: OpcodeConnection.Relay } relayTable
                || relayTable.Profile.Id != table.Profile.Id))
            throw new ConfigException("NPC peer transport requires the same profile's separate game and relay opcode views.");
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        _reset = table.Find(PacketDirection.ServerToClient, "gspResetWaveMobs_")
            ?? throw new ConfigException("NPC rounds require S2C gspResetWaveMobs_.");
        Bind(RoundNpcOperation.StartWave, "gcpReqStartNewWave", "gspRepStartNewWave", "HordeWaveStartRequest");
        Bind(RoundNpcOperation.ClearWave, "gcpReqEndWave", "gspRepEndWave", "HordeWaveEndRequest");
        Bind(RoundNpcOperation.CompleteWaves, "gcpReqEndAllWave", "gspRepEndAllWave", "HordeAllWavesEndRequest");
        Bind(RoundNpcOperation.SpawnTable, "gcpReqWaveAllSpawnMobs", "gspRepWaveAllSpawnMobs", "HordeWaveTable");
        Bind(RoundNpcOperation.SpawnGroup, "gppCreateMobsFromSpawnGroup", "gppCreateMobsFromSpawnGroup", "NpcSpawnGroup");
        Bind(RoundNpcOperation.SpawnMember, "gppCreateSpawnGroupMemberMob", "gppCreateSpawnGroupMemberMob", "NpcSpawnMember");
        Bind(RoundNpcOperation.SpawnActor, "gppCreateAIObject", "gppCreateAIObject", "NpcSpawnActor");
        Bind(RoundNpcOperation.ReplayActor, "gppHordeMobReplay", "gppHordeMobReplay", "NpcActorReplay");
        Bind(RoundNpcOperation.AssignSpawnGroup, "gppWaveSetMobSpawnGroup", "gppWaveSetMobSpawnGroup", "NpcWaveMobGroup");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundNpcReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundNpcAcknowledged>>(this));

        void Bind(RoundNpcOperation operation, string request, string reply, string structure)
        {
            _ = packets.Resolve(build, structure);
            _replies.Add(operation, table.Find(PacketDirection.ServerToClient, reply)
                ?? throw new ConfigException($"NPC rounds require S2C {reply}."));
            var peerOperation = operation is RoundNpcOperation.SpawnGroup or RoundNpcOperation.SpawnMember
                or RoundNpcOperation.SpawnActor or RoundNpcOperation.ReplayActor or RoundNpcOperation.AssignSpawnGroup;
            var peerCode = peerTransport && peerOperation ? (RelayTable ?? table).S2C(reply) : (ushort?)null;
            RoundNpcReported Decode(ReadOnlySpan<byte> body)
            {
                _ = PacketCodec.Read(packets, build, structure, body);
                var payload = body.ToArray();
                return new RoundNpcReported(operation, payload)
                {
                    // kr-1256 sub_8F8C40 expands groups only on the master. kr-1256 sub_87CB30 restores the
                    // local+transport queue before each scene; concrete frames must not echo their sender.
                    PeerFeed = peerCode is { } code && operation != RoundNpcOperation.SpawnGroup
                        ? new RoundFeedSent(code, payload) { OnRelay = true } : null,
                };
            }
            RoomRoutes.Bind(context, protocol, table, logger, request, Decode);
            if (peerCode is not null && !string.IsNullOrEmpty(RelayProtocol))
            {
                var relay = RelayTable ?? throw new ConfigException("NPC peer transport requires a relay opcode table.");
                context.Effect(router.Register(RelayProtocol, relay.C2S(request),
                    async (session, packet, ct) =>
                    {
                        var message = Decode(packet.Payload.Span);
                        if (session.Connection.IsClosed || !session.TryFeature<IMatchGameLink>(out var link) || link.Game.Connection.IsClosed)
                            return;
                        if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                            throw new InvalidOperationException("No typed NPC handler is mounted for the match relay.");
                    }));
            }
        }
    }

    public async ValueTask SendAsync(ISession session, RoundNpcAcknowledged message, CancellationToken ct)
    {
        // kr-1256 sub_7ED5C0 clears old mobs on 908; kr-1256 sub_7B90D0 releases the host's table latch on 909
        // and installs the table on every other member.
        if (message.Operation == RoundNpcOperation.SpawnTable)
            await session.SendAsync(_reset, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        await session.SendAsync(_replies[message.Operation], message.Payload, ct).ConfigureAwait(false);
    }
}
