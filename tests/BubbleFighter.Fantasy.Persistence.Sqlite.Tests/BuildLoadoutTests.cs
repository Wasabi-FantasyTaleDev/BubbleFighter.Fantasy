using System.Text.Json.Nodes;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class BuildLoadoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-loadouts-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteOptions Options => new() { DatabasePath = DatabasePath, MaxLoadoutsPerPlayer = 4 };
    private static readonly InventoryLoadoutScope Korean = new("kr-1256", [300001, 300002, 300029, 202001, 204999]);
    private static readonly InventoryLoadoutScope Older = new("cn-279", [300001, 300002, 202001]);
    private static readonly InventoryWearGroup[] Groups = [new(300001, 300031, 300001, true), new(202000, 204999, 202001, true)];

    [Fact]
    public async Task VersionSevenSaveMigratesAndRetainsIndependentSelectionsAfterRestart()
    {
        long user;
        InventorySlotRecord[] original;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var player = await store.CreateCharacterAsync("loadout-account", "loadout-player",
                new(Korean.SupportedItemIds, [300029, 204999]), default);
            user = player!.UserNum;
            await store.SetActiveWeaponPresetSlotAsync(user, 4, default);
            await store.SetWeaponPresetPagesAsync(user, WeaponPresetPages.SeedFromWorn(204999, 0), default);
            original = (await store.ListSlotsAsync(user, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default)).ToArray();
        }
        using (var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM players WHERE user_num=$user";
            command.Parameters.AddWithValue("$user", user);
            var json = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            json.Remove("Loadouts");
            command.CommandText = "UPDATE players SET state=$state WHERE user_num=$user; PRAGMA user_version=7;";
            command.Parameters.AddWithValue("$state", json.ToJsonString());
            command.ExecuteNonQuery();
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var old = await store.UpdateLoadoutSelectionAsync(user, Older, Groups, null, false, default);
            Assert.DoesNotContain(old!, row => row.ItemId is 300029 or 204999);
            Assert.Contains(old!, row => row.ItemId == 300001 && (row.Flags & 1) != 0);
            await store.UpdateLoadoutSelectionAsync(user, Older, Groups, 300002, true, default);
            await store.UpdateLoadoutPresetsAsync(user, Older, 1, WeaponPresetPages.SeedFromWorn(202001, 0), false, default);
            var korean = await store.UpdateLoadoutSelectionAsync(user, Korean, Groups, null, false, default);
            Assert.Contains(korean!, row => row.ItemId == 300029 && (row.Flags & 1) != 0);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var old = await store.GetLoadoutAsync(user, Older, default);
            Assert.Contains(300002, old.WornItemIds!);
            Assert.DoesNotContain(300029, old.WornItemIds!);
            Assert.Equal(1, old.ActivePresetSlot);
            Assert.All(old.PresetPages!, page => Assert.Equal(202001, page.Unknown0));
            var korean = await store.GetLoadoutAsync(user, Korean, default);
            Assert.Contains(300029, korean.WornItemIds!);
            Assert.Equal(4, korean.ActivePresetSlot);
            Assert.All(korean.PresetPages!, page => Assert.Equal(204999, page.Unknown0));
            Assert.Equal(original, await store.ListSlotsAsync(user, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
            Assert.Equal(4, await store.GetActiveWeaponPresetSlotAsync(user, default));
        }
    }

    [Fact]
    public async Task RefusedWearAndCancelledPresetLeaveStoredLoadoutIntact()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var player = await store.CreateCharacterAsync("guard-account", "guard-player", new([300001, 202001], [300001, 202001]), default);
        var user = player!.UserNum;
        await store.UpdateLoadoutSelectionAsync(user, Older, Groups, null, false, default);
        Assert.Null(await store.UpdateLoadoutSelectionAsync(user, Older, Groups, 300029, true, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateLoadoutPresetsAsync(user, Older, 3,
            WeaponPresetPages.SeedFromWorn(202001, 0), false, cancellation.Token).AsTask());
        var after = await store.GetLoadoutAsync(user, Older, default);
        Assert.Equal(0, after.ActivePresetSlot);
        Assert.Contains(300001, after.WornItemIds!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
