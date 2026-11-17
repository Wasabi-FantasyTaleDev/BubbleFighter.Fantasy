using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.CashInbox;

/// <summary>Client result status communicated in <c>gspRepPickup</c>.</summary>
public enum CashInboxPickupStatus
{
    /// <summary>The cash inbox item was moved into the regular inventory (wire code 1).</summary>
    Success = 0,

    /// <summary>The requested slot was missing, expired, already picked up, or could not be processed (wire code 2).</summary>
    Failed = 1,

    /// <summary>The requested slot belongs to another account (wire code 7).</summary>
    AccountMismatch = 2,

    /// <summary>The player already owns the item or cannot extend its quantity/duration further (wire code 8).</summary>
    AlreadyOwned = 3,
}

/// <summary>The client requests a full refresh of its cash inbox slots (<c>gcpReqCashInven</c>).</summary>
public sealed record CashInboxOpenRequested : IMessage;

/// <summary>The client asks to pick up one cash inbox slot into its regular inventory (<c>gcpReqPickup</c>).</summary>
/// <param name="CashSlotId">Selected wire <c>cash_slot_key</c>.</param>
public sealed record CashInboxPickupRequested(long CashSlotId) : IMessage;

/// <summary>The client reports closing the cash/gift inbox window (<c>gcpReqCloseGiftBox</c>).</summary>
public sealed record CashInboxCloseRequested : IMessage;

/// <summary>Publishes the complete active cash inbox snapshot (<c>gspCashInvenUpdateAllInventorySlot</c>).</summary>
/// <param name="Entries">Ordered active cash inbox entries to send as the replace-all list.</param>
public sealed record CashInventoryPublished(IReadOnlyList<CashInboxEntryRecord> Entries) : IMessage;

/// <summary>Publishes the result of picking up one cash inbox entry (<c>gspRepPickup</c>).</summary>
/// <param name="CashSlotId">Slot key targeted by the client's pickup request.</param>
/// <param name="Status">Mapped domain result turned into the client's reply code.</param>
/// <param name="PresentationCode">Optional secondary numeric field on modern clients (zero by default).</param>
/// <param name="PresentationText">Optional modern presentation string (empty by default).</param>
public sealed record CashInboxPickupReplied(
    long CashSlotId,
    CashInboxPickupStatus Status,
    int PresentationCode = 0,
    string PresentationText = "") : IMessage;

/// <summary>
/// Event emitted while publishing the login inventory burst so the owning shop plugin can supply active cash inbox
/// entries for the matching protocol without coupling the dialect plugin to persistence or shop services.
/// </summary>
/// <param name="Protocol">Protocol instance publishing the login cash inventory frame.</param>
/// <param name="UserNum">Authenticated player identifier.</param>
public sealed record CashInboxSnapshotRequested(string Protocol, long UserNum) : IEvent;
