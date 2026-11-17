using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>
/// A player's plaza mini-game record for one day, the two values the client's daily record packet restores
/// (<c>gspRepLmgUserRecord</c>, kr-1256 <c>sub_9D2FF0</c> case 142): how many fish were landed and the fortune
/// paper drawn. The fortune booth sends a request only while the stored paper is zero (kr-1256 <c>sub_D66DE0</c>),
/// so a day's paper must survive a reconnect.
/// </summary>
/// <param name="Day">The record's day in the deployment's reset time zone.</param>
/// <param name="FishingCount">Fish landed that day.</param>
/// <param name="FortunePaper">The fortune paper the client drew that day, or zero for none yet.</param>
public sealed record PlazaGameRecord(DateOnly Day, int FishingCount, ulong FortunePaper)
{
    /// <summary>Returns the empty record of <paramref name="day"/>.</summary>
    /// <param name="day">The day.</param>
    public static PlazaGameRecord Empty(DateOnly day) => new(day, 0, 0);
}

/// <summary>
/// One booth action's change to a player's day record and Lucci, applied as a single transaction: either the
/// record change and the wallet change both happen or neither does.
/// </summary>
/// <param name="SeedWallet">The player's wallet as last read; used only when the store holds no wallet row yet.</param>
/// <param name="Day">The current day in the deployment's reset time zone.</param>
public sealed record PlazaGameSettlement(PlayerWallet SeedWallet, DateOnly Day)
{
    /// <summary>Gets the Lucci the player must hold and pays; zero for a free action.</summary>
    public int Charge { get; init; }

    /// <summary>Gets the Lucci paid to the player after the charge.</summary>
    public int Credit { get; init; }

    /// <summary>Gets the daily fish limit when the action lands a fish, or zero when it lands none.</summary>
    public int FishLimit { get; init; }

    /// <summary>Gets the fortune paper the action stores for the day, or zero when it stores none.</summary>
    public ulong FortunePaper { get; init; }

    /// <summary>Throws when the settlement is out of range.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(SeedWallet);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SeedWallet.UserNum);
        ArgumentOutOfRangeException.ThrowIfNegative(Charge);
        ArgumentOutOfRangeException.ThrowIfNegative(Credit);
        ArgumentOutOfRangeException.ThrowIfNegative(FishLimit);
    }

    /// <summary>
    /// Applies the settlement to the stored record and wallet: the shared rule every store uses inside its transaction.
    /// </summary>
    /// <param name="record">The stored record of the settlement's day.</param>
    /// <param name="wallet">The stored wallet.</param>
    /// <returns>The outcome, with the new record and wallet when it is <see cref="PlazaGameSettlementStatus.Settled"/>.</returns>
    public PlazaGameSettled Apply(PlazaGameRecord record, PlayerWallet wallet)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(wallet);
        if (FishLimit > 0 && record.FishingCount >= FishLimit)
            return new(PlazaGameSettlementStatus.LimitReached, record, wallet);
        if (FortunePaper != 0 && record.FortunePaper != 0)
            return new(PlazaGameSettlementStatus.PaperTaken, record, wallet);
        if (wallet.Lucci < Charge)
            return new(PlazaGameSettlementStatus.ShortBalance, record, wallet);
        var next = record with
        {
            FishingCount = FishLimit > 0 ? record.FishingCount + 1 : record.FishingCount,
            FortunePaper = FortunePaper != 0 ? FortunePaper : record.FortunePaper,
        };
        return new(PlazaGameSettlementStatus.Settled, next, wallet with { Lucci = checked(wallet.Lucci - Charge + Credit) });
    }
}

/// <summary>Why a plaza settlement was or was not applied.</summary>
public enum PlazaGameSettlementStatus
{
    /// <summary>The record and wallet changes were written.</summary>
    Settled,

    /// <summary>The player held less Lucci than the charge; nothing was written.</summary>
    ShortBalance,

    /// <summary>The day's fish limit was already reached; nothing was written.</summary>
    LimitReached,

    /// <summary>The day already has a fortune paper; nothing was written.</summary>
    PaperTaken,
}

/// <summary>The outcome of a plaza settlement with the record and wallet as they are stored afterwards.</summary>
/// <param name="Status">Whether the settlement was applied.</param>
/// <param name="Record">The day record after the settlement (unchanged when it was not applied).</param>
/// <param name="Wallet">The wallet after the settlement (unchanged when it was not applied).</param>
public sealed record PlazaGameSettled(PlazaGameSettlementStatus Status, PlazaGameRecord Record, PlayerWallet Wallet);

/// <summary>
/// Keeps one row per player with the current day's plaza mini-game record. A stored row of an earlier day reads as
/// that day's empty record. A settlement changes the record and the player's wallet in one transaction, so a crash
/// or a concurrent session never leaves a fish paid without being counted, or a paper stored without being charged.
/// </summary>
public interface IPlazaGameRecordStore
{
    /// <summary>Returns the player's record of <paramref name="day"/>, empty when nothing is stored for that day.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="day">The current day.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlazaGameRecord> ReadAsync(long userNum, DateOnly day, CancellationToken ct);

    /// <summary>
    /// Applies <see cref="PlazaGameSettlement.Apply"/> to the stored record and wallet under the player's lock and
    /// writes both when it settles.
    /// </summary>
    /// <param name="settlement">The booth action's change.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlazaGameSettled> SettleAsync(PlazaGameSettlement settlement, CancellationToken ct);
}
