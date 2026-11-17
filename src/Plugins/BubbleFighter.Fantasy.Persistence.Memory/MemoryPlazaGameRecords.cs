using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Process-local plaza mini-game records for development profiles, kept on the player's entry so a settlement
/// changes the record and the wallet under the same per-player gate; lost on restart.
/// </summary>
/// <param name="table">The player table whose entries hold the records and wallets.</param>
public sealed class MemoryPlazaGameRecords(MemoryPlayerTable table) : IPlazaGameRecordStore
{
    /// <inheritdoc />
    public ValueTask<PlazaGameRecord> ReadAsync(long userNum, DateOnly day, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult(PlazaGameRecord.Empty(day));
        lock (entry.Gate)
            return ValueTask.FromResult(Current(entry, day));
    }

    /// <inheritdoc />
    public ValueTask<PlazaGameSettled> SettleAsync(PlazaGameSettlement settlement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        settlement.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(settlement.SeedWallet.UserNum);
        lock (entry.Gate)
        {
            var settled = settlement.Apply(Current(entry, settlement.Day), entry.Money ?? settlement.SeedWallet);
            if (settled.Status == PlazaGameSettlementStatus.Settled)
            {
                entry.PlazaGame = settled.Record;
                entry.Money = settled.Wallet;
            }
            return ValueTask.FromResult(settled);
        }
    }

    // A record of an earlier day is that day's history only; the current day starts empty.
    private static PlazaGameRecord Current(PlayerEntry entry, DateOnly day) =>
        entry.PlazaGame is { } record && record.Day == day ? record : PlazaGameRecord.Empty(day);
}
