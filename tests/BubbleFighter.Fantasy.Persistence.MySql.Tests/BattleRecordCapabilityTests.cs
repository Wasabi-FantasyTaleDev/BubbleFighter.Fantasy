using BubbleFighter.Fantasy.Contracts.Progress;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class BattleRecordCapabilityTests
{
    [Fact]
    public async Task InvalidBattleCountersFailBeforeOpeningADatabaseConnection()
    {
        var store = new MySqlPlayerProgressRepository(new UnusedFactory());
        Assert.True(store.IsDurable);
        var command = new RoundSettlementCommand("match", 7, 0, before => before)
        {
            BattleRecord = new(1, BattleRecordOutcome.Won, -1, 2, 3, 4, 11),
        };
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.SettleRoundAsync(command, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ListBattleRecordsAsync(0, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void InvalidModeCapacityFailsBeforeOpeningADatabaseConnection(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MySqlPlayerProgressRepository(new UnusedFactory(), maxBattleModesPerPlayer: capacity));

    private sealed class UnusedFactory : BubbleFighter.Fantasy.Abstractions.Persistence.IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Validation must precede opening the database.");
    }
}
