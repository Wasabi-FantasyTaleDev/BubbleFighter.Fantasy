using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView.Handlers;

/// <summary>
/// Inbound handling for the inventory view surface. <see cref="InventoryViewPlugin"/> registers
/// <see cref="HandleViewRequestAsync"/> for the C2S name a row puts in <c>view_request</c>
/// (kr-1256 C2S 806 <c>gcpReqInventoryUseItem</c>, builder <c>kr-1256 sub_80DA80</c>). The body is
/// the build's <c>InventoryUseRequest</c> row of <c>packets/inventory-use.yaml</c>: item id,
/// quantity and the parameters field, a counted UTF-16 string list where the build's table states
/// one (<c>kr-1256 sub_80DA80</c>, list writer <c>kr-1256 sub_429D10</c>) and a single string on
/// the legacy builds (<c>kr-812 sub_8B9490</c>). The client code proves no S2C reply for this
/// request, so the handler reads through <see cref="PacketCodec"/> and logs fire-and-forget; it
/// never answers. A malformed body throws <see cref="PacketFormatException"/> and no frame is sent.
/// </summary>
public sealed class InventoryViewHandlers(ILogger logger, PacketTables tables, string build)
{
    // kr-1256 sub_1126430 accepts up to 0x4000 UTF-16 code units. Empty strings still cost
    // four bytes each after the two words and count; retain that frame-derived list bound.
    private static readonly PacketReadLimits RequestLimits = new(
        MaxStringCharacters: 0x4000, MaxCollectionElements: (ushort.MaxValue - 12) / 4);

    /// <summary>
    /// Handles the view request: reads the build's whole <c>InventoryUseRequest</c> layout through
    /// the packet tables, then logs the request fire-and-forget.
    /// </summary>
    /// <param name="session">The session the request arrived on.</param>
    /// <param name="packet">The inbound request frame.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A completed task; nothing is sent back to the client.</returns>
    /// <exception cref="PacketFormatException">Thrown when the body is not the build's whole <c>InventoryUseRequest</c> layout, or a string exceeds the original 0x4000-character client writer bound (<c>kr-1256 sub_1126430</c>).</exception>
    public ValueTask HandleViewRequestAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var fields = PacketCodec.Read(tables, build, "InventoryUseRequest", packet.Payload.Span, RequestLimits);
        var itemId = Convert.ToInt32(fields["item_id"], CultureInfo.InvariantCulture);
        var quantity = Convert.ToInt32(fields["quantity"], CultureInfo.InvariantCulture);
        // The table decides the parameters shape: the counted list of kr-1256 sub_80DA80 or the
        // single string of kr-812 sub_8B9490; activation rejects any other type before this line.
        var parameters = fields["parameters"] switch
        {
            object?[] list => list.Length,
            string single => single.Length == 0 ? 0 : 1,
            _ => throw new InvalidOperationException($"The InventoryUseRequest.parameters of '{build}' decoded as neither a string nor a list."),
        };
        logger.LogInformation(
            "Session {Id} view request item={Item} quantity={Quantity} parameters={Parameters}; fire-and-forget, no reply is sent.",
            session.Id,
            itemId,
            quantity,
            parameters);
        return ValueTask.CompletedTask;
    }
}
