using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.Presents;

/// <summary>Why a gift was or was not stored.</summary>
public enum PresentSendOutcome
{
    /// <summary>The sender was charged and the gift is stored.</summary>
    Sent = 0,

    /// <summary>The sender's balance does not cover the price; nothing changed.</summary>
    InsufficientFunds = 1,

    /// <summary>The receiver already holds the most gifts a player may keep; nothing changed.</summary>
    ReceiverBoxFull = 2,

    /// <summary>The sender already keeps the most sent gifts a player may keep; nothing changed.</summary>
    SenderBoxFull = 3,

    /// <summary>The receiver does not exist (any more); nothing changed.</summary>
    ReceiverMissing = 4,
}

/// <summary>Everything one gift purchase needs, resolved from the shop catalog by the caller.</summary>
/// <param name="ProfileId">The client build whose shop workbook the key belongs to.</param>
/// <param name="Sender">The paying player and nickname.</param>
/// <param name="Receiver">The receiving player and nickname.</param>
/// <param name="StockKey">The Stock or StockSet key bought.</param>
/// <param name="StockSetId">The bought bundle, or zero for an individual stock (purchase audit).</param>
/// <param name="ItemId">The first item the stock grants (purchase audit).</param>
/// <param name="Message">The sender's message.</param>
/// <param name="MoneyType">The catalog currency, <c>lucci</c> or <c>cash</c>.</param>
/// <param name="Price">The nonnegative catalog price.</param>
/// <param name="SeedWallet">The wallet to start the sender from when it has none yet.</param>
/// <param name="SentAtUnixMilliseconds">The send time (UTC).</param>
/// <param name="MaxPerPlayer">The most gifts either list of a player may hold.</param>
public sealed record PresentSendCommand(string ProfileId, PresentPlayer Sender, PresentPlayer Receiver, int StockKey,
    int StockSetId, int ItemId, string Message, string MoneyType, int Price, PlayerWallet SeedWallet,
    long SentAtUnixMilliseconds, int MaxPerPlayer)
{
    /// <summary>Rejects invalid inputs before any storage is modified.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentNullException.ThrowIfNull(Sender);
        ArgumentNullException.ThrowIfNull(Receiver);
        ArgumentNullException.ThrowIfNull(Message);
        ArgumentNullException.ThrowIfNull(SeedWallet);
        if (Sender.UserNum <= 0 || Receiver.UserNum <= 0 || Sender.UserNum == Receiver.UserNum
            || SeedWallet.UserNum != Sender.UserNum || StockKey <= 0 || StockSetId < 0 || ItemId < 0
            || Price < 0 || MoneyType is not ("lucci" or "cash") || SeedWallet.Lucci < 0 || SeedWallet.Cash < 0
            || SentAtUnixMilliseconds < 0 || MaxPerPlayer < 1
            || Message.Length > PresentLimits.MaxMessageChars
            || Sender.Nickname.Length is 0 or > PresentLimits.MaxNicknameChars
            || Receiver.Nickname.Length is 0 or > PresentLimits.MaxNicknameChars)
            throw new ArgumentException("A gift needs two different players, a positive stock key, a known currency, a nonnegative price and bounded text.");
    }
}

/// <summary>What storing one gift came to.</summary>
/// <param name="Outcome">Whether the gift was stored, and if not why.</param>
/// <param name="Present">The stored gift, or null unless the outcome is <see cref="PresentSendOutcome.Sent"/>.</param>
/// <param name="Wallet">The sender's wallet after the charge, or null unless the outcome is Sent.</param>
public sealed record PresentSendResult(PresentSendOutcome Outcome, PresentRecord? Present, PlayerWallet? Wallet);

/// <summary>Why a system reward was or was not stored.</summary>
public enum PresentRewardOutcome
{
    /// <summary>The claim was recorded and every gift of the reward is stored.</summary>
    Granted = 0,

    /// <summary>The claim key was recorded before: nothing was stored now.</summary>
    AlreadyClaimed = 1,

    /// <summary>The receiver does not exist (any more); nothing changed and no claim was recorded.</summary>
    ReceiverMissing = 2,
}

/// <summary>
/// A reward the server gives a player for free through the gift box, such as the gifts of a reached level. The claim
/// key makes it happen once: the store records it together with the gifts, so a repeated command stores nothing.
/// </summary>
/// <param name="ProfileId">The client build whose shop workbook the keys belong to.</param>
/// <param name="ReceiverUserNum">The receiving player; the store reads the nickname the gifts carry.</param>
/// <param name="ClaimKey">The once-only key, unique per player and build, for example <c>level:12</c>.</param>
/// <param name="SenderNickname">The name the box shows as the sender.</param>
/// <param name="StockKeys">The Stock or StockSet keys to give, one gift each, in order.</param>
/// <param name="Message">The message every gift carries.</param>
/// <param name="SentAtUnixMilliseconds">The grant time (UTC).</param>
public sealed record PresentRewardCommand(string ProfileId, long ReceiverUserNum, string ClaimKey, string SenderNickname,
    IReadOnlyList<int> StockKeys, string Message, long SentAtUnixMilliseconds)
{
    /// <summary>Rejects invalid inputs before any storage is modified.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClaimKey);
        ArgumentNullException.ThrowIfNull(SenderNickname);
        ArgumentNullException.ThrowIfNull(StockKeys);
        ArgumentNullException.ThrowIfNull(Message);
        if (ProfileId.Length > 32 || ReceiverUserNum <= 0 || ClaimKey.Length > PresentLimits.MaxClaimKeyChars
            || StockKeys.Count is 0 or > PresentLimits.MaxRewardGifts || StockKeys.Any(key => key <= 0)
            || SentAtUnixMilliseconds < 0 || Message.Length > PresentLimits.MaxMessageChars
            || SenderNickname.Length is 0 or > PresentLimits.MaxNicknameChars)
            throw new ArgumentException("A reward needs a receiver, a bounded claim key, one to a few positive stock keys and bounded text.");
    }
}

