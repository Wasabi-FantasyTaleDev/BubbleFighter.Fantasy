using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>The file-backed store applies the same conversion guard and keeps one card spent across a reopen.</summary>
public sealed class ConversionGuardTests : IDisposable
{
    private const int Card = 190151;
    private const int Target = 190035;
    private static readonly DateTimeOffset Now = new(2099, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [Card, Target]);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-conversion-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };

    [Fact]
    public async Task ConversionCommitsOnceAndSurvivesReopen()
    {
        long user;
        InventoryLaboratoryCommand command;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = (await store.CreateAsync("conversion-account", "conversion-player", default)).UserNum;
            InventorySlotRecord[] rows =
            [
                new(user, InventoryBag.CharacterBag, 1, Card, 3, 0) { Lifetime = InventoryLifetime.Permanent },
                new(user, InventoryBag.CharacterBag, 2, Target, 1, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(7)) },
            ];
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
            command = new(user, Scope, [new(rows[0], 1, false)], [new(new(Target, InventoryGrantKind.Permanent, 1, 1), rows[1])], Now)
            {
                Conversions = [new(Target, 86_400_000, 0x79)],
            };
            var converted = Assert.Single(Assert.IsType<InventoryLaboratoryResult>(await store.TryLaboratoryAsync(command, default)).GrantedItems);
            Assert.Equal(InventoryLifetimeKind.Permanent, converted.Lifetime.Kind);
            Assert.Equal(2, converted.SlotKey);
            Assert.Null(await store.TryLaboratoryAsync(command, default));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var reopened = (await store.ReadLaboratoryInventoryAsync(user, Scope, default)).CharacterBag;
            Assert.Equal(2, reopened.Single(row => row.ItemId == Card).Quantity);
            Assert.Equal(InventoryLifetimeKind.Permanent, reopened.Single(row => row.ItemId == Target).Lifetime.Kind);
            // A request rebuilt from the reopened rows finds a permanent target and spends nothing.
            var rebuilt = new InventoryLaboratoryCommand(user, Scope, [new(reopened.Single(row => row.ItemId == Card), 1, false)],
                [new(new(Target, InventoryGrantKind.Permanent, 1, 1), reopened.Single(row => row.ItemId == Target))], Now)
            {
                Conversions = [new(Target, 86_400_000, 0x79)],
            };
            Assert.Null(await store.TryLaboratoryAsync(rebuilt, default));
            Assert.Equal(2, (await store.ReadLaboratoryInventoryAsync(user, Scope, default)).CharacterBag.Single(row => row.ItemId == Card).Quantity);
        }
    }

    [Fact]
    public async Task ATimerThatRunsOutBeforeTheWriteSpendsNothing()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = (await store.CreateAsync("conversion-expiry", "conversion-expiry", default)).UserNum;
        InventorySlotRecord[] rows =
        [
            new(user, InventoryBag.CharacterBag, 1, Card, 3, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 2, Target, 1, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(2)) },
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        var command = new InventoryLaboratoryCommand(user, Scope, [new(rows[0], 1, false)],
            [new(new(Target, InventoryGrantKind.Permanent, 1, 1), rows[1])], Now.AddDays(1).AddSeconds(1)) { Conversions = [new(Target, 86_400_000, 0x79)] };
        Assert.Null(await store.TryLaboratoryAsync(command, default));
        Assert.Equal(rows, (await store.ReadLaboratoryInventoryAsync(user, Scope, default)).CharacterBag);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
