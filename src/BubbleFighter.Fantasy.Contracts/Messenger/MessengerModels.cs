namespace BubbleFighter.Fantasy.Contracts.Messenger;

/// <summary>
/// Stable messenger-wide account identifier. It is assigned by the messenger store the first time a
/// tenant subject signs in and never changes, so friend relations survive nickname changes and are
/// independent of short-lived launcher tickets.
/// </summary>
/// <param name="Value">Positive store-assigned number.</param>
public readonly record struct MessengerUserId(long Value);

/// <summary>
/// An authenticated identity as asserted by a trusted tenant: which server or deployment vouches for
/// the account (<paramref name="Tenant"/>), the tenant's own stable account key, and display data.
/// </summary>
/// <param name="Tenant">Isolation key of the issuing server or deployment; relations never cross it.</param>
/// <param name="Subject">The tenant's stable account key, unique within the tenant.</param>
/// <param name="Nickname">Display nickname, unique within the tenant at issue time.</param>
/// <param name="GameCode">Game and region code shown by native clients; display data only, never an isolation key.</param>
public sealed record MessengerIdentity(string Tenant, string Subject, string Nickname, uint GameCode);

/// <summary>A messenger account as stored.</summary>
/// <param name="Id">Stable messenger-wide identifier.</param>
/// <param name="Tenant">Isolation key of the tenant that vouches for the account.</param>
/// <param name="Subject">The tenant's stable account key.</param>
/// <param name="Nickname">Last asserted display nickname.</param>
/// <param name="GameCode">Last asserted game and region code.</param>
public sealed record MessengerUser(MessengerUserId Id, string Tenant, string Subject, string Nickname, uint GameCode);

/// <summary>An authenticated messenger session bound to one account.</summary>
/// <param name="Token">Opaque bearer for messenger requests; never logged.</param>
/// <param name="User">The signed-in account, its game code shown as <paramref name="GameCode"/>.</param>
/// <param name="ExpiresAt">When the session stops being accepted unless it is used again.</param>
/// <param name="GameCode">The code this session's client compares friends against; every record the session reads carries it for the client's own game.</param>
public sealed record MessengerSession(string Token, MessengerUser User, DateTimeOffset ExpiresAt, uint GameCode);

/// <summary>Online state reported for a friend.</summary>
public enum MessengerPresenceStatus
{
    /// <summary>Not signed in to the messenger, hidden by a block, or timed out.</summary>
    Offline,

    /// <summary>Signed in to the messenger from a game client.</summary>
    Online,
}

/// <summary>A friend's current presence as seen by one viewer.</summary>
/// <param name="Status">Online state.</param>
/// <param name="Level">Last level the friend's client reported, or zero when unknown.</param>
public sealed record MessengerPresence(MessengerPresenceStatus Status, int Level);

/// <summary>One friend in a viewer's list.</summary>
/// <param name="User">The friend's account.</param>
/// <param name="CategoryId">The viewer's category holding the friend; zero for the uncategorized root.</param>
/// <param name="Blocked">Whether the viewer has blocked this friend.</param>
/// <param name="Presence">The friend's presence as visible to the viewer.</param>
public sealed record MessengerFriend(MessengerUser User, int CategoryId, bool Blocked, MessengerPresence Presence);

/// <summary>A viewer-owned friend category.</summary>
/// <param name="Id">Positive identifier, unique for the owner; zero is reserved for the root.</param>
/// <param name="Name">Display name.</param>
/// <param name="GameCode">Game and region code the category was created under.</param>
public sealed record MessengerCategory(int Id, string Name, uint GameCode);

/// <summary>The outcome of creating a category: the result, and the new category when it is <see cref="MessengerResult.Ok"/>.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Category">The created category; null unless <paramref name="Result"/> is <see cref="MessengerResult.Ok"/>.</param>
public sealed record MessengerCategoryOutcome(MessengerResult Result, MessengerCategory? Category);

/// <summary>One page of a viewer's friend list, ordered by friend id.</summary>
/// <param name="Categories">Every category of the viewer, on every page.</param>
/// <param name="Friends">The friends on this page.</param>
/// <param name="Page">Zero-based page number.</param>
/// <param name="TotalPages">Number of pages; at least one.</param>
/// <param name="TotalFriends">Number of friends across all pages.</param>
public sealed record MessengerFriendListPage(
    IReadOnlyList<MessengerCategory> Categories, IReadOnlyList<MessengerFriend> Friends, int Page, int TotalPages, int TotalFriends);

/// <summary>A pending friend request.</summary>
/// <param name="Handle">Positive request handle, echoed when the recipient answers.</param>
/// <param name="From">The requesting account.</param>
/// <param name="To">The recipient account.</param>
/// <param name="Message">The requester's message.</param>
/// <param name="CreatedAt">When the request was created.</param>
public sealed record MessengerFriendRequest(int Handle, MessengerUser From, MessengerUser To, string Message, DateTimeOffset CreatedAt);

/// <summary>How a recipient answered a friend request.</summary>
public enum MessengerFriendDecision
{
    /// <summary>The recipient accepted; both accounts become friends.</summary>
    Accept,

