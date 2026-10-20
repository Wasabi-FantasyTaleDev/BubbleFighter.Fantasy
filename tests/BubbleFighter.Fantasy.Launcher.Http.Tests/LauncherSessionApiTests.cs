using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

/// <summary>Durable sign-ins over real HTTP: login, refresh rotation, restarts, logout and launch identity.</summary>
public sealed class LauncherSessionApiTests
{
    private const string Password = "Local-test-123";

    private static LauncherOptions Options() => new()
    {
        Listen = "127.0.0.1:0", PasswordIterations = 100000, SessionSeconds = 1800, RefreshSessionSeconds = 2592000,
        Instances = [new() { Id = "kr-812", Build = "kr-812", Name = [new() { Language = "en", Text = "KR 2015" }], Port = 7110 }],
    };

    private sealed class Deployment(LauncherOptions options, ILauncherAccountRepository accounts, ILauncherSessionStore store, TimeProvider? clock = null)
    {
        internal LauncherSessions Sessions { get; private set; } = null!;
        internal LauncherServer Server { get; private set; } = null!;
        internal HttpClient Http { get; private set; } = null!;

        // A fresh LauncherSessions over the same store is what a server restart leaves: bearers and tickets are gone.
        internal async Task StartAsync()
        {
            Sessions = new LauncherSessions(options, store, clock);
            Server = await LauncherServer.StartAsync(options, accounts, Sessions);
            Http = new HttpClient { BaseAddress = new Uri(Server.Addresses.Single()) };
        }

        internal async Task StopAsync() { Http.Dispose(); await Server.DisposeAsync(); }
    }

    private static async Task<(MemoryLauncherAccounts Accounts, MemoryLauncherSessions Store)> SeedAsync(params string[] names)
    {
        var accounts = new MemoryLauncherAccounts(10);
        foreach (var name in names) Assert.True(await accounts.RegisterAsync(name, "", default));
        return (accounts, new MemoryLauncherSessions(10));
    }

    private static async Task<JsonElement> LoginAsync(HttpClient http, string username)
    {
        // Seeded identities have no password yet; the first launcher login enrolls it, as in production.
        using var response = await http.PostAsJsonAsync("v1/auth/login", new { username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient http, string refreshToken) =>
        http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken });

