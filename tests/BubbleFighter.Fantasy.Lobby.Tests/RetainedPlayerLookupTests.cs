using System.Data.Common;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Lobby.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class RetainedPlayerLookupTests
{
    [Fact]
    public async Task SqlLookupReadsExactIdentityWithoutWritingOrProvisioning()
    {
        // This exercises the portable SELECT and its mapping, not a live MySQL deployment.
        var connectionString = new SqliteConnectionStringBuilder
        { DataSource = "retained-player-" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared }.ToString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using (var command = anchor.CreateCommand())
        {
            command.CommandText = "CREATE TABLE players(user_num INTEGER PRIMARY KEY,username TEXT,nickname TEXT,level INTEGER,color_index INTEGER); "
                + "INSERT INTO players VALUES(7,'account','Saved name',23,14),(8,'other','Other name',2,0),(2147483648,'high','High identity',4,3);";
            await command.ExecuteNonQueryAsync();
        }
        var connections = new ReadOnlyConnections(connectionString);
        var repository = new MySqlPlayerRepository(connections);
        Assert.True(repository.SupportsRetainedAccountLookup);
        var found = await repository.FindByUserNumAsync(7, default);
        Assert.NotNull(found);
        Assert.Equal(7, found.UserNum);
        Assert.Equal("account", found.Username);
        Assert.Equal("Saved name", found.Nickname);
        Assert.Equal(23, found.Level);
        Assert.Equal((ushort)14, found.ColorIndex);
        Assert.Equal(2147483648L, (await repository.FindByUserNumAsync(2147483648L, default))!.UserNum);
        Assert.Null(await repository.FindByUserNumAsync(9, default));
        Assert.Equal(3, connections.Opens);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.FindByUserNumAsync(0, default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.FindByUserNumAsync(7, new CancellationToken(true)).AsTask());
        Assert.Equal(3, connections.Opens);
    }

    private sealed class ReadOnlyConnections(string connectionString) : IDbConnectionFactory
    {
        internal int Opens;
        public async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
        {
            var connection = new SqliteConnection(connectionString);
            try
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA query_only=ON";
                await command.ExecuteNonQueryAsync(ct);
                Opens++;
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
    }
}