    /// <summary>The recipient declined.</summary>
    Decline,

    /// <summary>The prompt was dismissed without a choice (including automatic answers while busy); the request stays pending and is delivered again at the next messenger sign-in.</summary>
    Dismiss,
}

/// <summary>Outcome of a messenger operation.</summary>
public enum MessengerResult
{
    /// <summary>The operation was committed, or was already in the requested state.</summary>
    Ok,

    /// <summary>The target account, request, friend or category does not exist for the caller.</summary>
    NotFound,

    /// <summary>The accounts are already friends.</summary>
    AlreadyFriends,

    /// <summary>A request between the accounts is already pending.</summary>
    AlreadyPending,

    /// <summary>The target refuses requests from the caller.</summary>
    Refused,

    /// <summary>A configured bound (friends, categories, pending requests) would be exceeded.</summary>
    LimitReached,

    /// <summary>The category still holds friends.</summary>
    NotEmpty,

    /// <summary>The request was malformed, for example an empty name or the caller's own account.</summary>
    Invalid,

    /// <summary>The target belongs to another tenant and no federation permits the operation.</summary>
    CrossTenant,
}

/// <summary>A catalog entry describing a game whose friends native clients can group and label.</summary>
/// <param name="Code">Game and region code.</param>
/// <param name="FullName">Full display name.</param>
/// <param name="ShortName">Short display name.</param>
public sealed record MessengerGame(uint Code, string FullName, string ShortName);

/// <summary>A change delivered to one signed-in account, numbered by a per-account sequence.</summary>
/// <param name="Sequence">Strictly increasing per account.</param>
public abstract record MessengerEvent(long Sequence);

/// <summary>Someone asked the account to become friends.</summary>
/// <param name="Sequence">Per-account sequence number.</param>
/// <param name="Request">The pending request.</param>
public sealed record MessengerFriendRequestEvent(long Sequence, MessengerFriendRequest Request) : MessengerEvent(Sequence);

/// <summary>The account's friend list or categories changed; the client should fetch the list again.</summary>
/// <param name="Sequence">Per-account sequence number.</param>
public sealed record MessengerFriendListChangedEvent(long Sequence) : MessengerEvent(Sequence);

/// <summary>A friend's visible presence changed.</summary>
/// <param name="Sequence">Per-account sequence number.</param>
/// <param name="Friend">The friend whose presence changed.</param>
/// <param name="Presence">The new presence as visible to the account.</param>
public sealed record MessengerPresenceChangedEvent(long Sequence, MessengerUserId Friend, MessengerPresence Presence) : MessengerEvent(Sequence);

/// <summary>One account in a chat with its presence; native chat windows announce a member that is not online.</summary>
/// <param name="User">The member's account, shown under the viewer's game code.</param>
/// <param name="Presence">The member's presence when the record was made.</param>
public sealed record MessengerChatMember(MessengerUser User, MessengerPresence Presence);

/// <summary>A conversation between two friends. Its id is what native clients key their chat window by.</summary>
/// <param name="Id">Positive identifier, unique among the live chats of this process.</param>
/// <param name="Members">The two members of the chat.</param>
public sealed record MessengerChat(int Id, IReadOnlyList<MessengerChatMember> Members);

/// <summary>The outcome of opening a chat: the result, and the chat when it is <see cref="MessengerResult.Ok"/>.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Chat">The opened chat; null unless <paramref name="Result"/> is <see cref="MessengerResult.Ok"/>.</param>
public sealed record MessengerChatOutcome(MessengerResult Result, MessengerChat? Chat);

/// <summary>A chat window should open for the account: it opened the chat, or a message arrived while its window was closed.</summary>
/// <param name="Sequence">Per-account sequence number.</param>
/// <param name="Chat">The chat to show.</param>
public sealed record MessengerChatOpenedEvent(long Sequence, MessengerChat Chat) : MessengerEvent(Sequence);

/// <summary>A line in a chat, delivered to every member including its sender, whose client shows its own line only from this event.</summary>
/// <param name="Sequence">Per-account sequence number.</param>
/// <param name="ChatId">The chat the line belongs to.</param>
/// <param name="From">The sending account.</param>
/// <param name="Text">The line as sent.</param>
public sealed record MessengerChatMessageEvent(long Sequence, int ChatId, MessengerUser From, string Text) : MessengerEvent(Sequence);

/// <summary>Events after a cursor.</summary>
/// <param name="Events">Events in sequence order.</param>
/// <param name="Next">Cursor to pass on the next poll.</param>
/// <param name="Reset">True when events before <paramref name="Events"/> were dropped by the bounded queue; the client must refetch its state.</param>
public sealed record MessengerEventBatch(IReadOnlyList<MessengerEvent> Events, long Next, bool Reset);

/// <summary>A signed, short-lived credential a game server hands to its client for messenger sign-in.</summary>
/// <param name="Value">Opaque ticket text; a secret for its lifetime.</param>
/// <param name="ExpiresAt">When the ticket stops being accepted.</param>
public sealed record MessengerTicket(string Value, DateTimeOffset ExpiresAt);
