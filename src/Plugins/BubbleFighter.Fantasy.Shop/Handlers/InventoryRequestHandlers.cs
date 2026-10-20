using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Handlers;

/// <summary>
/// Inbound handling for the client's own inventory-dialog open, <c>gcpEnterInventory</c>.
/// <see cref="InventoryRequestHandlers"/> is
/// registered by <see cref="ShopPlugin"/> only when
/// <c>hold_equip_until_inventory_open</c> is on, so a row that leaves the option
/// off never changes the client-visible behaviour of this opcode.
/// </summary>
/// <remarks>
/// <para>
/// Why this packet and not a timer: the kr-1256 client skips <c>gspInventoryUpdateInventorySlots</c>
/// container 2 until a my-fighter object exists (kr-1256 <c>sub_9AEB10</c>), and the weapon category
/// <c>+1632</c> that the equip gate compares against is written only by the
/// client's own <c>DlgInventory2</c> refresh (kr-1256 <c>sub_5F7C90</c> ->
/// <c>sub_5F8C30(300, 0)</c>). kr-1256 <c>sub_932E10</c> runs that refresh and then stamps
/// <c>gcpEnterInventory</c> on an empty packet, so the arrival
/// of this request is the earliest point at which a held record can pass the gate.
/// </para>
/// <para>
/// The body is always empty in the client code. A non-empty body is logged and
/// ignored; it is never parsed, because no reader shape exists for it.
/// </para>
/// </remarks>
/// <param name="hold">The held equip records, keyed by player.</param>
/// <param name="inventorySlotsUpdate">The build's <c>gspInventoryUpdateInventorySlots</c> number, from its opcode table.</param>
/// <param name="logger">The row logger.</param>
public sealed class InventoryRequestHandlers(EquipHoldStore hold, ushort inventorySlotsUpdate, ILogger logger)
{
    /// <summary>
    /// Handles <c>gcpEnterInventory</c>: flushes every held container-2 equip record of the
    /// session's player as one <c>gspInventoryUpdateInventorySlots</c> burst (container 1 empty, container 2 =
    /// the records, container 3 empty), then clears the held set so a repeated
    /// dialog open sends nothing.
    /// </summary>
    /// <param name="session">The session the request arrived on.</param>
    /// <param name="packet">The inbound frame; the body is expected to be empty.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask HandleRequestInventoryAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (packet.Payload.Length != 0)
        {
            logger.LogError(
                "Session {Id} gcpEnterInventory carries {Bytes} body byte(s); the client builder writes an empty Lambda::TcpPacket body (kr-1256 sub_932E10). The body is ignored, not parsed.",
                session.Id, packet.Payload.Length);
        }

        var user = ShopPlugin.TryGetUserNum(session);
        if (user <= 0)
        {
            logger.LogError(
                "Session {Id} gcpEnterInventory has no lobby identity (players row missing); nothing can be flushed.",
                session.Id);
            return;
        }

        if (!hold.TryTake(user, out var records))
        {
            logger.LogInformation(
                "Session {Id} gcpEnterInventory: player {User} holds no equip record; nothing is sent.",
                session.Id, user);
            return;
        }

        var body = ShopWire.InventoryUpdateSlotsEquipHoldEnvelope(records);
        await session.SendAsync(inventorySlotsUpdate, body, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Session {Id} gcpEnterInventory: flushed {Count} held container-2 equip record(s) for player {User} as one gspInventoryUpdateInventorySlots ({Bytes} B on the wire, {Plain} B plain).",
            session.Id, records.Count, user, body.Length, ShopWire.EquipHoldSingleRecordBytes + ((records.Count - 1) * ShopWire.ArticleSlotBytes));
    }
}
