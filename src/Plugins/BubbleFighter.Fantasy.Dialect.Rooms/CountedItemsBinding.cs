using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Serves the <c>counted_items</c> capability (its used-notification is additionally gated by
/// <c>direct_supply_effects</c>): the C2S <c>gcpPveUseCountItem</c> slot (kr-812 <c>sub_80A910</c>
/// reads the one word; cn-717, id-400, id-1809, cn-706 and cn-324 match; cn-324 <c>sub_53CAB0</c>
/// passes the UI slot to <c>sub_467720</c>, which stamps the request), the prepared 16-byte slot list
/// (S2C <c>gspPveItemInfo</c>, kr-812 <c>sub_80B420</c> and matches) and the three-word used notice
/// (S2C <c>gspPveItemUsed</c>, kr-812 <c>sub_9D95D0</c>; cn-324 <c>sub_46FED0</c> resolves the first
/// word as a player seat and checks local control) — packets/pve-counted-items.yaml. cn-279 has no
/// such class.
/// </summary>
internal sealed class CountedItemsBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundCountedItemUseRequested>, IMessageSender<RoundCountedItemsPrepared>, IMessageSender<RoundCountedItemUsed>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        // The request slot this handler casts, the prepared list it builds and the element fields it writes.
        PacketLayoutContract.RequireFields(tables, build, "PveItemUseRequest", "Counted items", ("item_slot", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "PveItemsPrepared", "Counted items", ("items", "list<PveItemSlot>"));
        PacketLayoutContract.RequireFields(tables, build, "PveItemSlot", "Counted items", ("kind", "u32"), ("item_slot", "u32"));
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
        // The used seat and kind this sender writes; the supply row alone reaches this check without Mount.
        PacketLayoutContract.RequireFields(tables, build, "PveItemUsed", "Counted items", ("seat", "u32"), ("kind", "u32"));
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
