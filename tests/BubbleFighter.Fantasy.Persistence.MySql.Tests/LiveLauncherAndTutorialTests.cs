using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Persistence.MySql;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

// Runs the launcher session, launcher registration and tutorial stores against a real server. Every test returns
// early unless BFF_MYSQL_VERIFY_CONNECTION names a scratch database (never a live one); rows use unique names.
public sealed class LiveLauncherAndTutorialTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("BFF_MYSQL_VERIFY_CONNECTION");
    private static readonly Lazy<Task<MySqlConnectionFactory>> Ready = new(async () =>
    {
        var factory = new MySqlConnectionFactory(ConnectionString!);
        await new MigrationRunner(factory, NullLogger.Instance, typeof(MySqlPlugin).Assembly).ApplyAsync(default);
        return factory;
    });

    private static async Task<MySqlConnectionFactory?> FactoryAsync() =>
        ConnectionString is null ? null : await Ready.Value;

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..20];

    // MySQL DATETIME keeps whole seconds here, so the test clock does too.
    private static DateTime Now() => new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static async Task<long> NewPlayerAsync()
    {
        await using var db = new MySqlConnection(ConnectionString);
        await db.OpenAsync();
        var name = Unique("lt");
        await using var command = new MySqlCommand(
            "INSERT INTO players (username, nickname, level, created_at) VALUES (@n, @n, 1, UTC_TIMESTAMP()); SELECT LAST_INSERT_ID();", db);
        command.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SessionsAreFoundRotatedOnceAndRevoked()
    {
        if (await FactoryAsync() is not { } factory) return;
        var sessions = new MySqlLauncherSessions(factory);
        var now = Now();
        var id = Unique("s");
        var first = Unique("h");
        await sessions.CreateAsync(id, "player", first, now.AddHours(1), now, default);
        var found = await sessions.FindAsync(first, default);
        Assert.NotNull(found);
        Assert.Equal((id, "player", now.AddHours(1), (DateTime?)null), (found.SessionId, found.Username, found.ExpiresAt, found.RevokedAt));
        Assert.Equal(DateTimeKind.Utc, found.ExpiresAt.Kind);

        // Concurrent refreshes of one token: exactly one wins.
        var next = Unique("h");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            sessions.TryRotateAsync(id, first, i == 0 ? next : Unique("h"), now.AddHours(2), now, default).AsTask()));
        Assert.Single(results, result => result);
        Assert.Null(await sessions.FindAsync(first, default));
        Assert.False(await sessions.TryRotateAsync(id, first, Unique("h"), now.AddHours(2), now, default));

        // An expired session cannot rotate.
        var expiredId = Unique("s");
        var expired = Unique("h");
        await sessions.CreateAsync(expiredId, "player", expired, now.AddSeconds(-1), now, default);
        Assert.False(await sessions.TryRotateAsync(expiredId, expired, Unique("h"), now.AddHours(1), now, default));

        // Revocation sticks, and a revoked session cannot rotate.
        var liveId = Unique("s");
        var live = Unique("h");
        await sessions.CreateAsync(liveId, "player", live, now.AddHours(1), now, default);
        await sessions.RevokeAsync(liveId, now, default);
        await sessions.RevokeAsync(liveId, now.AddMinutes(5), default);
        Assert.Equal(now, (await sessions.FindAsync(live, default))!.RevokedAt);
        Assert.False(await sessions.TryRotateAsync(liveId, live, Unique("h"), now.AddHours(1), now, default));

        // Creating a session removes sessions expired or revoked more than the retention period ago.
        var oldId = Unique("s");
        var old = Unique("h");
        await sessions.CreateAsync(oldId, "player", old, now.AddDays(-30), now.AddDays(-31), default);
        await sessions.CreateAsync(Unique("s"), "player", Unique("h"), now.AddHours(1), now, default);
        Assert.Null(await sessions.FindAsync(old, default));
        Assert.NotNull(await sessions.FindAsync(live, default));
    }

    [Fact]
    public async Task RegistrationRefusesADuplicateAndAPlayerName()
    {
        if (await FactoryAsync() is not { } factory) return;
        var accounts = new MySqlLauncherAccounts(factory);
        var name = Unique("r");
        Assert.True(await accounts.RegisterAsync(name, "hash", default));
        Assert.False(await accounts.RegisterAsync(name, "other", default));
        Assert.Equal("hash", (await accounts.FindAsync(name, default))!.PasswordHash);
        var contested = Unique("c");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => accounts.RegisterAsync(contested, "h", default).AsTask()));
        Assert.Single(results, result => result);
    }

    [Fact]
    public async Task TutorialClearsKeepTheBestRecordPerStage()
    {
        if (await FactoryAsync() is not { } factory) return;
        var stages = new MySqlTutorialStages(factory);
        var user = await NewPlayerAsync();
        Assert.Empty(await stages.ListAsync(user, default));
        Assert.True(await stages.RecordClearAsync(user, new TutorialStageClear(2, 5, 90), default));
        Assert.False(await stages.RecordClearAsync(user, new TutorialStageClear(2, 3, 120), default));
        Assert.False(await stages.RecordClearAsync(user, new TutorialStageClear(2, 7, 0), default));
        Assert.False(await stages.RecordClearAsync(user, new TutorialStageClear(2, 1, 60), default));
        Assert.True(await stages.RecordClearAsync(user, new TutorialStageClear(1, 2, 0), default));
        Assert.False(await stages.RecordClearAsync(user, new TutorialStageClear(1, 2, 45), default));
        Assert.Equal([new TutorialStageClear(1, 2, 45), new TutorialStageClear(2, 7, 60)], await stages.ListAsync(user, default));
    }
}
