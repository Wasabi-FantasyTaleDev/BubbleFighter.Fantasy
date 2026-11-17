using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using BubbleFighter.Fantasy.Persistence.Tests;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Runs the shared clan directory contract over the SQLite save and pins its upgrade step and restart behaviour.</summary>
public sealed class SqliteClanDirectoryTests : ClanDirectoryContractTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-clan-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteDatabase> _databases = [];

    private string DatabasePath => Path.Combine(_directory, "save.sqlite");

    private SqliteDatabase Open()
    {
        var database = new SqliteDatabase(new SqliteOptions
        {
            DatabasePath = DatabasePath, MaxPlayers = 64, MaxAccounts = 64, MaxDatabaseBytes = 4_194_304,
        });
        _databases.Add(database);
        return database;
    }

    /// <inheritdoc />
    protected override ValueTask<IClanDirectory> CreateEmptyAsync() => new(new SqliteClanDirectory(Open()));

    [Fact]
    public async Task ClansMembersAndPostsSurviveARestart()
    {
        var first = Open();
        var clans = new SqliteClanDirectory(first);
        var clan = (await clans.CreateAsync("Alpha", "hi", 30, 7, "Seven", 3, default))!;
        Assert.True(await clans.AddMemberAsync(clan.ClanId, 8, "Eight", 4, ClanRole.Applicant, default));
        Assert.True(await clans.SeatAsync(clan.ClanId, 8, ClanRole.Member, default));
        var post = await clans.AddPostAsync(clan.ClanId, 8, "Eight", 4, "hello", default);
        var disband = new DateTime(2026, 11, 23, 4, 5, 6, 789, DateTimeKind.Utc);
        Assert.True(await clans.UpdateAsync(clan with { Notice = "n", DisbandAt = disband }, default));
        first.Dispose();
        _databases.Remove(first);

        var reopened = new SqliteClanDirectory(Open());
        var stored = (await reopened.FindAsync(clan.ClanId, default))!;
        Assert.Equal(("Alpha", "n", 2, disband), (stored.Name, stored.Notice, stored.MemberCount, stored.DisbandAt!.Value));
        Assert.Equal(ClanRole.Member, (await reopened.FindMemberAsync(8, default))!.Role);
        Assert.Equal(post, (await reopened.FindPostAsync(clan.ClanId, post.PostId, default))!);
    }

    [Fact]
    public async Task AVersion17SaveUpgradesAndNumbersClansFrom1000()
    {
        Open().Dispose();
        _databases.Clear();
        // Roll a fresh save back to version 17, as a save written before the clan directory existed.
        using (var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE clan_posts; DROP TABLE clan_members; DROP TABLE clans; "
                + "DELETE FROM sqlite_sequence WHERE name IN ('clans', 'clan_posts'); PRAGMA user_version=17;";
            command.ExecuteNonQuery();
        }
        var database = Open();
        Assert.Equal(18L, database.RawScalar("PRAGMA user_version"));
        var clan = await new SqliteClanDirectory(database).CreateAsync("Alpha", "", 30, 7, "Seven", 1, default);
        Assert.Equal(1000, clan!.ClanId);
    }

    /// <summary>Closes every save this test opened and deletes its folder.</summary>
    public void Dispose()
    {
        foreach (var database in _databases) database.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
