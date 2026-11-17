using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Verifies the SQLite Mysterystore keeps its selection and the paid inventory in one saved state.</summary>
public sealed class MysteryStoreTests : IDisposable
{
    private const int Coin = 100385;
    private const int OfferKey = 2000001;
    private const string Revision = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [Coin, .. Enumerable.Range(101, 20)]);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-mystery-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };

    /// <summary>A purchase survives a reopen with its board, token and bought card, and a replay pays nothing.</summary>
    [Fact]
    public async Task PurchasedSelectionSurvivesReopen()
    {
        long user;
        MysteryStoreSelection bought;
        using (var database = new SqliteDatabase(Options))
        {
            var repositories = new SqliteRepositories(database) { MysteryStoreClock = new FixedClock(Now) };
            IMysteryStore store = repositories;
            user = (await repositories.CreateAsync("mystery-account", "mystery-player", default)).UserNum;
            await repositories.ReplaceSlotsAsync(user, InventoryBag.CharacterBag,
                [new(user, InventoryBag.CharacterBag, 1, Coin, 10, 0) { Lifetime = InventoryLifetime.Permanent }], default);
            var opened = await store.EnsureAsync(new(user, Scope, Board(), 60), default);
            Assert.Equal(MysteryStoreOutcome.SelectionCreated, opened.Outcome);
            var result = await store.PurchaseAsync(new(new(user, Scope), Revision, opened.Selection!.Token, OfferKey, 3), default);
            Assert.Equal(MysteryStoreOutcome.Purchased, result.Outcome);
            bought = result.Selection!;
        }
        using (var database = new SqliteDatabase(Options))
        {
            var repositories = new SqliteRepositories(database) { MysteryStoreClock = new FixedClock(Now.AddSeconds(1)) };
            IMysteryStore store = repositories;
            var reopened = await store.EnsureAsync(new(user, Scope, Board(), 60), default);
            Assert.Equal(MysteryStoreOutcome.Unchanged, reopened.Outcome);
            var persisted = Assert.IsType<MysteryStoreSelection>(reopened.Selection);
            Assert.Equal(bought.UserNum, persisted.UserNum);
            Assert.Equal(bought.ScopeKey, persisted.ScopeKey);
            Assert.Equal(bought.Token, persisted.Token);
            Assert.Equal(bought.ExpiresAtUnixMilliseconds, persisted.ExpiresAtUnixMilliseconds);
            Assert.Equal(bought.PurchasedMask, persisted.PurchasedMask);
            Assert.Equal(bought.Board.CatalogRevision, persisted.Board.CatalogRevision);
            Assert.Equal(bought.Board.CoinItemId, persisted.Board.CoinItemId);
            Assert.Equal(bought.Board.RefreshCoinCost, persisted.Board.RefreshCoinCost);
            Assert.Equal(bought.Board.GroupStockVariants, persisted.Board.GroupStockVariants);
            Assert.Equal(bought.Board.Offers.Count, persisted.Board.Offers.Count);
            for (var index = 0; index < bought.Board.Offers.Count; index++)
            {
                var expected = bought.Board.Offers[index];
                var actual = persisted.Board.Offers[index];
                Assert.Equal(expected.NativeKey, actual.NativeKey);
                Assert.Equal(expected.CoinPrice, actual.CoinPrice);
                Assert.Equal(expected.Unknown4, actual.Unknown4);
                Assert.Equal(expected.UnknownName, actual.UnknownName);
                Assert.Equal(expected.Grants.ToArray(), actual.Grants.ToArray());
            }
            var replay = await store.PurchaseAsync(new(new(user, Scope), Revision, bought.Token, OfferKey, 3), default);
            Assert.Equal(MysteryStoreOutcome.AlreadyPurchased, replay.Outcome);
            Assert.Equal(7, replay.Inventory!.CharacterBag.Single(row => row.ItemId == Coin).Quantity);
        }
    }

    /// <summary>A truncated durable mask is corruption rather than an unpurchased board.</summary>
    [Fact]
    public async Task MissingPurchasedMaskFailsClosed()
    {
        using var database = new SqliteDatabase(Options);
        var repositories = new SqliteRepositories(database) { MysteryStoreClock = new FixedClock(Now) };
        var user = (await repositories.CreateAsync("corrupt-account", "corrupt-player", default)).UserNum;
        IMysteryStore store = repositories;
        await store.EnsureAsync(new(user, Scope, Board(), 60), default);
        database.Write((db, transaction) => db.RawExecute(
            "UPDATE players SET state=json_remove(state, '$.MysteryStores.\"kr-1256\".PurchasedMask') WHERE user_num=$user",
            transaction, ("$user", user)), default);
        await Assert.ThrowsAnyAsync<Exception>(async () => await store.EnsureAsync(new(user, Scope, Board(), 60), default));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static MysteryStoreBoard Board() => new(Revision, Coin, 2, false,
        Enumerable.Range(0, 6).Select(index => new MysteryStoreOffer(OfferKey + index, 3, 0, string.Empty,
            [new(101 + index, InventoryGrantKind.Count, 2, 99, false)])).ToArray());

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
