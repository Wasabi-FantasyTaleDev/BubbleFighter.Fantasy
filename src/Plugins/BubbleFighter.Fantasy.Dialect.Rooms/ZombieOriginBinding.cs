using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Maps the controlled fighter's request and the private weighted draw on the game connection.</summary>
internal sealed class ZombieOriginBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IInboundMessageBinding<RoundZombieOriginPrototypeRequested>, IMessageSender<RoundZombieOriginPrototypeRoll>
{
    public string Protocol => protocol;
    private readonly ushort _reply = table.S2C("gspZombieOriginPrototypeRoll_");

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireSignature(packets, build, "ZombieOriginPrototypeRequest", "Zombie G", "character_kind:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ZombieOriginPrototypeRoll", "Zombie G", "roll:u32");
        RoomRoutes.Bind(context, protocol, table, context.Logger, "gcpZombieOriginPrototypeRequest_", body =>
            new RoundZombieOriginPrototypeRequested((int)PacketCodec.ReadScalars(packets, build, "ZombieOriginPrototypeRequest", body)["character_kind"]));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundZombieOriginPrototypeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundZombieOriginPrototypeRoll>>(this));
    }

    public ValueTask SendAsync(ISession session, RoundZombieOriginPrototypeRoll message, CancellationToken ct)
    {
        if ((uint)message.Roll >= 10_000) throw new ArgumentOutOfRangeException(nameof(message));
        var body = PacketCodec.Write(packets, build, "ZombieOriginPrototypeRoll", new Dictionary<string, object?> { ["roll"] = message.Roll });
        return session.SendAsync(_reply, body, ct);
    }
}
