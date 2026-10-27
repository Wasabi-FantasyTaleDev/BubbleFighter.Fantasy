using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Validates the solo tutorial's NPC requests and acknowledges the host's native latches.</summary>
internal sealed class TutorialNpcBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<InitialTutorialNpcReported>, IMessageSender<InitialTutorialNpcAcknowledged>
{
    private readonly Dictionary<InitialTutorialNpcOperation, ushort> _replies = [];
    private ushort _reset;

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _reset = table.Find(PacketDirection.ServerToClient, "gspResetTutorialMobs_")
            ?? throw new ConfigException("The initial tutorial requires S2C gspResetTutorialMobs_.");
        Bind(InitialTutorialNpcOperation.StartWave, "gcpReqStartNewWave", "gspRepStartNewWave", "HordeWaveStartRequest");
        Bind(InitialTutorialNpcOperation.ClearWave, "gcpReqEndWave", "gspRepEndWave", "HordeWaveEndRequest");
        Bind(InitialTutorialNpcOperation.CompleteWaves, "gcpReqEndAllWave", "gspRepEndAllWave", "HordeAllWavesEndRequest");
        Bind(InitialTutorialNpcOperation.FailWaves, "gcpFailTutorialWaves_", "gspFailTutorialWaves_", "HordeAllWavesEndRequest");
        Bind(InitialTutorialNpcOperation.SpawnTable, "gcpReqWaveAllSpawnMobs", "gspRepWaveAllSpawnMobs", "HordeWaveTable");
        Bind(InitialTutorialNpcOperation.SpawnGroup, "gppCreateMobsFromSpawnGroup", "gppCreateMobsFromSpawnGroup", "TutorialSpawnGroup");
        Bind(InitialTutorialNpcOperation.SpawnMember, "gppCreateSpawnGroupMemberMob", "gppCreateSpawnGroupMemberMob", "TutorialSpawnMember");
        Bind(InitialTutorialNpcOperation.SpawnActor, "gppCreateAIObject", "gppCreateAIObject", "TutorialSpawnActor");
        Bind(InitialTutorialNpcOperation.ReplayActor, "gppHordeMobReplay", "gppHordeMobReplay", "TutorialActorReplay");
        Bind(InitialTutorialNpcOperation.AssignSpawnGroup, "gppWaveSetMobSpawnGroup", "gppWaveSetMobSpawnGroup", "TutorialWaveMobGroup");
        Bind(InitialTutorialNpcOperation.SetSpawnPointReplay, "gppSetTutorialSpawnPointReplay_", "gppSetTutorialSpawnPointReplay_", "TutorialSpawnPointReplay");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InitialTutorialNpcReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<InitialTutorialNpcAcknowledged>>(this));

        void Bind(InitialTutorialNpcOperation operation, string request, string reply, string structure)
        {
            _ = packets.Resolve(build, structure);
            _replies.Add(operation, table.Find(PacketDirection.ServerToClient, reply)
                ?? throw new ConfigException($"The initial tutorial requires S2C {reply}."));
            RoomRoutes.Bind(context, protocol, table, logger, request, body =>
            {
                _ = PacketCodec.Read(packets, build, structure, body);
                return new InitialTutorialNpcReported(operation, body.ToArray());
            });
        }
    }

    public async ValueTask SendAsync(ISession session, InitialTutorialNpcAcknowledged message, CancellationToken ct)
    {
        // kr-1256 sub_7ED5C0 clears old mobs on 908; kr-1256 sub_7B90D0 releases the host's table latch on 909.
        if (message.Operation == InitialTutorialNpcOperation.SpawnTable)
            await session.SendAsync(_reset, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        await session.SendAsync(_replies[message.Operation], message.Payload, ct).ConfigureAwait(false);
    }
}
