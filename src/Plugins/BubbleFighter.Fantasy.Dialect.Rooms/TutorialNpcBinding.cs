using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Validates the solo tutorial's teaching-only NPC requests. Its waves and spawns are ordinary
/// <see cref="NpcBinding"/> operations, mounted beside this binding.
/// </summary>
internal sealed class TutorialNpcBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<InitialTutorialNpcReported>, IMessageSender<InitialTutorialNpcAcknowledged>
{
    private readonly Dictionary<InitialTutorialNpcOperation, ushort> _replies = [];

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        Bind(InitialTutorialNpcOperation.FailWaves, "gcpFailTutorialWaves_", "gspFailTutorialWaves_", "HordeAllWavesEndRequest");
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

    public ValueTask SendAsync(ISession session, InitialTutorialNpcAcknowledged message, CancellationToken ct) =>
        session.SendAsync(_replies[message.Operation], message.Payload, ct);
}
