using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed class RevivalBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundInstantRevivalRequested>, IInboundMessageBinding<RoundPveRevivalRequested>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "instant_revival"))
            throw new ConfigException("instant_revival requires an audited consumable revival capability.");
        _ = tables.Resolve(build, "InstantRevivalRequest");
        _ = tables.Resolve(build, "PveRevivalRequest");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundInstantRevivalRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundPveRevivalRequested>>(this));
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(router.Register(protocol, table.C2S("gcpGameReqUseRespawnItem"), async (session, packet, ct) =>
        {
            var request = ReadInstant(tables, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Consumable revival has no typed handler.");
        }));
        context.Effect(router.Register(protocol, table.C2S("gcpPveUseRevival"), async (session, packet, ct) =>
        {
            if (packet.Payload.Length != 0) throw new PacketFormatException("PvE revival must be empty.", 0);
            if (!await messages.DispatchAsync(protocol, session, new RoundPveRevivalRequested(), ct).ConfigureAwait(false))
                throw new InvalidOperationException("PvE revival has no typed handler.");
        }));
    }

    internal static RoundInstantRevivalRequested ReadInstant(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var words = PacketCodec.ReadScalars(tables, build, "InstantRevivalRequest", body);
        var actor = (int)words["actor"];
        return new(actor, PlayRoomCodec.PlayerSeatOf(actor));
    }
}
