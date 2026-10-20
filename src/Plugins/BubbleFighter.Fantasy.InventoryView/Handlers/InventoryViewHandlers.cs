using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InventoryView.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView.Handlers;

/// <summary>
/// Inbound handling for the inventory view surface. <see cref="InventoryViewPlugin"/>
/// registers <see cref="HandleViewRequestAsync"/> for C2S 806 (policy name
/// <c>gcpReqInventoryUseItem</c>, builder kr-1256 <c>sub_80DA80</c>) when a row names it in <c>view_request</c>.
/// The client code proves no S2C reply for this request, so the handler parses strictly
/// and logs; it never answers. A malformed body throws
/// <see cref="PacketFormatException"/> and no frame is sent.
/// </summary>
public sealed class InventoryViewHandlers(ILogger logger)
{
    /// <summary>
    /// Handles C2S 806: parses the two leading <c>i32</c> values and the counted
    /// prefixed-wstring list, then logs the request fire-and-forget.
    /// </summary>
    /// <param name="session">The session the request arrived on.</param>
    /// <param name="packet">The inbound C2S 806 frame.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A completed task; nothing is sent back to the client.</returns>
    /// <exception cref="PacketFormatException">Thrown when the body does not match the builder <c>sub_80DA80</c> shape.</exception>
    public ValueTask HandleViewRequestAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = InventoryViewRequest.Parse(packet.Payload.Span);
        logger.LogInformation(
            "Session {Id} C2S 806 view request unknown0={Unknown0} unknown1={Unknown1} records={Count}; fire-and-forget, no reply is sent.",
            session.Id,
            request.Unknown0,
            request.Unknown1,
            request.Records.Count);
        return ValueTask.CompletedTask;
    }
}