    private static async Task<HttpResponseMessage> LogoutAsync(HttpClient http, string? refreshToken, string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/logout")
            { Content = JsonContent.Create(refreshToken is null ? new Dictionary<string, string>() : new() { ["refreshToken"] = refreshToken }) };
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await http.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAccountAsync(HttpClient http, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await http.SendAsync(request);
    }

    private static async Task<string> LaunchAsync(HttpClient http, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/launch") { Content = JsonContent.Create(new { build = "kr-812" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
    }

    [Fact]
    public async Task NewSignInRevokesOldestAccessRefreshAndTicketsAcrossSessionServiceRestart()
    {
        var options = Options(); options.MaxSessionsPerAccount = 1; options.MaxSessions = 1;
        var store = new MemoryLauncherSessions(10);
        var sessions = new LauncherSessions(options, store);
        var old = await sessions.SignInAsync("player", default);
        var ticket = sessions.Issue(old.AccessToken, "kr-812").Token;
        var current = await sessions.SignInAsync("player", default);
        Assert.Null(sessions.Account(old.AccessToken));
        Assert.Null(sessions.Redeem(ticket, "kr-812"));
        Assert.Equal("player", sessions.Account(current.AccessToken));
        var restarted = new LauncherSessions(options, store);
        Assert.Null(await restarted.RefreshAsync(old.RefreshToken, default));
        Assert.NotNull(await restarted.RefreshAsync(current.RefreshToken, default));
    }

    [Fact]
    public async Task LoginReturnsBothCredentialsWithOrderedExpiriesAndTheAccount()
    {
        var (accounts, store) = await SeedAsync("player");
        var options = Options(); options.AllowLegacyPasswordEnrollment = true;
        var deployment = new Deployment(options, accounts, store); await deployment.StartAsync();
        try
        {
            var login = await LoginAsync(deployment.Http, "player");
            var access = login.GetProperty("accessToken").GetString()!; var refresh = login.GetProperty("refreshToken").GetString()!;
            Assert.True(LauncherSessions.WellFormed(access)); Assert.True(LauncherSessions.WellFormed(refresh)); Assert.NotEqual(access, refresh);
            var accessExpiry = login.GetProperty("accessExpiresAt").GetDateTimeOffset();
            var refreshExpiry = login.GetProperty("refreshExpiresAt").GetDateTimeOffset();
            Assert.InRange(accessExpiry - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromHours(24));
            Assert.InRange(refreshExpiry - DateTimeOffset.UtcNow, TimeSpan.FromDays(29), TimeSpan.FromDays(365));
            Assert.True(refreshExpiry > accessExpiry);
            Assert.EndsWith("Z", login.GetProperty("accessExpiresAt").GetString());
            var account = login.GetProperty("account");
            Assert.Equal("player", account.GetProperty("username").GetString());
            Assert.Equal("player", account.GetProperty("displayName").GetString());
            Assert.Equal(JsonValueKind.Object, account.GetProperty("stats").ValueKind);
            Assert.False(login.TryGetProperty("token", out _));
        }
        finally { await deployment.StopAsync(); }
    }

    [Fact]
    public async Task RefreshRotatesAndTheOldCredentialIsRefused()
    {
        var (accounts, store) = await SeedAsync("player");
        var options = Options(); options.AllowLegacyPasswordEnrollment = true;
        var deployment = new Deployment(options, accounts, store); await deployment.StartAsync();
        try
        {
            var login = await LoginAsync(deployment.Http, "player");
            var first = login.GetProperty("refreshToken").GetString()!;
            using var renewed = await RefreshAsync(deployment.Http, first);
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            var body = await renewed.Content.ReadFromJsonAsync<JsonElement>();
            var second = body.GetProperty("refreshToken").GetString()!;
            Assert.NotEqual(first, second);
            Assert.Equal("player", body.GetProperty("account").GetProperty("username").GetString());
            using var replay = await RefreshAsync(deployment.Http, first);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            // The replay did not end the session: the rotated credential still renews it.
            using var again = await RefreshAsync(deployment.Http, second);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
        finally { await deployment.StopAsync(); }
    }

    [Fact]
    public async Task RefreshIssuedBeforeARestartStillResolvesTheSameAccountAndLaunchIdentity()
    {
        var (accounts, store) = await SeedAsync("player");
        var options = Options(); options.AllowLegacyPasswordEnrollment = true;
        var deployment = new Deployment(options, accounts, store); await deployment.StartAsync();
        var login = await LoginAsync(deployment.Http, "player");
        var oldAccess = login.GetProperty("accessToken").GetString()!;
        await deployment.StopAsync();

        await deployment.StartAsync();
        try
        {
            // The restarted server knows no bearer; the launcher's forced refresh after the 401 renews the sign-in.
            using (var stale = await GetAccountAsync(deployment.Http, oldAccess))
                Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
            using var renewed = await RefreshAsync(deployment.Http, login.GetProperty("refreshToken").GetString()!);
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            var body = await renewed.Content.ReadFromJsonAsync<JsonElement>();
            var access = body.GetProperty("accessToken").GetString()!;
            using (var retried = await GetAccountAsync(deployment.Http, access))
            {
                Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
                Assert.Equal("player", (await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());
            }
            Assert.Equal("player", deployment.Sessions.Redeem(await LaunchAsync(deployment.Http, access), "kr-812"));
        }
        finally { await deployment.StopAsync(); }
    }

    [Fact]
    public async Task ExpiredAndRevokedRefreshCredentialsAreRefusedWithoutTouchingOtherSessions()
    {
        var clock = new TestClock();
        var options = Options(); options.RefreshSessionSeconds = 3600;
        var store = new MemoryLauncherSessions(10);
        var sessions = new LauncherSessions(options, store, clock);
        var expiring = await sessions.SignInAsync("one", default);
        var revoked = await sessions.SignInAsync("two", default);
        var other = await sessions.SignInAsync("three", default);
        Assert.True(await sessions.SignOutAsync(revoked.RefreshToken, null, default));
        Assert.Null(await sessions.RefreshAsync(revoked.RefreshToken, default));
        clock.Advance(1800);
        var kept = await sessions.RefreshAsync(other.RefreshToken, default);
        Assert.NotNull(kept);
        clock.Advance(1801);
        Assert.Null(await sessions.RefreshAsync(expiring.RefreshToken, default));
        Assert.Null(await sessions.RefreshAsync("not-a-credential", default));
        Assert.Null(await sessions.RefreshAsync(new string('0', 64), default));
        Assert.Equal("three", (await sessions.RefreshAsync(kept!.RefreshToken, default))?.Username);
    }

    [Fact]
    public async Task ConcurrentRefreshesOfOneCredentialHaveExactlyOneWinner()
    {
        var (accounts, store) = await SeedAsync("player");
        var options = Options(); options.AllowLegacyPasswordEnrollment = true; options.MaxConcurrentRequests = 16;
        var deployment = new Deployment(options, accounts, store); await deployment.StartAsync();
        try
        {
            var refresh = (await LoginAsync(deployment.Http, "player")).GetProperty("refreshToken").GetString()!;
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RefreshAsync(deployment.Http, refresh)));
            try
            {
                Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
                Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            }
            finally { foreach (var response in responses) response.Dispose(); }
        }
        finally { await deployment.StopAsync(); }
    }

    [Fact]
    public async Task LogoutAfterARestartNeedsOnlyTheRefreshCredentialAndEndsPendingTickets()
    {
        var (accounts, store) = await SeedAsync("player");
        var options = Options(); options.AllowLegacyPasswordEnrollment = true;
        var deployment = new Deployment(options, accounts, store); await deployment.StartAsync();
        var login = await LoginAsync(deployment.Http, "player");
        var refresh = login.GetProperty("refreshToken").GetString()!;
        var ticket = await LaunchAsync(deployment.Http, login.GetProperty("accessToken").GetString()!);
        await deployment.StopAsync();
        await deployment.StartAsync();
        try
        {
            using (var logout = await LogoutAsync(deployment.Http, refresh, login.GetProperty("accessToken").GetString()))
            {
                Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
                Assert.True((await logout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signedOut").GetBoolean());
            }
            using (var renewed = await RefreshAsync(deployment.Http, refresh)) Assert.Equal(HttpStatusCode.Unauthorized, renewed.StatusCode);
            Assert.Null(deployment.Sessions.Redeem(ticket, "kr-812"));
            using var repeated = await LogoutAsync(deployment.Http, refresh, null);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        }
        finally { await deployment.StopAsync(); }
    }

    [Fact]
    public async Task LogoutRevokesOnlyItsOwnSessionAndRefusesMismatchedCredentials()
    {
        var (accounts, store) = await SeedAsync("one", "two");
        var options = Options(); options.AllowLegacyPasswordEnrollment = true;
        var deployment = new Deployment(options, accounts, store); await deployment.StartAsync();
        try
        {
            var one = await LoginAsync(deployment.Http, "one"); var two = await LoginAsync(deployment.Http, "two");
            var oneAccess = one.GetProperty("accessToken").GetString()!; var twoAccess = two.GetProperty("accessToken").GetString()!;
            var oneTicket = await LaunchAsync(deployment.Http, oneAccess); var twoTicket = await LaunchAsync(deployment.Http, twoAccess);
            using (var mismatch = await LogoutAsync(deployment.Http, one.GetProperty("refreshToken").GetString(), twoAccess))
                Assert.Equal(HttpStatusCode.Unauthorized, mismatch.StatusCode);
            using (var still = await GetAccountAsync(deployment.Http, oneAccess)) Assert.Equal(HttpStatusCode.OK, still.StatusCode);
            using (var logout = await LogoutAsync(deployment.Http, one.GetProperty("refreshToken").GetString(), oneAccess))
                Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
            using (var ended = await GetAccountAsync(deployment.Http, oneAccess)) Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
            Assert.Null(deployment.Sessions.Redeem(oneTicket, "kr-812"));
            using (var other = await GetAccountAsync(deployment.Http, twoAccess)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            Assert.Equal("two", deployment.Sessions.Redeem(twoTicket, "kr-812"));
            using (var renewed = await RefreshAsync(deployment.Http, two.GetProperty("refreshToken").GetString()!))
                Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        }
        finally { await deployment.StopAsync(); }
    }

    [Fact]
    public async Task ErrorResponsesCarryNoCredentialOrPassword()
    {
        var (accounts, store) = await SeedAsync("player");
        var options = Options(); options.AllowLegacyPasswordEnrollment = true;
        var deployment = new Deployment(options, accounts, store); await deployment.StartAsync();
        try
        {
            var login = await LoginAsync(deployment.Http, "player");
            var refresh = login.GetProperty("refreshToken").GetString()!; var access = login.GetProperty("accessToken").GetString()!;
            using var wrong = await deployment.Http.PostAsJsonAsync("v1/auth/login", new { username = "player", password = "Wrong-pass-999" });
            using var replay = await RefreshAsync(deployment.Http, (await (await RefreshAsync(deployment.Http, refresh)).Content
                .ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString() + "x");
            using var stale = await GetAccountAsync(deployment.Http, access);
            foreach (var response in new[] { wrong, replay, stale })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                var text = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Wrong-pass-999", text); Assert.DoesNotContain(Password, text);
                Assert.DoesNotContain(refresh, text); Assert.DoesNotContain(access, text);
            }
        }
        finally { await deployment.StopAsync(); }
    }

    [Fact]
    public void RefreshMustOutliveTheAccessBearer()
    {
        var options = Options(); options.RefreshSessionSeconds = options.SessionSeconds;
        Assert.Contains(options.Validate(), error => error.MemberNames.Contains(nameof(LauncherOptions.RefreshSessionSeconds)));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
