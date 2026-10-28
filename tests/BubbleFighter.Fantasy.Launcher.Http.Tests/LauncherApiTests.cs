using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherApiTests
{
    /// <summary>Slow startup remains valid until the ten-minute boundary, without permitting ticket replay.</summary>
    [Theory]
    [InlineData(62, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    public async Task DefaultTicketLifetimeAllowsSlowStartupAndExpiresAtTenMinutes(int elapsedSeconds, bool accepted)
    {
        var clock = new TestClock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(10), clock);
        var session = await sessions.SignInAsync("startup-test", default);
        var issuedAt = clock.GetUtcNow();
        var ticket = sessions.Issue(session.AccessToken, "kr-1256");
        Assert.Equal(issuedAt.AddMinutes(10), ticket.ExpiresAt);
        clock.Advance(elapsedSeconds);
        Assert.Null(sessions.Redeem(ticket.Token, "kr-812"));
        Assert.Equal(accepted ? "startup-test" : null, sessions.Redeem(ticket.Token, "kr-1256"));
        Assert.Null(sessions.Redeem(ticket.Token, "kr-1256"));
    }

    [Fact]
    public async Task CatalogRegistrationLoginProfileAndLogoutUseRealHttp()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000, HostPolicy = "ip", Region = "CN",
            Instances = [new() { Id = "kr-1256", Build = "kr-1256", Name = [new() { Language = "en", Text = "KR 2026" }], Port = 7100, ClientLanguages = ["zh-CN", "ko"] }] };
        var accounts = new MemoryLauncherAccounts(10); var sessions = new LauncherSessions(options, new MemoryLauncherSessions(10));
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions);
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        var catalog = await http.GetFromJsonAsync<JsonElement>("v1/catalog");
        Assert.Equal("ip", catalog.GetProperty("hostPolicy").GetString());
        Assert.Equal("CN", catalog.GetProperty("region").GetString());
        Assert.False(catalog.GetProperty("instances")[0].TryGetProperty("downloadSource", out _));
        Assert.Equal(["level", "lucci", "cash"], catalog.GetProperty("profileFields").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("build = \"kr-1256\"", await http.GetStringAsync("v1/instances/kr-1256/instance.toml"));
        using var anonymous = await http.GetAsync("v1/account"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var invalid = await http.PostAsJsonAsync("v1/auth/register", new { username = "tester", password = "short" }); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var created = await http.PostAsJsonAsync("v1/auth/register", new { username = "tester", password = "Local-test-123" }); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var duplicate = await http.PostAsJsonAsync("v1/auth/register", new { username = "TESTER", password = "Local-test-123" }); Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var stored = await accounts.FindAsync("tester", default); Assert.NotNull(stored); Assert.StartsWith("pbkdf2-sha256$", stored.PasswordHash); Assert.DoesNotContain("Local-test-123", stored.PasswordHash);
        using var rejected = await http.PostAsJsonAsync("v1/auth/login", new { username = "tester", password = "wrong" }); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "TESTER", password = "Local-test-123" }); login.EnsureSuccessStatusCode();
        var response = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.GetProperty("accessToken").GetString());
        var account = await http.GetFromJsonAsync<JsonElement>("v1/account"); Assert.Equal("tester", account.GetProperty("username").GetString());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("stats").GetProperty("lucci").ValueKind);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("stats").GetProperty("exp").ValueKind);
        using var malformed = await http.PostAsJsonAsync("v1/launch", new { }); Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var launch = await http.PostAsJsonAsync("v1/launch", new { build = "kr-1256" }); launch.EnsureSuccessStatusCode();
        var ticket = (await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
        Assert.Null(sessions.Redeem(ticket, "kr-812")); Assert.Equal("tester", sessions.Redeem(ticket, "kr-1256")); Assert.Null(sessions.Redeem(ticket, "kr-1256"));
        using var logout = await http.PostAsJsonAsync("v1/auth/logout", new { refreshToken = response.GetProperty("refreshToken").GetString() }); logout.EnsureSuccessStatusCode();
        using var expired = await http.GetAsync("v1/account"); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    /// <summary>
    /// An in-game sign-in build launches without a bearer; the game then signs in with the launcher's own requests
    /// and binds that launch once. Other builds still refuse an anonymous launch, and the catalog carries the HTTPS
    /// override.
    /// </summary>
    [Fact]
    public async Task AnInGameSignInBuildLaunchesFirstAndTheGameBindsTheLaunch()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000, SecureUrl = "https://bf.example/api",
            Instances =
            [
                new() { Id = "id-400", Build = "id-400", Name = [new() { Language = "en", Text = "ID 2014" }], Port = 7120, LauncherLogin = "game" },
                new() { Id = "kr-812", Build = "kr-812", Name = [new() { Language = "en", Text = "KR 2015" }], Port = 7110 },
            ] };
        var accounts = new MemoryLauncherAccounts(10); var sessions = new LauncherSessions(options, new MemoryLauncherSessions(10));
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions);
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        Assert.Equal("https://bf.example/api", (await http.GetFromJsonAsync<JsonElement>("v1/catalog")).GetProperty("secureUrl").GetString());
        using var refused = await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" }); Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var opened = await http.PostAsJsonAsync("v1/launch", new { build = "id-400" }); opened.EnsureSuccessStatusCode();
        var launch = await opened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(launch.GetProperty("gameLogin").GetBoolean());
        Assert.False(launch.TryGetProperty("ticket", out _));
        var launchId = launch.GetProperty("launchId").GetString()!; var launchKey = launch.GetProperty("launchKey").GetString()!;
        using var early = await http.PostAsJsonAsync("v1/launch/companion", new { launchId, launchKey });
        Assert.Equal("launch_not_bound", (await early.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        using var created = await http.PostAsJsonAsync("v1/auth/register", new { username = "player", password = "Local-test-123" }); created.EnsureSuccessStatusCode();
        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "player", password = "Local-test-123" }); login.EnsureSuccessStatusCode();
        using var game = new HttpClient { BaseAddress = http.BaseAddress };
        game.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        using var wrongKey = await game.PostAsJsonAsync("v1/launch", new { build = "id-400", launchId, launchKey = "00" }); Assert.Equal(HttpStatusCode.NotFound, wrongKey.StatusCode);
        using var bound = await game.PostAsJsonAsync("v1/launch", new { build = "id-400", launchId, launchKey }); bound.EnsureSuccessStatusCode();
        var ticket = (await bound.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
        Assert.Equal("player", sessions.Redeem(ticket, "id-400"));
        using var replay = await game.PostAsJsonAsync("v1/launch", new { build = "id-400", launchId, launchKey }); Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        // Without a companion row the bound launch has no grant to hand out.
        using var later = await http.PostAsJsonAsync("v1/launch/companion", new { launchId, launchKey });
        Assert.Equal("companion_unavailable", (await later.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // A game started without the launcher signs in and launches without a launch to bind.
        using var standalone = await game.PostAsJsonAsync("v1/launch", new { build = "id-400" }); standalone.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SessionBoundsExpiryAndRevocationAreEnforced()
    {
        var clock = new TestClock();
        var sessions = new LauncherSessions(new LauncherOptions { MaxSessions = 1, SessionSeconds = 60, TicketSeconds = 10 }, new MemoryLauncherSessions(10), clock);
        var session = await sessions.SignInAsync("one", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.SignInAsync("two", default).AsTask());
        var ticket = sessions.Issue(session.AccessToken, "kr-1256"); clock.Advance(11); Assert.Null(sessions.Redeem(ticket.Token, "kr-1256"));
        var second = sessions.Issue(session.AccessToken, "kr-1256");
        Assert.True(await sessions.SignOutAsync(session.RefreshToken, null, default)); Assert.Null(sessions.Redeem(second.Token, "kr-1256"));
        Assert.Null(sessions.Account(session.AccessToken));
        var next = await sessions.SignInAsync("two", default); clock.Advance(61); Assert.Null(sessions.Account(next.AccessToken));
        Assert.NotNull((await sessions.SignInAsync("three", default)).AccessToken);
    }

    [Fact]
    public async Task RegistrationCanBeDisabledAndUnknownFieldsDoNotBypassAuthentication()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000, RegistrationEnabled = false };
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(1), new LauncherSessions(options, new MemoryLauncherSessions(10)));
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        using var registration = await http.PostAsJsonAsync("v1/auth/register", new { username = "tester", password = "Local-test-123" }); Assert.Equal(HttpStatusCode.Forbidden, registration.StatusCode);
        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "tester", password = "Local-test-123", extra = true }); Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task SignInCreatesUnknownAccountsOnlyWhenEnabled()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000, RegistrationEnabled = false, CreateAccountOnLogin = true };
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(4), new LauncherSessions(options, new MemoryLauncherSessions(10)));
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        using var shortPassword = await http.PostAsJsonAsync("v1/auth/login", new { username = "fresh", password = "short" }); Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        using var first = await http.PostAsJsonAsync("v1/auth/login", new { username = "fresh", password = "Local-test-123" }); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var again = await http.PostAsJsonAsync("v1/auth/login", new { username = "FRESH", password = "Local-test-123" }); Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var wrong = await http.PostAsJsonAsync("v1/auth/login", new { username = "fresh", password = "Other-test-456" }); Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public void DeploymentConfigurationBindsLocalizedInstanceNames()
    {
        var config = new BubbleFighter.Fantasy.Kernel.PluginConfig(new BubbleFighter.Fantasy.Abstractions.PluginRow
        {
            Id = "launcher.http", Plugin = "launcher.http", Source = "test", Config = new Dictionary<string, object?>
            {
                ["host_policy"] = "ip", ["region"] = "CN", ["instances"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = "kr-1256", ["build"] = "kr-1256", ["port"] = 7100,
                        ["name"] = new List<object?> { new Dictionary<string, object?> { ["language"] = "en", ["text"] = "KR 2026" } },
                        ["client_languages"] = new List<object?> { "zh-CN", "ko" },
                        ["download_source"] = new Dictionary<string, object?> { ["kind"] = "alist",
                            ["base_url"] = "https://downloads.example.invalid/", ["catalog_path"] = "FantasyTale/source.toml",
                            ["package_id"] = "bubblefighter.kr-1256" } }
                }
            }
        });
        var options = config.Bind<LauncherOptions>();
        Assert.Equal("KR 2026", Assert.Single(Assert.Single(options.Instances).Name).Text);
        Assert.Equal("ip", options.HostPolicy);
        var download = Assert.Single(options.Instances).DownloadSource;
        Assert.NotNull(download);
        Assert.Equal("alist", download.Kind);
        Assert.Equal("https://downloads.example.invalid/", download.BaseUrl);
        Assert.Equal("FantasyTale/source.toml", download.CatalogPath);
        Assert.Equal("bubblefighter.kr-1256", download.PackageId);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("alist")]
    public async Task CatalogAdvertisesConfiguredDownloadSourceWithoutChangingPortablePreset(string kind)
    {
        var options = DownloadOptions(kind);
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(1), new LauncherSessions(options, new MemoryLauncherSessions(10)));
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        var catalog = await http.GetFromJsonAsync<JsonElement>("v1/catalog");
        var instance = catalog.GetProperty("instances")[0];
        Assert.Equal("kr-812", instance.GetProperty("build").GetString());
        var source = instance.GetProperty("downloadSource");
        Assert.Equal(4, source.EnumerateObject().Count());
        Assert.Equal(kind, source.GetProperty("kind").GetString());
        Assert.Equal("https://downloads.example.invalid/", source.GetProperty("baseUrl").GetString());
        Assert.Equal("FantasyTale/source.toml", source.GetProperty("catalogPath").GetString());
        Assert.Equal("bubblefighter.kr-812", source.GetProperty("packageId").GetString());
        Assert.DoesNotContain("download", await http.GetStringAsync("v1/instances/kr-812/instance.toml"));
    }

    [Theory]
    [InlineData("kind", "ftp")]
    [InlineData("base", "file:///clients")]
    [InlineData("base", "https://user:password@example.invalid/")]
    [InlineData("base", "https://example.invalid/?token=secret")]
    [InlineData("base", "https://example.invalid/#catalog")]
    [InlineData("path", "../source.toml")]
    [InlineData("path", "/source.toml")]
    [InlineData("path", "folder//source.toml")]
    [InlineData("path", "folder\\source.toml")]
    [InlineData("path", "%2e%2e/source.toml")]
    [InlineData("path", "https://example.invalid/source.toml")]
    [InlineData("package", "")]
    [InlineData("package", "BubbleFighter")]
    public async Task InvalidDownloadSourceFailsBeforeListenerBinding(string field, string value)
    {
        var options = DownloadOptions("http");
        var source = options.Instances[0].DownloadSource!;
        switch (field)
        {
            case "kind": source.Kind = value; break;
            case "base": source.BaseUrl = value; break;
            case "path": source.CatalogPath = value; break;
            case "package": source.PackageId = value; break;
        }
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            LauncherServer.StartAsync(options, new MemoryLauncherAccounts(1), new LauncherSessions(options, new MemoryLauncherSessions(10))));
    }

    [Fact]
    public async Task InstanceWithoutAHostAdvertisesTheAddressTheLauncherUsed()
    {
        var options = new LauncherOptions
        {
            Listen = "127.0.0.1:0", PasswordIterations = 100000,
            Instances = [new() { Id = "kr-812", Build = "kr-812", Name = [new() { Language = "en", Text = "KR 2015" }], Host = "", Port = 7110 }],
        };
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(1), new LauncherSessions(options, new MemoryLauncherSessions(10)));
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/catalog");
        request.Headers.Host = "play.example.invalid:8090";
        using var response = await http.SendAsync(request);
        var endpoint = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instances")[0].GetProperty("endpoint");
        Assert.Equal("play.example.invalid", endpoint.GetProperty("host").GetString());
        Assert.Equal(7110, endpoint.GetProperty("port").GetInt32());
    }

    private static LauncherOptions DownloadOptions(string kind) => new()
    {
        Listen = "127.0.0.1:0", PasswordIterations = 100000,
        Instances = [new() { Id = "kr-812", Build = "kr-812", Name = [new() { Language = "en", Text = "KR 2015" }], Port = 7110,
            DownloadSource = new() { Kind = kind, BaseUrl = "https://downloads.example.invalid/", PackageId = "bubblefighter.kr-812" } }],
    };

    [Fact]
    public async Task LegacyEnrollmentPersistsOnlyTheFirstPassword()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000, AllowLegacyPasswordEnrollment = true, RegistrationEnabled = false, MinimumPasswordLength = 6 };
        var accounts = new MemoryLauncherAccounts(10);
        await accounts.RegisterAsync("legacy", "", default);
        await using var server = await LauncherServer.StartAsync(options, accounts, new LauncherSessions(options, new MemoryLauncherSessions(10)));
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        using var shortPassword = await http.PostAsJsonAsync("v1/auth/login", new { username = "legacy", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Equal("", (await accounts.FindAsync("legacy", default))!.PasswordHash);
        using var first = await http.PostAsJsonAsync("v1/auth/login", new { username = "legacy", password = "First6" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = (await accounts.FindAsync("legacy", default))!.PasswordHash;
        Assert.StartsWith("pbkdf2-sha256$", original);
        using var wrong = await http.PostAsJsonAsync("v1/auth/login", new { username = "legacy", password = "Other-pass-456" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(original, (await accounts.FindAsync("legacy", default))!.PasswordHash);
        using var again = await http.PostAsJsonAsync("v1/auth/login", new { username = "legacy", password = "First6" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var unknown = await http.PostAsJsonAsync("v1/auth/login", new { username = "unknown", password = "First-pass-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Null(await accounts.FindAsync("unknown", default));
    }

    [Fact]
    public async Task LegacyEnrollmentRequiresExplicitConfigurationAndNeverReplacesOtherHashes()
    {
        var accounts = new MemoryLauncherAccounts(10);
        await accounts.RegisterAsync("legacy", "", default);
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000 };
        await using (var server = await LauncherServer.StartAsync(options, accounts, new LauncherSessions(options, new MemoryLauncherSessions(10))))
        {
            using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
            using var denied = await http.PostAsJsonAsync("v1/auth/login", new { username = "legacy", password = "First-pass-123" });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("", (await accounts.FindAsync("legacy", default))!.PasswordHash);
        }
        await accounts.RegisterAsync("existing", "unsupported-but-nonempty", default);
        options.AllowLegacyPasswordEnrollment = true;
        await using var enabled = await LauncherServer.StartAsync(options, accounts, new LauncherSessions(options, new MemoryLauncherSessions(10)));
        using var client = new HttpClient { BaseAddress = new Uri(enabled.Addresses.Single()) };
        using var invalidHash = await client.PostAsJsonAsync("v1/auth/login", new { username = "existing", password = "First-pass-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalidHash.StatusCode);
        Assert.Equal("unsupported-but-nonempty", (await accounts.FindAsync("existing", default))!.PasswordHash);
    }

    [Fact]
    public async Task ConcurrentLegacyLoginsCannotBothEnrollDifferentPasswords()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000, AllowLegacyPasswordEnrollment = true };
        var accounts = new MemoryLauncherAccounts(10);
        await accounts.RegisterAsync("legacy", "", default);
        await using var server = await LauncherServer.StartAsync(options, accounts, new LauncherSessions(options, new MemoryLauncherSessions(10)));
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        var responses = await Task.WhenAll(
            http.PostAsJsonAsync("v1/auth/login", new { username = "legacy", password = "First-pass-123" }),
            http.PostAsJsonAsync("v1/auth/login", new { username = "legacy", password = "Other-pass-456" }));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
