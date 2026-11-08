namespace BubbleFighter.Fantasy.Contracts.Couples;

/// <summary>An account identity resolved by the server inside one player-data authority.</summary>
/// <param name="Authority">The configured authority owning the player data; compared ordinally.</param>
/// <param name="UserNum">The positive retained account number, independent of a client nickname.</param>
public sealed record CoupleAccount(string Authority, long UserNum);

/// <summary>A server-created invitation generation with its bounded presentation text and validity interval.</summary>
/// <param name="Id">A fresh server-generated token; native clients do not choose this value.</param>
/// <param name="Recipient">The account resolved by the server from the requested target.</param>
/// <param name="Message">The invitation text, subject to the store's configured bound.</param>
/// <param name="CreatedAt">The authoritative creation time.</param>
/// <param name="ExpiresAt">The exclusive end of the authoritative acceptance interval.</param>
public sealed record CoupleInvitationDraft(
    Guid Id, CoupleAccount Recipient, string Message, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>An invitation tied to both accounts and one immutable generation.</summary>
/// <param name="Id">The server-created invitation identity.</param>
/// <param name="Sender">The authenticated account that created this invitation.</param>
/// <param name="Recipient">The only account allowed to accept or reject it.</param>
/// <param name="Message">The original invitation text.</param>
/// <param name="CreatedAt">The authoritative creation time.</param>
/// <param name="ExpiresAt">The exclusive end of the acceptance interval.</param>
public sealed record CoupleInvitation(
    Guid Id, CoupleAccount Sender, CoupleAccount Recipient, string Message,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>A mutually accepted relationship; both account lookups return the same immutable pair.</summary>
/// <param name="Id">The invitation generation that established the pair.</param>
/// <param name="First">The account that sent the accepted invitation.</param>
/// <param name="Second">The account that accepted the invitation.</param>
/// <param name="CreatedAt">The authoritative acceptance time.</param>
public sealed record CoupleRelationship(Guid Id, CoupleAccount First, CoupleAccount Second, DateTimeOffset CreatedAt);

/// <summary>The storage outcome, independent of any client's numeric result code.</summary>
public enum CoupleStoreResult
{
    /// <summary>The invitation is pending and can still be answered.</summary>
    Pending,
    /// <summary>Both memberships were committed together.</summary>
    Accepted,
    /// <summary>The intended recipient rejected the invitation.</summary>
    Rejected,
    /// <summary>The authenticated sender cancelled the invitation.</summary>
    Cancelled,
    /// <summary>The authoritative acceptance interval ended.</summary>
    Expired,
    /// <summary>At least one participant already belongs to a relationship.</summary>
    AlreadyPaired,
    /// <summary>The generation no longer exists in the bounded store.</summary>
    NotFound,
    /// <summary>The authenticated actor does not own the requested operation.</summary>
    Forbidden,
    /// <summary>The supplied generation conflicts with retained state.</summary>
    Conflict,
    /// <summary>The operation would exceed a configured storage bound.</summary>
    CapacityReached,
    /// <summary>Both memberships of the expected relationship were removed together.</summary>
    BrokenUp,
}

/// <summary>An atomic mutation result, with only state the authenticated actor may see.</summary>
/// <param name="Result">The semantic outcome, translated by the dialect only after native verification.</param>
/// <param name="Invitation">The affected invitation, or null for an unauthorized or unknown generation.</param>
/// <param name="Relationship">The current relationship established by the invitation, when still active.</param>
public sealed record CoupleStoreOutcome(
    CoupleStoreResult Result, CoupleInvitation? Invitation = null, CoupleRelationship? Relationship = null);

/// <summary>
/// Atomic relationship storage. Actors and targets must be resolved from authenticated server state.
/// Accepting locks both memberships and the pending generation in one transaction; accepting, cancelling,
/// rejecting and breaking up never modify an account named only by an untrusted client field.
/// This contract stores relationships only: a game handler requiring ring consumption must use a provider
/// that also commits its proven inventory rules atomically, and must not treat this contract alone as that guarantee.
/// Completed outcomes may have bounded retention; an evicted receipt returns NotFound. A caller must never reuse
/// an invitation token, including after a breakup, expiry, cancellation or receipt eviction.
/// </summary>
public interface ICoupleStore
{
    /// <summary>Gets whether accepted mutations survive a process restart.</summary>
    bool PersistsState { get; }

    /// <summary>Reads an account's current relationship without allocating a membership.</summary>
    ValueTask<CoupleRelationship?> FindRelationshipAsync(CoupleAccount member, CancellationToken cancellationToken);

    /// <summary>Reads incoming invitations that are still valid at the authoritative time, in creation order.</summary>
    ValueTask<IReadOnlyList<CoupleInvitation>> ListIncomingAsync(
        CoupleAccount recipient, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records one server-created generation. Both participants must share the same authority and be distinct.
    /// Repeating the same generation and contents returns retained state; changing its participants or contents is a conflict.
    /// </summary>
    ValueTask<CoupleStoreOutcome> CreateInvitationAsync(
        CoupleAccount sender, CoupleInvitationDraft invitation, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an invitation addressed to the authenticated recipient. Accepting atomically requires both accounts
    /// to remain unpaired. Repeated answers return the retained outcome without creating a second relationship.
    /// </summary>
    ValueTask<CoupleStoreOutcome> AnswerInvitationAsync(
        CoupleAccount recipient, Guid invitationId, bool accept, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Cancels only the authenticated sender's pending generation, preserving any later invitation.</summary>
    ValueTask<CoupleStoreOutcome> CancelInvitationAsync(
        CoupleAccount sender, Guid invitationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Removes both memberships only when the actor belongs to the expected relationship generation.
    /// A stale request cannot dissolve a subsequent relationship with the same or a different partner.
    /// </summary>
    ValueTask<CoupleStoreOutcome> BreakUpAsync(
        CoupleAccount member, Guid relationshipId, CancellationToken cancellationToken);
}
