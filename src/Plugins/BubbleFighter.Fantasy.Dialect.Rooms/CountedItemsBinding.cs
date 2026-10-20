using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed class CountedItemsBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundCountedItemUseRequested>, IMessageSender<RoundCountedItemsPrepared>, IMessageSender<RoundCountedItemUsed>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        foreach (var name in new[] { "PveItemSlot", "PveItemsPrepared", "PveItemUseRequest", "PveItemUsed" })
            _ = tables.Resolve(build, name);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundCountedItemUseRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundCountedItemsPrepared>>(this));
        MountEffect(context);
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, table.C2S("gcpPveUseCountItem"), async (session, packet, ct) =>
        {
            var values = PacketCodec.ReadScalars(tables, build, "PveItemUseRequest", packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, new RoundCountedItemUseRequested((int)values["item_slot"]), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Counted cooperative item use has no typed handler.");
        }));
    }

    internal void MountEffect(IPluginContext context)
    {
        if (!tables.CapabilityOf(build, "direct_supply_effects"))
            throw new ConfigException("Direct supply effects require an audited native effect consumer.");
        _ = tables.Resolve(build, "PveItemUsed");
        _ = table.S2C("gspPveItemUsed");
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundCountedItemUsed>>(this));
    }

    public ValueTask SendAsync(ISession session, RoundCountedItemsPrepared message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspPveItemInfo"), EncodePrepared(tables, build, message.Kinds), ct);

    public ValueTask SendAsync(ISession session, RoundCountedItemUsed message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspPveItemUsed"), PacketCodec.Write(tables, build, "PveItemUsed",
            new Dictionary<string, object?> { ["seat"] = message.Seat, ["kind"] = message.Kind }), ct);

    internal static byte[] EncodePrepared(PacketTables tables, string build, IReadOnlyList<int> kinds)
    {
        // kr-812 sub_7F1120 queries 1..3; cn-324 sub_6E2B40 queries 1..5. Slot zero is reserved.
        var items = new List<Dictionary<string, object?>> { new() };
        for (var slot = 0; slot < kinds.Count; slot++)
            items.Add(new() { ["kind"] = kinds[slot], ["item_slot"] = slot + 1 });
        return PacketCodec.Write(tables, build, "PveItemsPrepared", new Dictionary<string, object?>
        {
            ["items"] = items,
        });
    }
}
