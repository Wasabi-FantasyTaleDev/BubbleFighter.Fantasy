using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Adapts the native expired-item action: <c>gcpReqInventoryThrowItem</c> in and <c>gspRepInventoryThrowItem</c>
/// out, both through the packet tables. The request carries <c>item_id:u32</c> (readers kr-812 sub_9857B0,
/// kr-1256 sub_8136F0, cn-774 sub_9B4BC0, tw-454 sub_C12C40) and the reply echoes it with
/// <c>result:u32</c> (readers kr-812 sub_6DB1B0, kr-1256 sub_9B6250, cn-774 sub_BB4930, tw-454 sub_A92020).
/// </summary>
internal sealed class InventoryActionsBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<InventoryThrowRequested>, IMessageSender<InventoryThrowReplied>
{
    private ushort _reply;

    public string Protocol => protocol;

    /// <summary>
    /// Registers the request route and the reply sender on the row's scope. A table that names neither packet
    /// fails here, so a build without the audited pair keeps no half-mounted route.
    /// </summary>
    /// <param name="context">The owning row's context.</param>
    /// <param name="messages">The router the typed request dispatches through.</param>
    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        // The request supplies item_id and the reply echoes it with the result; both are cast or sized as u32.
        PacketLayoutContract.RequireFields(tables, build, "InventoryActionRequest", "Inventory throw",
            ("item_id", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "InventoryThrowReply", "Inventory throw",
            ("item_id", "u32"), ("result", "u32"));
        var request = opcodes.C2S("gcpReqInventoryThrowItem");
        _reply = opcodes.S2C("gspRepInventoryThrowItem");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InventoryThrowRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<InventoryThrowReplied>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, request, async (session, packet, ct) =>
        {
            var fields = PacketCodec.ReadScalars(tables, build, "InventoryActionRequest", packet.Payload.Span);
            var message = new InventoryThrowRequested((int)fields["item_id"]);
            if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Inventory throw requests have no typed handler.");
        }));
    }

    /// <summary>Writes the eight-byte identity and result reply through the tables.</summary>
    public ValueTask SendAsync(ISession session, InventoryThrowReplied message, CancellationToken ct) =>
        session.SendAsync(_reply, PacketCodec.Write(tables, build, "InventoryThrowReply",
            new Dictionary<string, object?> { ["item_id"] = message.ItemId, ["result"] = message.Result }), ct);
}
