using System.Text.Json;
using System.Text.Json.Nodes;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks atomic pet growth, consumable updates and competing inventory changes for SQLite.</summary>
public sealed class PetMutationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-pets-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteOptions Options => new() { DatabasePath = DatabasePath };
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-812", [301001, 100643, 200001]);
    private static readonly PetProgression Initial = new(0, 95, Now.AddHours(1), null, null, null);
    private static readonly PetProgression Grown = new(1, 0, Now.AddHours(8), Now, new(1, 4, 0, 0, 0), null);

    /// <summary>SQLite reopening preserves concurrent purchase and feeding changes in both bags, including hidden rows.</summary>
    [Fact]
    public async Task FeedAndConcurrentPurchaseSurviveReopenWithBothBagCopiesAndHiddenRows()
    {
        long user;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await SeedAsync(store);
            var expected = Pet(user);
            await Task.WhenAll(Task.Run(async () => Assert.NotNull(await store.MutatePetAsync(Command(expected), default))),
                Task.Run(async () => Assert.NotNull(await store.PurchaseAsync(Purchase(user, [new(200001, 7)]), default))));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var saved = await store.ListSlotsAsync(user, bag, 512, default);
                Assert.Equal(Pet(user) with { Bag = bag, Pet = State(Grown) }, saved.Single(row => row.ItemId == 301001));
                Assert.Equal(1, saved.Single(row => row.ItemId == 100643).Quantity);
                Assert.Equal(Foreign(user) with { Bag = bag }, saved.Single(row => row.ItemId == 999001));
            }
            var projected = await store.ListLoadoutSlotsAsync(user, Scope, InventoryBag.CharacterBag, 512, default);
            Assert.DoesNotContain(projected, row => row.ItemId == 999001);
            Assert.Equal(7, projected.Single(row => row.ItemId == 200001).Quantity);
            Assert.Null(await store.MutatePetAsync(Command(Pet(user)), default));
        }
    }

    /// <summary>Missing food and duplicate SQLite plans cannot leave partially applied pet feeding.</summary>
    [Fact]
    public async Task MissingFoodAndConcurrentDuplicatePlansCannotPartiallyFeed()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await SeedAsync(store, 0);
        Assert.Null(await store.MutatePetAsync(Command(Pet(user)), default));
        Assert.Equal(State(Initial), (await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default))[0].Pet);
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Pet(user), Food(user, 1)], default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag,
            [Pet(user) with { Bag = InventoryBag.EquippedBag }, Food(user, 1) with { Bag = InventoryBag.EquippedBag }], default);
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await store.MutatePetAsync(Command(Pet(user)), default))));
        Assert.Single(results, result => result is not null);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal(State(Grown), Assert.Single(await store.ListSlotsAsync(user, bag, 512, default)).Pet);
    }

    /// <summary>Cancellation after writing the SQLite snapshot restores both food and progression.</summary>
    [Fact]
    public async Task CancellationAfterTheSnapshotWriteRollsBackFoodAndGrowth()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await SeedAsync(store);
        using var cancellation = new CancellationTokenSource();
        var connection = Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection);
        connection.CreateFunction("cancel_pet_write", () => { cancellation.Cancel(); return 0; });
        database.Write((db, transaction) => db.RawExecute(
            "CREATE TRIGGER cancel_pet_save AFTER UPDATE OF state ON players BEGIN SELECT cancel_pet_write(); END;", transaction), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MutatePetAsync(Command(Pet(user)), cancellation.Token).AsTask());
        Assert.True(cancellation.IsCancellationRequested);
        database.Write((db, transaction) => db.RawExecute("DROP TRIGGER cancel_pet_save;", transaction), default);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var saved = await store.ListSlotsAsync(user, bag, 512, default);
            Assert.Equal(State(Initial), saved.Single(row => row.ItemId == 301001).Pet);
            Assert.Equal(2, saved.Single(row => row.ItemId == 100643).Quantity);
        }
    }

    /// <summary>Reopened pet state compares structurally and ordinary renewal retains its skill vector.</summary>
    [Fact]
    public async Task ReopenedPetComparesStructurallyAndRenewalPreservesItsSkills()
    {
        long user;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await SeedAsync(store);
            await store.MutatePetAsync(Command(Pet(user)), default);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            await Assert.ThrowsAsync<ShopPurchaseConflictException>(() => store.PurchaseAsync(Renew(Pet(user)), default).AsTask());
            var expected = Pet(user) with { Pet = State(Grown with { FirstSkill = new(1, 4, 0, 0, 0) }) };
            var renewed = await store.PurchaseAsync(Renew(expected), default);
            Assert.Equal(State(Grown), Assert.Single(renewed!.Grants).Pet);
            var saved = (await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default))[0];
            Assert.Equal(State(Grown), saved.Pet);
            Assert.Equal(2, saved.Quantity);
        }
    }

    /// <summary>Schema-ten inventory rows upgrade without manufacturing pet progression for historical items.</summary>
    [Fact]
    public async Task VersionTenRowsWithoutPetMetadataUpgradeWithoutInventingProgression()
    {
        long user;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await SeedAsync(store);
        }
        using (var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM players WHERE user_num=$user";
            command.Parameters.AddWithValue("$user", user);
            var state = JsonNode.Parse((string)command.ExecuteScalar()!)!;
            foreach (var slot in state["Slots"]!.AsArray()) slot!.AsObject().Remove("Pet");
            command.CommandText = "UPDATE players SET state=$state WHERE user_num=$user; DROP TABLE IF EXISTS pet_identity_allocator; PRAGMA user_version=10;";
            command.Parameters.AddWithValue("$state", state.ToJsonString());
            command.ExecuteNonQuery();
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            Assert.All(await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default), row => Assert.Null(row.Pet));
            Assert.Equal(13L, database.RawScalar("PRAGMA user_version"));
            Assert.NotNull(await store.MutatePetAsync(Command(Pet(user) with { Pet = null }), default));
        }
    }

    /// <summary>Truncated SQLite pet JSON is rejected instead of supplying valid-looking defaults.</summary>
    [Theory]
    [InlineData("Experience")]
    [InlineData("FullnessExpiresAt")]
    [InlineData("FirstSkill")]
    public void TruncatedPetStateFailsInsteadOfSupplyingDefaults(string missing)
    {
        var state = new SaveState { Slots = [Pet(1) with { Pet = State(Grown) }] };
        var json = JsonNode.Parse(SaveJson.Encode(state))!;
        json["Slots"]![0]!["Pet"]!["kr-812"]!.AsObject().Remove(missing);
        Assert.Throws<JsonException>(() => SaveJson.Decode(json.ToJsonString()));
    }

    /// <summary>Separate client builds retain independent pet progression after SQLite reopening.</summary>
    [Fact]
    public async Task BuildsKeepIndependentGrowthAfterRestart()
    {
        long user;
        var modern = Initial with { Experience = 17, FullnessExpiresAt = Now.AddHours(2), LastFedAt = Now };
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await SeedAsync(store);
            var first = await store.MutatePetAsync(Command(Pet(user)), default);
            var expected = first!.Single(row => row.ItemId == 301001);
            Assert.NotNull(await store.MutatePetAsync(Command(expected) with
            {
                Scope = new("kr-1256", Scope.SupportedItemIds), Progression = modern,
            }, default));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var saved = (await store.ListSlotsAsync(user, bag, 512, default)).Single(row => row.ItemId == 301001);
                Assert.Equal(Grown, saved.Pet!.Find("kr-812"));
                Assert.Equal(modern, saved.Pet.Find("kr-1256"));
                Assert.Equal(State(Grown).With("kr-1256", modern), saved.Pet);
            }
        }
    }

    /// <summary>Exceeding the progression-build capacity rolls back the entire SQLite pet mutation.</summary>
    [Fact]
    public async Task ConfiguredBuildCapacityRollsBackTheWholePetMutation()
    {
        using var database = new SqliteDatabase(new() { DatabasePath = DatabasePath, MaxLoadoutsPerPlayer = 1 });
        var store = new SqliteRepositories(database);
        var user = await SeedAsync(store);
        var command = Command(Pet(user)) with { Scope = new("kr-1256", Scope.SupportedItemIds) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.MutatePetAsync(command, default).AsTask());
        var rows = await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default);
        Assert.Equal(Pet(user), rows.Single(row => row.ItemId == 301001));
        Assert.Equal(2, rows.Single(row => row.ItemId == 100643).Quantity);
    }

    private static InventorySlotRecord Pet(long user) => new(user, InventoryBag.CharacterBag, 10, 301001, 1, 0x81)
        { Lifetime = InventoryLifetime.Permanent, Pet = State(Initial) };
    private static InventorySlotRecord Food(long user, int count) => new(user, InventoryBag.CharacterBag, 11, 100643, count, 0x40);
    private static InventorySlotRecord Foreign(long user) => new(user, InventoryBag.CharacterBag, 12, 999001, 3, 0x21);
    private static PetProgressions State(PetProgression progression) => PetProgressions.Empty.With("kr-812", progression);

    private static PetMutationCommand Command(InventorySlotRecord expected) => new(expected, Scope, Grown, 100643, true, Now);
    private static ShopPurchaseCommand Purchase(long user, IReadOnlyList<ShopPurchaseGrant> grants) =>
        new(new(user, 100, 0), 42, 0, "lucci", 10, 1000, grants);
    private static ShopPurchaseCommand Renew(InventorySlotRecord expected) => Purchase(expected.UserNum, [
        new(expected.ItemId, 2) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = expected, Lifetime = InventoryLifetime.Permanent },
    ]);

    private static async Task<long> SeedAsync(SqliteRepositories store, int foodCount = 2)
    {
        var user = (await store.CreateAsync("pet-account", "pet-player", default)).UserNum;
        InventorySlotRecord[] rows = [Pet(user), Food(user, foodCount), Foreign(user)];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return user;
    }

    /// <summary>Removes the temporary SQLite save and its journal files after the test releases its repositories.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
