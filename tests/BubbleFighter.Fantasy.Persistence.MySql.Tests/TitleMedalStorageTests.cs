using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks medal storage validation and command refusal before any live database access.</summary>
public sealed class TitleMedalStorageTests
{
    /// <summary>Serialized balances survive storage decoding, and a stored receipt recognizes only its exact event.</summary>
    [Fact]
    public void StoredLedgerRoundTripPreservesDebitAndReplay()
    {
        var command = new TitleMedalGrantCommand(1, "kr-1256", "test", "event-1", [new(30007, 30), new(30008, 25)]);
        var limits = new TitleMedalLimits();
        var ledger = TitleMedalLedger.Empty.Credit(command, limits).Debit([new(30007, 20)], limits)!;
        var decoded = MySqlInventoryRepository.DecodeTitleLedger(JsonSerializer.Serialize(ledger), limits);
        Assert.Equal(ledger.Medals, decoded.Medals);
        var receipt = TitleMedalGrantReceipt.For(command);
        receipt.RequireSameEvent(TitleMedalGrantReceipt.For(command));
        Assert.Throws<InvalidOperationException>(() => TitleMedalGrantReceipt.For(command with { Source = "different" }).RequireSameEvent(receipt));
        Assert.Throws<InvalidOperationException>(() => TitleMedalGrantReceipt.For(new TitleMedalGrantCommand(1, "kr-1256", "test", "event-1", [new(30007, 31)])).RequireSameEvent(receipt));
    }

    /// <summary>Negative balances, duplicate medal identities, unknown fields and a balance row carrying receipts fail before publication.</summary>
    [Theory]
    [InlineData("{\"Medals\":[{\"MedalId\":1,\"Count\":-1}]}")]
    [InlineData("{\"Medals\":[{\"MedalId\":1,\"Count\":0},{\"MedalId\":1,\"Count\":2}]}")]
    [InlineData("{\"Medals\":[],\"Unknown\":0}")]
    [InlineData("{\"Medals\":[],\"Receipts\":[]}")]
    public void CorruptStoredLedgerIsRejected(string json) =>
        Assert.ThrowsAny<Exception>(() => MySqlInventoryRepository.DecodeTitleLedger(json, new()));

    /// <summary>Foreign initialization, invalid grant identity and cancellation never open a database connection.</summary>
    [Fact]
    public async Task InvalidAndCancelledCommandsNeverOpenDatabase()
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        var scope = new InventoryLoadoutScope("kr-1256", [400001]);
        var command = new TitleClaimCommand(1, scope, 400001, 1200001, [new(30007, 20)],
            new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, DateTimeOffset.UnixEpoch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryClaimAsync(command, new(true)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryClaimAsync(command with
            { ArticleInitialization = new(2, scope, [400001], DateTimeOffset.UnixEpoch) }, default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(1, scope.Key, new(true)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.GrantAsync(new(1, scope.Key, "", "event", [new(30007, 1)]), default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GrantAsync(new(1, scope.Key, "test", "event", [new(30007, 1)]), new(true)).AsTask());
    }

    /// <summary>An automatic grant is validated before any connection opens: no debits or bonuses, and a medal claim still needs debits.</summary>
    [Fact]
    public async Task AutomaticGrantValidationRunsBeforeTheDatabase()
    {
        var store = new MySqlInventoryRepository(new UnusedFactory());
        var scope = new InventoryLoadoutScope("kr-1256", [400001]);
        var term = new InventoryGrantTerm(400001, InventoryGrantKind.Permanent, 1, 1, false);
        var automatic = new TitleClaimCommand(1, scope, 400001, 1200001, [], term, null, DateTimeOffset.UnixEpoch) { IsAutomaticGrant = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryClaimAsync(automatic, new(true)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryClaimAsync(automatic with { BonusMedals = [new(30007, 1)] }, default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryClaimAsync(
            new TitleClaimCommand(1, scope, 400001, 1200001, [], term, null, DateTimeOffset.UnixEpoch), default).AsTask());
        var ledger = TitleMedalLedger.Empty.Credit(new(1, "kr-1256", "test", "event-1", [new(30007, 3)]), new());
        Assert.Same(ledger, ledger.Claim(automatic, new(), includeBonuses: true));
    }
    private sealed class UnusedFactory : BubbleFighter.Fantasy.Abstractions.Persistence.IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Validation must finish before opening the database.");
    }
}
