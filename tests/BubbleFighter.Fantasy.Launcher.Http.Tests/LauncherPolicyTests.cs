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
        Assert.Empty(grant.GetProperty("pluginRecommendations").EnumerateArray());
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
    public async Task PublicCatalogAndSessionEnvelopesPreserveEachIdentityPolicyScope()
    {
        LauncherPluginRecommendation[] recommendations =
        [
            new() { Id = "plugin.scoped", Builds = ["build-z"], Required = false },
            new() { Id = "plugin.scoped", Builds = ["build-a", "build-z"], Required = true },
            new() { Id = "plugin.global", Builds = ["build-a"], Required = true },
            new() { Id = "plugin.global", Builds = [], Required = false },
        ];
        await using var fixture = await Fixture.StartAsync("1.0.0", recommendations);
        fixture.Http.DefaultRequestHeaders.Add(VersionHeader, "malformed-old-client");
        var catalog = await fixture.Http.GetFromJsonAsync<JsonElement>("v1/catalog");
        fixture.Http.DefaultRequestHeaders.Remove(VersionHeader);
        var login = await fixture.LoginAsync("1.0.0");
        using var refresh = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = login.GetProperty("refreshToken").GetString(), launcherVersion = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var envelope in new[] { catalog, login, renewed })
        {
            Assert.Equal("1.0.0", envelope.GetProperty("minimumLauncherVersion").GetString());
            var plugins = envelope.GetProperty("pluginRecommendations").EnumerateArray().ToArray();
            Assert.Equal(4, plugins.Length);
            var scoped = Assert.Single(plugins, p => p.GetProperty("id").GetString() == "plugin.scoped" && p.GetProperty("required").GetBoolean());
            Assert.Equal(["build-a", "build-z"], scoped.GetProperty("builds").EnumerateArray().Select(b => b.GetString()));
            Assert.True(scoped.GetProperty("required").GetBoolean());
            var global = Assert.Single(plugins, p => p.GetProperty("id").GetString() == "plugin.global" && !p.GetProperty("required").GetBoolean());
            Assert.Empty(global.GetProperty("builds").EnumerateArray());
            Assert.False(global.GetProperty("required").GetBoolean());
            var scopedGlobal = Assert.Single(plugins, p => p.GetProperty("id").GetString() == "plugin.global" && p.GetProperty("required").GetBoolean());
            Assert.Equal(["build-a"], scopedGlobal.GetProperty("builds").EnumerateArray().Select(build => build.GetString()));
            Assert.All(plugins, p => Assert.Equal(["id", "builds", "required", "disabled"], p.EnumerateObject().Select(v => v.Name)));
            Assert.All(plugins, p => Assert.False(p.GetProperty("disabled").GetBoolean()));
        }
    }

    [Fact]
    public async Task DisjointRequiredAndDisabledScopesSurviveCatalogLoginAndRefresh()
    {
        await using var fixture = await Fixture.StartAsync(recommendations:
        [
            new() { Id = "plugin.scoped", Builds = ["build-a"], Required = true },
            new() { Id = "plugin.scoped", Builds = ["build-b"], Disabled = true },
            new() { Id = "plugin.scoped", Builds = ["build-c"], Disabled = true },
        ]);
        var catalog = await fixture.Http.GetFromJsonAsync<JsonElement>("v1/catalog");
        var login = await fixture.LoginAsync(null);
        using var refresh = await fixture.Http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = login.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var envelope in new[] { catalog, login, renewed })
        {
            var policies = envelope.GetProperty("pluginRecommendations").EnumerateArray().ToArray();
            Assert.Equal(2, policies.Length);
            var required = Assert.Single(policies, policy => policy.GetProperty("required").GetBoolean());
            Assert.False(required.GetProperty("disabled").GetBoolean());
            Assert.Equal(["build-a"], required.GetProperty("builds").EnumerateArray().Select(build => build.GetString()));
            var disabled = Assert.Single(policies, policy => policy.GetProperty("disabled").GetBoolean());
            Assert.False(disabled.GetProperty("required").GetBoolean());
            Assert.Equal(["build-b", "build-c"], disabled.GetProperty("builds").EnumerateArray().Select(build => build.GetString()));
        }
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("long-version")]
    [InlineData("null-version")]
    [InlineData("null-recommendations")]
    [InlineData("null-entry")]
    [InlineData("url-identity")]
    [InlineData("path-identity")]
    [InlineData("null-identity")]
    [InlineData("long-identity")]
    [InlineData("null-builds")]
    [InlineData("null-build")]
    [InlineData("path-build")]
    [InlineData("too-many-entries")]
    [InlineData("too-many-builds")]
    [InlineData("merged-builds")]
    [InlineData("required-disabled")]
    [InlineData("overlap")]
    [InlineData("required-wildcard")]
    [InlineData("disabled-wildcard")]
    public async Task InvalidPolicyFailsBeforeListenerBinding(string scenario)
    {
        var options = Fixture.Options();
        options.PluginRecommendations = [new() { Id = "plugin.id" }];
        switch (scenario)
        {
            case "minimum": options.MinimumLauncherVersion = "1.0"; break;
            case "long-version": options.MinimumLauncherVersion = "1.0.0+" + new string('a', 123); break;
            case "null-version": options.MinimumLauncherVersion = null!; break;
            case "null-recommendations": options.PluginRecommendations = null!; break;
            case "null-entry": options.PluginRecommendations = [null!]; break;
            case "url-identity": options.PluginRecommendations[0].Id = "https://example.invalid/plugin"; break;
            case "path-identity": options.PluginRecommendations[0].Id = "../plugin"; break;
            case "null-identity": options.PluginRecommendations[0].Id = null!; break;
            case "long-identity": options.PluginRecommendations[0].Id = new string('a', 129); break;
            case "null-builds": options.PluginRecommendations[0].Builds = null!; break;
            case "null-build": options.PluginRecommendations[0].Builds = [null!]; break;
            case "path-build": options.PluginRecommendations[0].Builds = ["../build"]; break;
            case "too-many-entries": options.PluginRecommendations = Enumerable.Range(0, 65).Select(i => new LauncherPluginRecommendation { Id = $"plugin.{i}" }).ToArray(); break;
            case "too-many-builds": options.PluginRecommendations[0].Builds = Enumerable.Range(0, 65).Select(i => $"build-{i}").ToArray(); break;
            case "merged-builds": options.PluginRecommendations = [new() { Id = "same", Builds = Enumerable.Range(0, 64).Select(i => $"build-{i}").ToArray() }, new() { Id = "same", Builds = ["build-64"] }]; break;
            case "required-disabled": options.PluginRecommendations[0].Required = true; options.PluginRecommendations[0].Disabled = true; break;
            case "overlap": options.PluginRecommendations = [new() { Id = "same", Builds = ["build-a", "build-b"], Required = true }, new() { Id = "same", Builds = ["build-b", "build-c"], Disabled = true }]; break;
            case "required-wildcard": options.PluginRecommendations = [new() { Id = "same", Required = true }, new() { Id = "same", Builds = ["build-a"], Disabled = true }]; break;
            case "disabled-wildcard": options.PluginRecommendations = [new() { Id = "same", Builds = ["build-a"], Required = true }, new() { Id = "same", Disabled = true }]; break;
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
    public void SnakeCaseConfigurationBindsPolicyAndAcceptsExactLimits()
    {
        var config = new BubbleFighter.Fantasy.Kernel.PluginConfig(new BubbleFighter.Fantasy.Abstractions.PluginRow
        {
            Id = "launcher.http", Plugin = "launcher.http", Source = "test", Config = new Dictionary<string, object?>
            {
                ["minimum_launcher_version"] = "1.2.3",
                ["plugin_recommendations"] = new List<object?> { new Dictionary<string, object?> { ["id"] = "plugin.id", ["builds"] = new List<object?> { "build-a" }, ["required"] = true } },
            },
        });
        var options = config.Bind<LauncherOptions>();
        Assert.Equal("1.2.3", options.MinimumLauncherVersion);
        var recommendation = Assert.Single(options.PluginRecommendations);
        Assert.Equal("plugin.id", recommendation.Id);
        Assert.Equal(["build-a"], recommendation.Builds);
        Assert.True(recommendation.Required);
        options.MinimumLauncherVersion = "1.0.0+" + new string('a', 122);
        options.PluginRecommendations = Enumerable.Range(0, 64).Select(i => new LauncherPluginRecommendation { Id = i == 0 ? new string('a', 128) : $"plugin.{i}", Builds = Enumerable.Range(0, 64).Select(b => $"build-{b}").ToArray() }).ToArray();
        Assert.Empty(options.Validate());
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
        internal static async Task<Fixture> StartAsync(string minimum = "", LauncherPluginRecommendation[]? recommendations = null)
        {
            var options = Options();
            options.MinimumLauncherVersion = minimum;
            options.PluginRecommendations = recommendations ?? [];
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
