namespace BubbleFighter.Fantasy.Contracts.Messenger;

/// <summary>One stored friend relation as seen by its owner.</summary>
/// <param name="Friend">The other account.</param>
/// <param name="CategoryId">Owner's category holding the friend; zero for the root.</param>
/// <param name="Blocked">Whether the owner blocked the friend.</param>
/// <param name="BlockedByFriend">Whether the friend blocked the owner (the reverse relation's flag).</param>
public sealed record MessengerRelation(MessengerUser Friend, int CategoryId, bool Blocked, bool BlockedByFriend);

/// <summary>Everything one account owns: friend relations and categories.</summary>
/// <param name="Relations">Friend relations, ordered by friend id.</param>
/// <param name="Categories">Categories, ordered by id.</param>
public sealed record MessengerRelations(IReadOnlyList<MessengerRelation> Relations, IReadOnlyList<MessengerCategory> Categories);

/// <summary>A pending request as stored.</summary>
/// <param name="Handle">Positive request handle.</param>
/// <param name="From">Requesting account.</param>
/// <param name="To">Recipient account.</param>
/// <param name="Message">Requester's message.</param>
/// <param name="CreatedAt">UTC creation time.</param>
public sealed record MessengerStoredRequest(int Handle, MessengerUserId From, MessengerUserId To, string Message, DateTime CreatedAt);

/// <summary>The outcome of creating or resolving a request, with the request it concerned when there is one.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Request">The request involved (the new, existing pending or resolved one); null when none was found or created.</param>
public sealed record MessengerRequestOutcome(MessengerResult Result, MessengerStoredRequest? Request);

/// <summary>Limits a store enforces atomically with the change they bound.</summary>
/// <param name="MaxFriends">Maximum friends per account.</param>
/// <param name="MaxCategories">Maximum categories per account.</param>
/// <param name="MaxPendingRequests">Maximum pending requests addressed to one account.</param>
public sealed record MessengerLimits(int MaxFriends, int MaxCategories, int MaxPendingRequests);

/// <summary>
/// Durable messenger state: accounts, mutual friendships, blocks, categories and pending requests.
/// Every method is one atomic change; relations are always stored in both directions so a friendship
/// is created and ended for both accounts together. Implementations never return another tenant's
/// account from a tenant-scoped lookup.
/// </summary>
public interface IMessengerStore
{
    /// <summary>Returns the account for the identity's tenant and subject, creating it or refreshing its nickname and game code.</summary>
    ValueTask<MessengerUser> UpsertUserAsync(MessengerIdentity identity, CancellationToken cancellationToken);

    /// <summary>Finds an account by id, or null.</summary>
    ValueTask<MessengerUser?> FindUserAsync(MessengerUserId id, CancellationToken cancellationToken);

    /// <summary>Finds the account with <paramref name="nickname"/> (case-insensitive) in <paramref name="tenant"/>, or null when absent or ambiguous.</summary>
    ValueTask<MessengerUser?> FindUserByNicknameAsync(string tenant, string nickname, CancellationToken cancellationToken);

    /// <summary>Loads an account's relations and categories.</summary>
    ValueTask<MessengerRelations> LoadRelationsAsync(MessengerUserId owner, CancellationToken cancellationToken);

    /// <summary>Lists pending requests addressed to <paramref name="recipient"/>, oldest first.</summary>
    ValueTask<IReadOnlyList<MessengerStoredRequest>> ListIncomingRequestsAsync(MessengerUserId recipient, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a pending request unless the accounts are friends (<see cref="MessengerResult.AlreadyFriends"/>), a request
    /// in either direction is pending (<see cref="MessengerResult.AlreadyPending"/>, returning that request), or the
    /// recipient's pending bound is reached (<see cref="MessengerResult.LimitReached"/>).
    /// </summary>
    ValueTask<MessengerRequestOutcome> CreateRequestAsync(
        MessengerUserId from, MessengerUserId to, string message, DateTime now, MessengerLimits limits, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the pending request <paramref name="handle"/> addressed to <paramref name="recipient"/>; when
    /// <paramref name="accept"/> is set, creates the friendship in both directions in the root category. Returns
    /// <see cref="MessengerResult.LimitReached"/> without changes when either account is at its friend bound.
    /// </summary>
    ValueTask<MessengerRequestOutcome> ResolveRequestAsync(
        int handle, MessengerUserId recipient, bool accept, MessengerLimits limits, CancellationToken cancellationToken);

    /// <summary>Ends a friendship in both directions; returns false when it did not exist.</summary>
    ValueTask<bool> DeleteFriendshipAsync(MessengerUserId owner, MessengerUserId friend, CancellationToken cancellationToken);

    /// <summary>Sets the owner's block flag on an existing friend; returns false when they are not friends.</summary>
    ValueTask<bool> SetBlockedAsync(MessengerUserId owner, MessengerUserId friend, bool blocked, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a friend to <paramref name="target"/> when it is currently in <paramref name="expected"/>; zero is the root.
    /// Returns <see cref="MessengerResult.NotFound"/> for an unknown friend, category or a mismatching current category.
    /// </summary>
    ValueTask<MessengerResult> SetFriendCategoryAsync(MessengerUserId owner, MessengerUserId friend, int expected, int target, CancellationToken cancellationToken);

    /// <summary>Creates a category with the next free positive id, or returns <see cref="MessengerResult.LimitReached"/>.</summary>
    ValueTask<MessengerCategoryOutcome> AddCategoryAsync(
        MessengerUserId owner, string name, uint gameCode, MessengerLimits limits, CancellationToken cancellationToken);

    /// <summary>Renames a category; <see cref="MessengerResult.NotFound"/> when the owner has no such category.</summary>
    ValueTask<MessengerResult> RenameCategoryAsync(MessengerUserId owner, int categoryId, string name, CancellationToken cancellationToken);

    /// <summary>Deletes an empty category; <see cref="MessengerResult.NotEmpty"/> while it holds friends.</summary>
    ValueTask<MessengerResult> DeleteCategoryAsync(MessengerUserId owner, int categoryId, CancellationToken cancellationToken);

    /// <summary>Deletes pending requests created at or before <paramref name="cutoff"/> and returns how many were removed.</summary>
    ValueTask<int> PruneRequestsAsync(DateTime cutoff, CancellationToken cancellationToken);
}
