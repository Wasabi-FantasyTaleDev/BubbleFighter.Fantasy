namespace BubbleFighter.Fantasy.Contracts.Presents;

/// <summary>Where a stored gift stands for its receiver.</summary>
public enum PresentState
{
    /// <summary>Not taken yet: the item is still the sender's gift and is not in any bag.</summary>
    Pending = 0,

    /// <summary>The receiver took it: the item was granted to the receiver's bag in the same transaction.</summary>
    Accepted = 1,

    /// <summary>The receiver threw it away before taking it: the item was never delivered.</summary>
    Discarded = 2,
}

/// <summary>Which of a player's two gift lists a request or reply is about.</summary>
public enum PresentBox
{
    /// <summary>Gifts other players sent to the player.</summary>
    Received = 0,

    /// <summary>Gifts the player sent.</summary>
    Sent = 1,
}

/// <summary>
/// One stored gift. The row is shared by two views, the receiver's and the sender's, which hide it
/// independently; it is removed when both have.
/// </summary>
/// <param name="Id">Store-allocated identity, positive and unique over all gifts.</param>
/// <param name="ProfileId">
/// The client build whose shop workbook <paramref name="StockKey"/> belongs to. A gift is only listed to and taken by
/// the same build, because a stock key means nothing in another build's workbook.
/// </param>
/// <param name="SenderUserNum">The paying player.</param>
/// <param name="SenderNickname">The sender's nickname when the gift was sent.</param>
/// <param name="ReceiverUserNum">The receiving player.</param>
/// <param name="ReceiverNickname">The receiver's nickname when the gift was sent.</param>
/// <param name="StockKey">The Stock or StockSet key of the build's shop workbook that was bought.</param>
/// <param name="Message">The sender's message.</param>
/// <param name="SentAtUnixMilliseconds">When the gift was sent (UTC).</param>
/// <param name="State">Where the gift stands for the receiver.</param>
public sealed record PresentRecord(long Id, string ProfileId, long SenderUserNum, string SenderNickname,
    long ReceiverUserNum, string ReceiverNickname, int StockKey, string Message, long SentAtUnixMilliseconds,
    PresentState State)
{
    /// <summary>Gets whether the sender removed the gift from the sent list.</summary>
    public bool SenderHidden { get; init; }

    /// <summary>Gets whether the receiver removed the gift from the received list.</summary>
    public bool ReceiverHidden { get; init; }
}

/// <summary>One page of a gift list, newest gift first.</summary>
/// <param name="Total">How many gifts the list holds in all, not only on this page.</param>
/// <param name="Records">The requested page.</param>
public sealed record PresentPage(int Total, IReadOnlyList<PresentRecord> Records);

/// <summary>A player found by nickname.</summary>
/// <param name="UserNum">Login <c>players.user_num</c>.</param>
/// <param name="Nickname">The stored nickname, in its stored spelling.</param>
public sealed record PresentPlayer(long UserNum, string Nickname);

/// <summary>Bounds on what a gift request may ask the store for.</summary>
public static class PresentLimits
{
    /// <summary>The most gifts one page may hold; the client asks for 4 (kr-812 sub_960BE0).</summary>
    public const int MaxPageCount = 16;

    /// <summary>The longest nickname the store looks up (the <c>players.nickname</c> column width).</summary>
    public const int MaxNicknameChars = 64;

    /// <summary>The longest message the store keeps.</summary>
    public const int MaxMessageChars = 1024;

    /// <summary>The largest gift id a client can name: ids travel as unsigned 32-bit words.</summary>
    public const long MaxId = uint.MaxValue;

    /// <summary>The sender number of gifts the server gives (rewards): no player has it, so no sent list shows them.</summary>
    public const long SystemSenderUserNum = 0;

    /// <summary>The longest once-only claim key a reward may carry.</summary>
    public const int MaxClaimKeyChars = 64;

    /// <summary>The most gifts one reward may hold.</summary>
    public const int MaxRewardGifts = 16;
}
