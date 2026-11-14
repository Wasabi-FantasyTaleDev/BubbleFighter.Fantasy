using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Tests.Shared;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class OwnedInventorySelectionTests : OwnedInventorySelectionContractTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-owned-selection-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _database;

    public OwnedInventorySelectionTests()
    {
        _database = new(new() { DatabasePath = Path.Combine(_directory, "save.sqlite") });
        var repository = new SqliteRepositories(_database);
        UserNum = repository.CreateCharacterAsync("owned-account", "owned-player", new([300001], [300001]), default)
            .AsTask().GetAwaiter().GetResult()!.UserNum;
        Store = repository;
    }

    protected override IInventoryLoadoutStore Store { get; }
    protected override long UserNum { get; }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
