using BubbleFighter.Fantasy.Contracts.Messenger;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using BubbleFighter.Fantasy.Persistence.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Runs the shared messenger store contract over the SQLite save and pins that its state survives a restart.</summary>
public sealed class SqliteMessengerStoreTests : MessengerStoreContractTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-messenger-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteDatabase> _databases = [];

    private SqliteDatabase Open()
    {
        var database = new SqliteDatabase(new SqliteOptions
        {
            DatabasePath = Path.Combine(_directory, "save.sqlite"), MaxPlayers = 64, MaxAccounts = 64, MaxDatabaseBytes = 4_194_304,
        });
        _databases.Add(database);
        return database;
    }

    /// <inheritdoc />
    protected override IMessengerStore CreateStore() => new SqliteMessengerStore(Open());

    [Fact]
    public async Task FriendshipsCategoriesAndRequestsSurviveARestart()
    {
        var limits = new MessengerLimits(10, 10, 10);
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var first = Open();
        var store = new SqliteMessengerStore(first);
        var a = await store.UpsertUserAsync(new("local", "a", "A", 1), CancellationToken.None);
        var b = await store.UpsertUserAsync(new("local", "b", "B", 1), CancellationToken.None);
        var c = await store.UpsertUserAsync(new("local", "c", "C", 1), CancellationToken.None);
        var (_, request) = await store.CreateRequestAsync(a.Id, b.Id, "hi", now, limits, CancellationToken.None);
        await store.ResolveRequestAsync(request!.Handle, b.Id, true, limits, CancellationToken.None);
        await store.AddCategoryAsync(a.Id, "Team", 1, limits, CancellationToken.None);
        await store.SetFriendCategoryAsync(a.Id, b.Id, 0, 1, CancellationToken.None);
        await store.CreateRequestAsync(c.Id, a.Id, "later", now, limits, CancellationToken.None);
        first.Dispose();
        _databases.Remove(first);

        var reopened = new SqliteMessengerStore(Open());
        var relations = await reopened.LoadRelationsAsync(a.Id, CancellationToken.None);
        Assert.Equal((b.Id, 1), (Assert.Single(relations.Relations).Friend.Id, relations.Relations[0].CategoryId));
        Assert.Equal("Team", Assert.Single(relations.Categories).Name);
        Assert.Equal(c.Id, Assert.Single(await reopened.ListIncomingRequestsAsync(a.Id, CancellationToken.None)).From);
        Assert.Equal(a.Id, (await reopened.UpsertUserAsync(new("local", "a", "A", 1), CancellationToken.None)).Id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var database in _databases) database.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
