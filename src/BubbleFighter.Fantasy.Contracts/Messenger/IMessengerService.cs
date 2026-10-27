namespace BubbleFighter.Fantasy.Contracts.Messenger;

/// <summary>
/// The game-neutral messenger: authenticated sessions, friend relations, categories, presence, the game
/// catalog and per-account change events. Operation names follow the native messenger operations they
/// serve. Every call taking a session token throws <see cref="UnauthorizedAccessException"/> for an
/// unknown or expired token. Mutations accept an optional caller-chosen request key: repeating a key
/// within the replay window returns the first result without applying the change again.
/// </summary>
public interface IMessengerService
{
    /// <summary>
    /// Exchanges a tenant-signed ticket for a session (CNMLoginAuthFunc). <paramref name="gameCode"/> is the code the
    /// client announced when it initialized its messenger; it must be the ticket's game or one of that game's aliases,
    /// and null keeps the ticket's code. Returns null for an invalid, expired or replayed ticket or a foreign code.
    /// </summary>
    ValueTask<MessengerSession?> LoginAuthAsync(string ticket, uint? gameCode, CancellationToken cancellationToken);

    /// <summary>Ends a session (CNMLogoutAuthFunc); the account goes offline when it was its last session.</summary>
    ValueTask LogoutAuthAsync(string session, CancellationToken cancellationToken);

    /// <summary>Marks the session's account online and queues its pending friend requests for delivery (CNMLoginMessengerFunc).</summary>
    ValueTask LoginMessengerAsync(string session, CancellationToken cancellationToken);

    /// <summary>Marks the session's account offline while keeping the session (CNMLogoutMessengerFunc).</summary>
    ValueTask LogoutMessengerAsync(string session, CancellationToken cancellationToken);

    /// <summary>Records the level the account's client reports (CNMChangeMyLevelFunc).</summary>
    ValueTask ChangeMyLevelAsync(string session, int level, CancellationToken cancellationToken);

    /// <summary>Returns one page of the account's friends and all of its categories (CNMGetFriendListFunc).</summary>
    ValueTask<MessengerFriendListPage> GetFriendListAsync(string session, int page, CancellationToken cancellationToken);

    /// <summary>Asks the account with <paramref name="nickname"/> in the caller's tenant to become friends (CNMRequestNewFriendFunc).</summary>
    ValueTask<MessengerResult> RequestNewFriendAsync(string session, string? requestKey, string nickname, string message, CancellationToken cancellationToken);

    /// <summary>Answers a pending request addressed to the caller (CNMConfirmNewFriendFunc).</summary>
    ValueTask<MessengerResult> ConfirmNewFriendAsync(string session, string? requestKey, int handle, MessengerFriendDecision decision, CancellationToken cancellationToken);

    /// <summary>Blocks or unblocks a friend (CNMBlockFriendFunc). A blocked friend sees the caller offline and cannot send requests.</summary>
    ValueTask<MessengerResult> BlockFriendAsync(string session, string? requestKey, MessengerUserId friend, bool blocked, CancellationToken cancellationToken);

    /// <summary>Places an uncategorized friend into a category (CNMAddFriendToCategoryFunc).</summary>
    ValueTask<MessengerResult> AddFriendToCategoryAsync(string session, string? requestKey, MessengerUserId friend, int categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a friend from the category it is listed in (CNMDeleteFriendFromCategoryFunc). A friend is listed in
    /// exactly one category or the root, so this ends the friendship for both accounts.
    /// </summary>
    ValueTask<MessengerResult> DeleteFriendFromCategoryAsync(string session, string? requestKey, MessengerUserId friend, int categoryId, CancellationToken cancellationToken);

    /// <summary>Moves a friend between categories; zero is the root (CNMMoveFriendCategoryFunc).</summary>
    ValueTask<MessengerResult> MoveFriendCategoryAsync(string session, string? requestKey, MessengerUserId friend, int fromCategoryId, int toCategoryId, CancellationToken cancellationToken);

    /// <summary>Creates a category (CNMAddCategoryFunc).</summary>
    ValueTask<MessengerCategoryOutcome> AddCategoryAsync(string session, string? requestKey, string name, CancellationToken cancellationToken);

    /// <summary>Deletes an empty category (CNMDeleteCategoryFunc).</summary>
    ValueTask<MessengerResult> DeleteCategoryAsync(string session, string? requestKey, int categoryId, CancellationToken cancellationToken);

    /// <summary>Renames a category (CNMChangeCategoryNameFunc).</summary>
    ValueTask<MessengerResult> ChangeCategoryNameAsync(string session, string? requestKey, int categoryId, string name, CancellationToken cancellationToken);

    /// <summary>Returns the configured game catalog (CNMGetSupportGameListFunc).</summary>
    ValueTask<IReadOnlyList<MessengerGame>> GetSupportGameListAsync(string session, CancellationToken cancellationToken);

    /// <summary>Finds a catalog entry by code (CNMGetGameFullNameFunc, CNMGetGameShortNameFunc), or null.</summary>
    ValueTask<MessengerGame?> FindGameAsync(string session, uint code, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the caller's chat with a friend, reusing a live chat between the two (CNMRequestChatSessionFunc). The caller
    /// receives <see cref="MessengerChatOpenedEvent"/>; the friend's window opens with the first line. Refused when either
    /// side has blocked the other.
    /// </summary>
    ValueTask<MessengerChatOutcome> RequestChatSessionAsync(string session, MessengerUserId friend, CancellationToken cancellationToken);

    /// <summary>Returns a chat the caller belongs to, or null (CNMGSGetSessionInfoFunc, CNMGSGetMemberListFunc).</summary>
    ValueTask<MessengerChat?> GetChatSessionAsync(string session, int chatId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a line to every member of a chat, the caller included (CNMCSSendChatMessageFunc). A member whose window is
    /// closed first receives <see cref="MessengerChatOpenedEvent"/> again.
    /// </summary>
    ValueTask<MessengerResult> SendChatMessageAsync(string session, int chatId, string text, CancellationToken cancellationToken);

    /// <summary>Records that the caller closed its chat window (CNMGSWantCloseFunc); the chat ends when no member has it open.</summary>
    ValueTask<MessengerResult> CloseChatSessionAsync(string session, int chatId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the session account's events after <paramref name="after"/>, waiting up to <paramref name="wait"/>
    /// when none are queued. Polling keeps the session and its presence alive.
    /// </summary>
    ValueTask<MessengerEventBatch> WaitEventsAsync(string session, long after, TimeSpan wait, CancellationToken cancellationToken);
}
