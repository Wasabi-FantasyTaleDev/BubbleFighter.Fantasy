using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherPolicyTests
{
    private const string VersionHeader = "X-FantasyTale-Launcher-Version";
    private const string Password = "Local-policy-test-123";

    [Fact]
    public async Task VersionExtensionIsAcceptedByAllFourOperationsAndTicketsStayBuildBound()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Http.DefaultRequestHeaders.Add(VersionHeader, "0.1.14");
        using var register = await fixture.Http.PostAsJsonAsync("v1/auth/register", new { username = "new-player", password = Password, launcherVersion = "0.1.14" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var grant = await fixture.LoginAsync("0.1.14");
        using var refresh = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = grant.GetProperty("refreshToken").GetString(), launcherVersion = "0.1.14" });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        fixture.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewed.GetProperty("accessToken").GetString());
        using var launch = await fixture.Http.PostAsJsonAsync("v1/launch", new { build = "test-build", launcherVersion = "0.1.14" });
        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        var ticket = (await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
        Assert.Null(fixture.Sessions.Redeem(ticket, "other-build"));
        Assert.Equal("tester", fixture.Sessions.Redeem(ticket, "test-build"));
        Assert.Null(fixture.Sessions.Redeem(ticket, "test-build"));
    }

    [Fact]
    public async Task DisabledMinimumPreservesUnversionedAuthentication()
    {
        await using var fixture = await Fixture.StartAsync();
        var grant = await fixture.LoginAsync(null);
        Assert.Equal("tester", grant.GetProperty("account").GetProperty("username").GetString());
        Assert.Equal(JsonValueKind.Null, grant.GetProperty("minimumLauncherVersion").ValueKind);
        Assert.Empty(grant.GetProperty("clientPolicy").GetProperty("plugins").EnumerateArray());
    }

    [Theory]
    [InlineData(null, "1.0.10", false)]
    [InlineData("1.0.9", "1.0.10", false)]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("1.0.10-rc.2", "1.0.10-rc.10", false)]
    [InlineData("1.0.10-rc.10", "1.0.10-rc.2", true)]
    [InlineData("1.0.10-rc.10", "1.0.10", false)]
    [InlineData("1.0.10", "1.0.10-rc.10", true)]
    [InlineData("1.0.10+local.002", "1.0.10+release", true)]
    [InlineData("1.0.10-1", "1.0.10-alpha", false)]
    [InlineData("1.0.10-alpha", "1.0.10-1", true)]
    [InlineData("1.0.10-alpha", "1.0.10-alpha.1", false)]
    [InlineData("1.0.10-alpha.1", "1.0.10-alpha", true)]
    [InlineData("1.0.10-999999999999999999999999999999", "1.0.10-999999999999999999999999999998", true)]
    [InlineData("999999999999999999999999999999.0.0", "999999999999999999999999999998.0.0", true)]
    public async Task MinimumUsesNumericAndPrereleaseSemVerPrecedence(string? supplied, string minimum, bool allowed)
    {
        await using var fixture = await Fixture.StartAsync(minimum);
        using var response = await fixture.Http.PostAsJsonAsync("v1/auth/login", LoginBody(supplied));
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.UpgradeRequired, response.StatusCode);
        if (!allowed)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, body.EnumerateObject().Count());
            Assert.Equal("launcher_update_required", body.GetProperty("error").GetString());
            Assert.Equal(minimum, body.GetProperty("minimumLauncherVersion").GetString());
            Assert.Equal("", (await fixture.Accounts.FindAsync("tester", default))!.PasswordHash);
        }
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+foo..bar")]
    [InlineData("1.0.0+foo+bar")]
    [InlineData("1.0.0-rc_1")]
    [InlineData("1.0.0-中文")]
    [InlineData(" 1.0.0")]
    [InlineData("")]
    public async Task InvalidVersionIsRejectedEvenWithoutMinimum(string supplied)
    {
        await using var fixture = await Fixture.StartAsync();
        using var response = await fixture.Http.PostAsJsonAsync("v1/auth/login", LoginBody(supplied));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("", (await fixture.Accounts.FindAsync("tester", default))!.PasswordHash);
    }

    [Theory]
    [InlineData("{\"username\":\"tester\",\"password\":\"Local-policy-test-123\",\"launcherVersion\":null}")]
    [InlineData("{\"username\":\"tester\",\"password\":\"Local-policy-test-123\",\"launcherVersion\":123}")]
    [InlineData("{\"username\":\"tester\",\"password\":\"Local-policy-test-123\",\"launcherVersion\":\"1.0.0\",\"launcherVersion\":\"1.0.0\"}")]
    [InlineData("{\"username\":\"tester\",\"password\":123,\"extra\":true}")]
    [InlineData("{\"password\":\"Local-policy-test-123\",\"extra\":true}")]
    [InlineData("{\"username\":\"tester\",\"username\":\"other\",\"password\":\"Local-policy-test-123\",\"extra\":true}")]
    public async Task ExtensionPreservesKnownFieldAndDuplicateValidation(string json)
    {
        await using var fixture = await Fixture.StartAsync();
        using var response = await fixture.Http.PostAsync("v1/auth/login", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("", (await fixture.Accounts.FindAsync("tester", default))!.PasswordHash);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnoredThroughoutAuthenticationAndTicketFlow()
    {
        await using var fixture = await Fixture.StartAsync("1.0.0");
        fixture.Http.DefaultRequestHeaders.Add(VersionHeader, "1.1.0");
        using var register = await fixture.Http.PostAsJsonAsync("v1/auth/register", new
        {
            username = "future-player", password = Password, launcherVersion = "1.1.0",
            future = new { enabled = true, values = new[] { 1, 2 } }, role = "admin",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        using var login = await fixture.Http.PostAsJsonAsync("v1/auth/login", new
        {
            username = "future-player", password = Password, launcherVersion = "1.1.0",
            future = new { enabled = true }, account = "other-player", extra = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var grant = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("future-player", grant.GetProperty("account").GetProperty("username").GetString());
        using var refresh = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new
        {
            refreshToken = grant.GetProperty("refreshToken").GetString(), launcherVersion = "1.1.0",
            future = new[] { "value" }, username = "other-player",
        });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("future-player", renewed.GetProperty("account").GetProperty("username").GetString());
        fixture.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewed.GetProperty("accessToken").GetString());
        using var launch = await fixture.Http.PostAsJsonAsync("v1/launch", new
        {
            build = "test-build", launcherVersion = "1.1.0", future = true, account = "other-player",
        });
        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        var ticket = (await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
        Assert.Equal("future-player", fixture.Sessions.Redeem(ticket, "test-build"));
        Assert.Null(fixture.Sessions.Redeem(ticket, "test-build"));
        using var logout = await fixture.Http.PostAsJsonAsync("v1/auth/logout", new
        {
            refreshToken = renewed.GetProperty("refreshToken").GetString(), future = "extension",
        });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Null(fixture.Sessions.Account(renewed.GetProperty("accessToken").GetString()!));
    }

    [Fact]
    public async Task HeaderOnlyVersionIsAcceptedButMismatchAndMultipleValuesAreRejected()
    {
        await using var fixture = await Fixture.StartAsync("1.0.0");
        fixture.Http.DefaultRequestHeaders.Add(VersionHeader, "1.0.0");
        using var mismatch = await fixture.Http.PostAsJsonAsync("v1/auth/login", LoginBody("1.0.1"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        fixture.Http.DefaultRequestHeaders.Remove(VersionHeader);
        fixture.Http.DefaultRequestHeaders.TryAddWithoutValidation(VersionHeader, ["1.0.0", "1.0.0"]);
        using var duplicate = await fixture.Http.PostAsJsonAsync("v1/auth/login", LoginBody("1.0.0"));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        fixture.Http.DefaultRequestHeaders.Remove(VersionHeader);
        fixture.Http.DefaultRequestHeaders.Add(VersionHeader, "1.0.0");
        await fixture.LoginAsync(null);
    }

    [Fact]
    public async Task OldRegistrationAndLaunchHaveNoSideEffectsAndAnonymousLaunchRemainsUnauthorized()
    {
        await using var fixture = await Fixture.StartAsync("1.0.0");
        using var registration = await fixture.Http.PostAsJsonAsync("v1/auth/register", new { username = "blocked", password = Password, launcherVersion = "0.9.0" });
        Assert.Equal(HttpStatusCode.UpgradeRequired, registration.StatusCode);
        Assert.Null(await fixture.Accounts.FindAsync("blocked", default));
        using var anonymous = await fixture.Http.PostAsJsonAsync("v1/launch", new { build = "test-build", launcherVersion = "1.0.0" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var grant = await fixture.LoginAsync("1.0.0");
        fixture.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("accessToken").GetString());
        using var oldLaunch = await fixture.Http.PostAsJsonAsync("v1/launch", new { build = "test-build", launcherVersion = "0.9.0" });
        Assert.Equal(HttpStatusCode.UpgradeRequired, oldLaunch.StatusCode);
        Assert.False((await oldLaunch.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("ticket", out _));
    }

    [Fact]
    public async Task RejectedRefreshDoesNotRotateAndRefreshOnlyLogoutIsAlwaysAvailable()
    {
        await using var fixture = await Fixture.StartAsync("1.0.0");
        var first = await fixture.LoginAsync("1.0.0");
        var second = await fixture.LoginAsync("1.0.0");
        using var rejected = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = first.GetProperty("refreshToken").GetString(), launcherVersion = "0.9.0" });
        Assert.Equal(HttpStatusCode.UpgradeRequired, rejected.StatusCode);
        using var accepted = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = first.GetProperty("refreshToken").GetString(), launcherVersion = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var renewed = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        using var stale = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = first.GetProperty("refreshToken").GetString(), launcherVersion = "1.0.0" });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        var ticket = fixture.Sessions.Issue(renewed.GetProperty("accessToken").GetString()!, "test-build");
        fixture.Http.DefaultRequestHeaders.Add(VersionHeader, "not-a-version");
        using var logout = await fixture.Http.PostAsJsonAsync("v1/auth/logout", new { refreshToken = renewed.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Null(fixture.Sessions.Redeem(ticket.Token, "test-build"));
        Assert.Null(fixture.Sessions.Account(renewed.GetProperty("accessToken").GetString()!));
        Assert.Equal("tester", fixture.Sessions.Account(second.GetProperty("accessToken").GetString()!));
    }

    [Fact]
    public async Task CatalogLoginAndRefreshCarryTheSameClientPolicyWithoutManagementLabels()
    {
        var policy = new LauncherClientPolicyOptions
        {
            Sources = [new() { Name = "official", Kind = "alist", BaseUrl = "https://store.example.invalid", CatalogPath = "store/catalog.toml" }],
            Plugins =
            [
                new()
                {
                    Id = "plugin.scoped", Name = "Label", Description = "Operator note",
                    Rules =
                    [
                        new() { Id = "need", Type = "require", Builds = ["build-a"] },
                        new() { Id = "off", Type = "disable", Builds = ["build-b"] },
                        new() { Id = "offer", Type = "recommend", Builds = ["build-a"], Sources = ["official"] },
                        new() { Id = "floor", Type = "min_version", Builds = ["build-a"], Version = "1.2.0" },
                    ],
                },
            ],
        };
        await using var fixture = await Fixture.StartAsync("1.0.0", policy);
        var catalog = await fixture.Http.GetFromJsonAsync<JsonElement>("v1/catalog");
        var login = await fixture.LoginAsync("1.0.0");
        using var refresh = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = login.GetProperty("refreshToken").GetString(), launcherVersion = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var envelope in new[] { catalog, login, renewed })
        {
            Assert.Equal("1.0.0", envelope.GetProperty("minimumLauncherVersion").GetString());
            Assert.False(envelope.TryGetProperty("pluginRecommendations", out _));
            var sent = envelope.GetProperty("clientPolicy");
            Assert.Equal(["version", "sources", "plugins"], sent.EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, sent.GetProperty("version").GetInt32());
            var source = Assert.Single(sent.GetProperty("sources").EnumerateArray());
            Assert.Equal(["name", "kind", "baseUrl", "catalogPath"], source.EnumerateObject().Select(p => p.Name));
            var plugin = Assert.Single(sent.GetProperty("plugins").EnumerateArray());
            Assert.Equal(["id", "rules"], plugin.EnumerateObject().Select(p => p.Name));
            var rules = plugin.GetProperty("rules").EnumerateArray().ToArray();
            Assert.Equal(["need", "off", "offer", "floor"], rules.Select(r => r.GetProperty("id").GetString()));
            Assert.Equal(["id", "type", "builds"], rules[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal(["id", "type", "builds", "sources"], rules[2].EnumerateObject().Select(p => p.Name));
            Assert.Equal(["id", "type", "builds", "version"], rules[3].EnumerateObject().Select(p => p.Name));
            Assert.Equal("1.2.0", rules[3].GetProperty("version").GetString());
        }
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("null-version")]
    [InlineData("null-policy")]
    [InlineData("null-plugins")]
    [InlineData("null-entry")]
    [InlineData("null-rule")]
    [InlineData("url-identity")]
    [InlineData("unknown-type")]
    [InlineData("conflict")]
    [InlineData("unknown-source")]
    public async Task InvalidPolicyFailsBeforeListenerBinding(string scenario)
    {
        var options = Fixture.Options();
        options.ClientPolicy = new() { Plugins = [new() { Id = "plugin.id", Rules = [new() { Id = "r1", Type = "require" }] }] };
        var plugin = options.ClientPolicy.Plugins[0];
        switch (scenario)
        {
            case "minimum": options.MinimumLauncherVersion = "1.0"; break;
            case "null-version": options.MinimumLauncherVersion = null!; break;
            case "null-policy": options.ClientPolicy = null!; break;
            case "null-plugins": options.ClientPolicy.Plugins = null!; break;
            case "null-entry": options.ClientPolicy.Plugins = [null!]; break;
            case "null-rule": plugin.Rules = [null!]; break;
            case "url-identity": plugin.Id = "https://example.invalid/plugin"; break;
            case "unknown-type": plugin.Rules[0].Type = "teleport"; break;
            case "conflict": plugin.Rules = [.. plugin.Rules, new() { Id = "r2", Type = "disable" }]; break;
            case "unknown-source": plugin.Rules = [new() { Id = "r1", Type = "recommend", Sources = ["missing"] }]; break;
        }
        await Assert.ThrowsAsync<ValidationException>(() => LauncherServer.StartAsync(options, new MemoryLauncherAccounts(10), new LauncherSessions(options, new MemoryLauncherSessions(10))));
    }

    [Theory]
    [InlineData("null-instance")]
    [InlineData("null-name-entry")]
    [InlineData("null-host")]
    [InlineData("null-release")]
    [InlineData("null-level-icon")]
    public void InvalidInstanceShapesReturnValidationErrorsInsteadOfThrowing(string scenario)
    {
        var options = Fixture.Options();
        switch (scenario)
        {
            case "null-instance": options.Instances = [null!]; break;
            case "null-name-entry": options.Instances[0].Name = [null!]; break;
            case "null-host": options.Instances[0].Host = null!; break;
            case "null-release": options.Instances[0].ReleaseVersion = null!; break;
            case "null-level-icon": options.LevelIconTemplate = null!; break;
        }

        var errors = options.Validate().ToArray();
        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.False(string.IsNullOrWhiteSpace(error.ErrorMessage)));
    }

    [Fact]
    public void SnakeCaseConfigurationBindsTheClientPolicy()
    {
        var config = new BubbleFighter.Fantasy.Kernel.PluginConfig(new BubbleFighter.Fantasy.Abstractions.PluginRow
        {
            Id = "launcher.http", Plugin = "launcher.http", Source = "test", Config = new Dictionary<string, object?>
            {
                ["minimum_launcher_version"] = "1.2.3",
                ["client_policy"] = new Dictionary<string, object?>
                {
                    ["sources"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "official", ["kind"] = "http", ["base_url"] = "https://store.example.invalid", ["catalog_path"] = "catalog.toml" } },
                    ["plugins"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = "plugin.id",
                            ["rules"] = new List<object?> { new Dictionary<string, object?> { ["id"] = "cap", ["type"] = "max_version", ["builds"] = new List<object?> { "build-a" }, ["version"] = "2.0.0" } },
                        },
                    },
                },
            },
        });
        var options = config.Bind<LauncherOptions>();
        Assert.Equal("1.2.3", options.MinimumLauncherVersion);
        Assert.Equal("https://store.example.invalid", Assert.Single(options.ClientPolicy.Sources).BaseUrl);
        var rule = Assert.Single(Assert.Single(options.ClientPolicy.Plugins).Rules);
        Assert.Equal(("cap", "max_version", "2.0.0"), (rule.Id, rule.Type, rule.Version));
        Assert.Equal(["build-a"], rule.Builds);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void TheRemovedRecommendationKeyIsAnUnknownKey()
    {
        var config = new BubbleFighter.Fantasy.Kernel.PluginConfig(new BubbleFighter.Fantasy.Abstractions.PluginRow
        {
            Id = "launcher.http", Plugin = "launcher.http", Source = "test",
            Config = new Dictionary<string, object?> { ["plugin_recommendations"] = new List<object?>() },
        });
        var error = Assert.ThrowsAny<Exception>(() => config.Bind<LauncherOptions>());
        Assert.Contains("plugin_recommendations", error.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, object> LoginBody(string? version)
    {
        var body = new Dictionary<string, object> { ["username"] = "tester", ["password"] = Password };
        if (version is not null) body.Add("launcherVersion", version);
        return body;
    }

    private sealed class Fixture(LauncherServer server, MemoryLauncherAccounts accounts, LauncherSessions sessions) : IAsyncDisposable
    {
        internal MemoryLauncherAccounts Accounts { get; } = accounts;
        internal LauncherSessions Sessions { get; } = sessions;
        internal HttpClient Http { get; } = new() { BaseAddress = new Uri(server.Addresses.Single()) };
        internal static LauncherOptions Options() => new()
        {
            Listen = "127.0.0.1:0", PasswordIterations = 100000, AllowLegacyPasswordEnrollment = true,
            Instances = [new() { Id = "test-build", Build = "test-build", Name = [new() { Language = "en", Text = "Test build" }], Port = 7100, LauncherLogin = "optional" }],
        };
        internal static async Task<Fixture> StartAsync(string minimum = "", LauncherClientPolicyOptions? policy = null)
        {
            var options = Options();
            options.MinimumLauncherVersion = minimum;
            options.ClientPolicy = policy ?? new();
            var accounts = new MemoryLauncherAccounts(10);
            await accounts.RegisterAsync("tester", "", default);
            var sessions = new LauncherSessions(options, new MemoryLauncherSessions(10));
            return new(await LauncherServer.StartAsync(options, accounts, sessions), accounts, sessions);
        }
        internal async Task<JsonElement> LoginAsync(string? version)
        {
            using var response = await Http.PostAsJsonAsync("v1/auth/login", LoginBody(version));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await server.DisposeAsync();
        }
    }
}