/// <summary>What storing one system reward came to.</summary>
/// <param name="Outcome">Whether the gifts were stored, and if not why.</param>
/// <param name="Presents">The stored gifts, empty unless the outcome is <see cref="PresentRewardOutcome.Granted"/>.</param>
public sealed record PresentRewardResult(PresentRewardOutcome Outcome, IReadOnlyList<PresentRecord> Presents);

/// <summary>
/// A gift named by a purchase command is no longer there for its receiver: taken already, thrown away, removed or
/// never theirs. The purchase has not committed and nothing changed.
/// </summary>
public sealed class PresentUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception for one gift.</summary>
    /// <param name="presentId">The gift that could not be delivered.</param>
    public PresentUnavailableException(long presentId)
        : base($"Gift {presentId} is not pending for the receiving player.") => PresentId = presentId;

    /// <summary>Gets the gift that could not be delivered.</summary>
    public long PresentId { get; }
}

/// <summary>
/// Stores gifts between players. A gift is bought by its sender (charged and stored in one transaction), listed to both
/// players, and taken by its receiver through <see cref="Shop.ShopPurchaseCommand.PresentId"/>, which marks the gift
/// taken in the same transaction that grants its items. Every list is bounded by the per-player cap given at send time.
/// </summary>
public interface IPresentStore
{
    /// <summary>Finds a player by nickname, ignoring case, or returns null.</summary>
    /// <param name="nickname">The nickname the sender typed.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PresentPlayer?> FindPlayerAsync(string nickname, CancellationToken ct);

    /// <summary>
    /// Atomically checks both players' list sizes, charges the sender, appends the purchase audit entry and stores the
    /// gift. Any refusal leaves wallet, audit and gifts unchanged.
    /// </summary>
    /// <param name="command">The resolved purchase.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PresentSendResult> SendAsync(PresentSendCommand command, CancellationToken ct);

    /// <summary>
    /// Stores the gifts of a system reward and records its claim key in one transaction, unless the key was recorded
    /// before. The sender is the system (<see cref="PresentLimits.SystemSenderUserNum"/>): nobody is charged, the gifts
    /// are in nobody's sent list, and the receiver's list cap does not apply, because the number of rewards is bounded
    /// by the operator's table and the claim keys. A repeated or concurrent command for one key stores exactly once.
    /// </summary>
    /// <param name="command">The reward.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PresentRewardResult> GrantRewardAsync(PresentRewardCommand command, CancellationToken ct);

    /// <summary>Lists the gifts a player has not removed from the received list, newest first.</summary>
    /// <param name="profileId">The client build whose gifts are listed.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="start">How many gifts to skip.</param>
    /// <param name="count">The page size, between 1 and <see cref="PresentLimits.MaxPageCount"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PresentPage> ListReceivedAsync(string profileId, long userNum, int start, int count, CancellationToken ct);

    /// <summary>Lists the gifts a player has not removed from the sent list, newest first.</summary>
    /// <param name="profileId">The client build whose gifts are listed.</param>
    /// <param name="userNum">The sending player.</param>
    /// <param name="start">How many gifts to skip.</param>
    /// <param name="count">The page size, between 1 and <see cref="PresentLimits.MaxPageCount"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PresentPage> ListSentAsync(string profileId, long userNum, int start, int count, CancellationToken ct);

    /// <summary>Counts the received gifts the player has neither taken, thrown away nor removed.</summary>
    /// <param name="profileId">The client build whose gifts are counted.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<int> CountPendingAsync(string profileId, long userNum, CancellationToken ct);

    /// <summary>Returns a gift still in the player's received list, or null.</summary>
    /// <param name="profileId">The client build the gift belongs to.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="id">The gift.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PresentRecord?> FindReceivedAsync(string profileId, long userNum, long id, CancellationToken ct);

    /// <summary>
    /// Throws a pending gift away: it becomes <see cref="PresentState.Discarded"/> and leaves the receiver's list.
    /// Returns false when no pending gift of the player has that id.
    /// </summary>
    /// <param name="profileId">The client build the gift belongs to.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="id">The gift.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> DiscardAsync(string profileId, long userNum, long id, CancellationToken ct);

    /// <summary>
    /// Removes a gift from the received list. Returns false when the player's list has no such gift, which includes a
    /// gift removed before.
    /// </summary>
    /// <param name="profileId">The client build the gift belongs to.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="id">The gift.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> DeleteReceivedAsync(string profileId, long userNum, long id, CancellationToken ct);

    /// <summary>
    /// Removes a gift from the sent list; the receiver keeps theirs. Returns false when the player's sent list has no
    /// such gift, which includes a gift removed before.
    /// </summary>
    /// <param name="profileId">The client build the gift belongs to.</param>
    /// <param name="userNum">The sending player.</param>
    /// <param name="id">The gift.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> DeleteSentAsync(string profileId, long userNum, long id, CancellationToken ct);
}
