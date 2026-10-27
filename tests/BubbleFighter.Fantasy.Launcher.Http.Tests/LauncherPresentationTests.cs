using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherPresentationTests
{
    private const string Password = "Local-presentation-123";

    [Fact]
    public void DeploymentKeysBindWithoutSpecialTimestampConversions()
    {
        var config = new BubbleFighter.Fantasy.Kernel.PluginConfig(new BubbleFighter.Fantasy.Abstractions.PluginRow
        {
            Id = "launcher.http", Plugin = "launcher.http", Source = "test", Config = new Dictionary<string, object?>
            {
                ["motd"] = "Welcome", ["roster_enabled"] = false, ["show_online_players"] = false,
                ["player_limit"] = 12, ["whitelist_enabled"] = true, ["whitelist"] = new List<object?> { "tester" },
                ["maintenance"] = true, ["maintenance_start"] = "2026-10-22T10:00:00Z",
                ["maintenance_end"] = "2026-10-22T11:00:00+00:00",
                ["instances"] = new List<object?> { new Dictionary<string, object?>
                {
                    ["id"] = "kr-812", ["build"] = "kr-812", ["port"] = 7110,
                    ["protocol_version"] = 812, ["patch_version"] = "2",
                    ["name"] = new List<object?> { new Dictionary<string, object?> { ["language"] = "en", ["text"] = "KR 2015" } },
                } },
            },
        });
        var options = config.Bind<LauncherOptions>();
        Assert.False(options.RosterEnabled);
        Assert.Equal(12, options.PlayerLimit);
        Assert.True(options.WhitelistEnabled);
        Assert.Equal("2026-10-22T10:00:00Z", options.MaintenanceStart);
        Assert.Equal(812, Assert.Single(options.Instances).ProtocolVersion);
        Assert.Equal("2", options.Instances[0].PatchVersion);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("icon")]
    [InlineData("images")]
    [InlineData("motd")]
    [InlineData("whitelist")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("protocol")]
    [InlineData("patch")]
    public async Task InvalidPresentationAndAdmissionConfigurationIsRejected(string field)
    {
        var options = Options();
        switch (field)
        {
            case "icon": options.IconUrl = "https://user:password@example.invalid/icon.png"; break;
            case "images": options.Images = ["file:///icon.png"]; break;
            case "motd": options.Motd = "text\0"; break;
            case "whitelist": options.Whitelist = ["tester", "TESTER"]; break;
            case "start": options.MaintenanceStart = "2026-10-22T10:00:00"; break;
            case "end": options.MaintenanceStart = "2026-10-22T10:00:00Z"; options.MaintenanceEnd = "2026-10-22T09:00:00Z"; break;
            case "protocol": options.Instances[0].ProtocolVersion = 0; break;
            case "patch": options.Instances[0].PatchVersion = "text\0"; break;
        }
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => Start(options));
    }

    [Fact]
    public async Task CatalogAndRosterUseAuthenticatedUniquePlayersWithoutPrivateFields()
    {
        var options = Options();
        options.Motd = "Welcome\nA second line";
        options.IconUrl = "https://assets.example.invalid/icon.png";
        options.Images = ["https://assets.example.invalid/one.png"];
        options.Instances[0].ProtocolVersion = 812;
        options.Instances[0].ReleaseVersion = "2015.01";
        options.Instances[0].PatchVersion = "2";
        options.MaxRosterPlayers = 1;
        var registry = new Registry([
            new Session(1, new(1, "private-login", "Old name", 2)),
            new Session(2, new(1, "private-login", "Alpha", 3)),
            new Session(3, new(2, "second-login", "Beta", 4)),
            new Session(4, new(3, "closed-login", "Closed", 5), closed: true),
            new Session(5, null),
            new Session(6, new(4, "relay-login", "Relay", 6), loginConnectionId: 99),
        ]);
        await using var server = await Start(options, registry);
        using var http = Client(server);
        var catalog = await http.GetFromJsonAsync<JsonElement>("v1/catalog");
        var presentation = catalog.GetProperty("presentation");
        Assert.Equal(2, presentation.GetProperty("onlinePlayers").GetInt32());
        Assert.Equal(options.Motd, presentation.GetProperty("motd").GetString());
        Assert.Equal(options.IconUrl, presentation.GetProperty("iconUrl").GetString());
        Assert.Equal("online", presentation.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, presentation.GetProperty("playerLimit").ValueKind);
        Assert.False(presentation.TryGetProperty("peakOnlinePlayers", out _));
        var preset = catalog.GetProperty("instances")[0];
        Assert.Equal(812, preset.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("2015.01", preset.GetProperty("releaseVersion").GetString());
        Assert.Equal("2", preset.GetProperty("patchVersion").GetString());
        var roster = await http.GetFromJsonAsync<JsonElement>("v1/players");
        Assert.Equal(1, roster.GetProperty("schema").GetInt32());
        Assert.True(roster.GetProperty("enabled").GetBoolean());
        Assert.True(roster.GetProperty("truncated").GetBoolean());
        var player = Assert.Single(roster.GetProperty("players").EnumerateArray());
        Assert.Equal(2, player.EnumerateObject().Count());
        Assert.Equal("Alpha", player.GetProperty("name").GetString());
        Assert.Equal(3, player.GetProperty("level").GetInt32());
        Assert.DoesNotContain("private-login", roster.GetRawText());
        registry.Sessions = [];
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>("v1/players")).GetProperty("players").EnumerateArray());
    }

    [Fact]
    public async Task DisabledRosterAndCounterNeverConsultRegistry()
    {
        var options = Options(); options.RosterEnabled = false; options.ShowOnlinePlayers = false;
        await using var server = await Start(options, new Registry([]) { ThrowOnRead = true });
        using var http = Client(server);
        var roster = await http.GetFromJsonAsync<JsonElement>("v1/players");
        Assert.False(roster.GetProperty("enabled").GetBoolean());
        Assert.Empty(roster.GetProperty("players").EnumerateArray());
        Assert.Equal(-1, (await http.GetFromJsonAsync<JsonElement>("v1/catalog")).GetProperty("presentation").GetProperty("onlinePlayers").GetInt32());
    }

    [Fact]
    public async Task SharedMatchStateDoesNotKeepAClosedLoginInPublicPresence()
    {
        var owner = new Session(10, new(1, "login", "Primary", 3));
        var relay = new Session(11, null) { Player = owner.Player };
        var unidentified = new Session(12, null) { Player = new PlayerSession(new(2, "unknown", "Unknown", 2)) };
        var registry = new Registry([owner, relay, unidentified]);
        await using var server = await Start(Options(), registry);
        using var http = Client(server);
        var roster = await http.GetFromJsonAsync<JsonElement>("v1/players");
        Assert.Equal("Primary", Assert.Single(roster.GetProperty("players").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(1, (await http.GetFromJsonAsync<JsonElement>("v1/catalog")).GetProperty("presentation").GetProperty("onlinePlayers").GetInt32());

        owner.Closed = true;
        Assert.Same(owner.Player, relay.Player);
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>("v1/players")).GetProperty("players").EnumerateArray());
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonElement>("v1/catalog")).GetProperty("presentation").GetProperty("onlinePlayers").GetInt32());
    }

    [Fact]
    public async Task MissingPresenceIsUnavailableRatherThanAnInventedEmptyRoster()
    {
        var options = Options();
        await using var server = await Start(options);
        using var http = Client(server);
        await AssertError(await http.GetAsync("v1/players"), HttpStatusCode.ServiceUnavailable, "player_state_unavailable");
        Assert.Equal(JsonValueKind.Null, (await http.GetFromJsonAsync<JsonElement>("v1/catalog")).GetProperty("presentation").GetProperty("onlinePlayers").ValueKind);
        options.PlayerLimit = 1;
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => Start(options));
    }

    [Fact]
    public async Task MaintenanceRefusesBeforeAutomaticAccountCreationAndIncludesPublicSchedule()
    {
        var options = Options(); options.Maintenance = true; options.CreateAccountOnLogin = true;
        options.MaintenanceStart = "2026-10-22T10:00:00Z";
        options.MaintenanceEnd = "2026-10-22T11:00:00Z";
        options.MaintenanceDescription = "Scheduled maintenance";
        var accounts = new MemoryLauncherAccounts(10);
        await using var server = await Start(options, accounts: accounts);
        using var http = Client(server);
        using var response = await http.PostAsJsonAsync("v1/auth/login", new { username = "new-player", password = Password });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("server_maintenance", body.GetProperty("error").GetString());
        Assert.Equal(options.MaintenanceStart, body.GetProperty("maintenanceStart").GetString());
        Assert.Equal(options.MaintenanceEnd, body.GetProperty("maintenanceEnd").GetString());
        Assert.Equal(options.MaintenanceDescription, body.GetProperty("maintenanceDescription").GetString());
        Assert.Null(await accounts.FindAsync("new-player", default));
    }

    [Fact]
    public async Task SignInAndLaunchWaitUntilTheHostIsReady()
    {
        var options = Options(); options.CreateAccountOnLogin = true;
        var accounts = new MemoryLauncherAccounts(10);
        var readiness = new Readiness();
        await using var server = await LauncherServer.StartAsync(options, accounts, new LauncherSessions(options, new MemoryLauncherSessions(10)),
            players: null, readiness: readiness);
        using var http = Client(server);
        await AssertError(await http.PostAsJsonAsync("v1/auth/login", new { username = "early", password = Password }), HttpStatusCode.ServiceUnavailable, "server_not_ready");
        Assert.Null(await accounts.FindAsync("early", default));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("v1/catalog")).StatusCode);

        readiness.IsReady = true;
        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "early", password = Password });
        login.EnsureSuccessStatusCode();
        var grant = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" })).StatusCode);
        readiness.IsReady = false;
        await AssertError(await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" }), HttpStatusCode.ServiceUnavailable, "server_not_ready");
    }

    private sealed class Readiness : IHostReadiness
    {
        public bool IsReady { get; set; }
        public bool IsMaintenance { get; private set; }
        public void SetMaintenance(bool enabled) => IsMaintenance = enabled;
    }

    [Fact]
    public async Task ALaunchCarriesItsCompanionGrantAndTheGameTicketNamesTheLaunch()
    {
        var options = Options(); options.CreateAccountOnLogin = true;
        var sessions = new LauncherSessions(options, new MemoryLauncherSessions(10));
        var companion = new Companion();
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(10), sessions, players: null, companion: companion);
        using var http = Client(server);
        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "linked", password = Password });
        login.EnsureSuccessStatusCode();
        var grant = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("accessToken").GetString());

        using var launch = await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" });
        var reply = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var link = reply.GetProperty("companion");
        Assert.Equal($"ws://{http.BaseAddress!.Host}:8092/companion/v1", link.GetProperty("url").GetString());
        Assert.Equal("first", link.GetProperty("ticket").GetString());
        Assert.Equal("launch-1", link.GetProperty("launchId").GetString());
        Assert.Equal(("linked", "kr-812"), companion.Opened.Single());
        Assert.Equal("launch-1", sessions.RedeemIdentity(reply.GetProperty("ticket").GetString()!, "kr-812")!.LaunchId);

        using var renewed = await http.PostAsJsonAsync("v1/companion/tickets", new { launchId = "launch-1" });
        Assert.Equal("renewed", (await renewed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString());
        await AssertError(await http.PostAsJsonAsync("v1/companion/tickets", new { launchId = "other" }), HttpStatusCode.NotFound, "launch_not_found");

        // A full launch table leaves the launch without a companion grant instead of refusing the game.
        companion.Full = true;
        using var withoutLink = await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" });
        var plain = await withoutLink.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(plain.TryGetProperty("companion", out _));
        Assert.Null(sessions.RedeemIdentity(plain.GetProperty("ticket").GetString()!, "kr-812")!.LaunchId);
    }

    [Fact]
    public async Task CompanionRenewalWithoutTheCompanionRowIsUnavailable()
    {
        var options = Options(); options.CreateAccountOnLogin = true;
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(10), new LauncherSessions(options, new MemoryLauncherSessions(10)), players: null);
        using var http = Client(server);
        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "plain", password = Password });
        var grant = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("accessToken").GetString());
        await AssertError(await http.PostAsJsonAsync("v1/companion/tickets", new { launchId = "launch-1" }), HttpStatusCode.NotFound, "companion_unavailable");
    }

    private sealed class Companion : ICompanionTicketIssuer
    {
        public List<(string Account, string Build)> Opened { get; } = [];
        public bool Full { get; set; }
        public CompanionGrant Open(string account, string build)
        {
            if (Full) throw new InvalidOperationException("The companion launch table is at its capacity of 1.");
            Opened.Add((account, build));
            return new("ws://{host}:8092/companion/v1", "first", "launch-1", DateTimeOffset.UtcNow.AddMinutes(2));
        }
        public CompanionGrant? Renew(string account, string launchId) =>
            account == "linked" && launchId == "launch-1" ? new("ws://{host}:8092/companion/v1", "renewed", launchId, DateTimeOffset.UtcNow.AddMinutes(2)) : null;
    }

    [Fact]
    public async Task WhitelistAndPlayerLimitRefuseNewTicketsAndAllowExistingPlayer()
    {
        var options = Options(); options.CreateAccountOnLogin = true;
        options.WhitelistEnabled = true; options.Whitelist = ["TESTER"];
        var registry = new Registry([]);
        await using var server = await Start(options, registry);
        using var http = Client(server);
        await AssertError(await http.PostAsJsonAsync("v1/auth/login", new { username = "outsider", password = Password }), HttpStatusCode.Forbidden, "not_whitelisted");
        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "tester", password = Password });
        login.EnsureSuccessStatusCode();
        var grant = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("accessToken").GetString());
        options.PlayerLimit = 1;
        registry.Sessions = [new Session(1, new(1, "other-player", "Other", 1))];
        await AssertError(await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" }), HttpStatusCode.ServiceUnavailable, "server_full");
        registry.Sessions = [new Session(1, new(1, "tester", "Tester", 1))];
        using var launch = await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" });
        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        options.Maintenance = true;
        await AssertError(await http.PostAsJsonAsync("v1/launch", new { build = "kr-812" }), HttpStatusCode.ServiceUnavailable, "server_maintenance");
    }

    [Fact]
    public async Task AuthenticationFailuresHaveStableCodesWithoutLeakingCredentials()
    {
        var options = Options();
        var accounts = new Accounts();
        await using var server = await Start(options, accounts: accounts);
        using var http = Client(server);
        await AssertError(await http.PostAsJsonAsync("v1/auth/login", new { username = "missing", password = Password }), HttpStatusCode.Unauthorized, "account_not_found");
        using var registered = await http.PostAsJsonAsync("v1/auth/register", new { username = "tester", password = Password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await AssertError(await http.PostAsJsonAsync("v1/auth/login", new { username = "tester", password = "wrong" }), HttpStatusCode.Unauthorized, "wrong_password");
        accounts.Banned = true;
        await AssertError(await http.PostAsJsonAsync("v1/auth/login", new { username = "tester", password = Password }), HttpStatusCode.Unauthorized, "account_banned");
        options.RegistrationEnabled = false;
        await AssertError(await http.PostAsJsonAsync("v1/auth/register", new { username = "second", password = Password }), HttpStatusCode.Forbidden, "registration_disabled");
    }

    private static LauncherOptions Options() => new()
    {
        Listen = "127.0.0.1:0", PasswordIterations = 100000,
        Instances = [new() { Id = "kr-812", Build = "kr-812", Port = 7110, Name = [new() { Language = "en", Text = "KR 2015" }] }],
    };

    private static Task<LauncherServer> Start(LauncherOptions options, ISessionRegistry? players = null, IMatchConnectionTracker? matches = null, ILauncherAccountRepository? accounts = null) =>
        LauncherServer.StartAsync(options, accounts ?? new MemoryLauncherAccounts(10), new LauncherSessions(options, new MemoryLauncherSessions(10)), players: players, matchConnections: matches);

    private static HttpClient Client(LauncherServer server) => new() { BaseAddress = new Uri(server.Addresses.Single()) };

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, body.GetProperty("error").GetString());
            Assert.DoesNotContain(Password, body.GetRawText());
            Assert.DoesNotContain("passwordHash", body.GetRawText());
        }
    }

    private sealed class Accounts : ILauncherAccountRepository
    {
        private readonly MemoryLauncherAccounts _inner = new(10);
        internal bool Banned { get; set; }
        public async ValueTask<LauncherAccount?> FindAsync(string username, CancellationToken cancellationToken)
        {
            var account = await _inner.FindAsync(username, cancellationToken);
            return Banned && account is not null ? account with { BannedUntil = DateTime.UtcNow.AddDays(1) } : account;
        }
        public ValueTask<bool> RegisterAsync(string username, string passwordHash, CancellationToken cancellationToken) => _inner.RegisterAsync(username, passwordHash, cancellationToken);
        public ValueTask<bool> TrySetInitialPasswordAsync(string username, string passwordHash, CancellationToken cancellationToken) => _inner.TrySetInitialPasswordAsync(username, passwordHash, cancellationToken);
    }

    private sealed class Registry(IReadOnlyList<ISession> sessions) : ISessionRegistry
    {
        internal IReadOnlyList<ISession> Sessions { get; set; } = sessions;
        internal bool ThrowOnRead { get; init; }
        public IReadOnlyList<ISession> All => ThrowOnRead ? throw new InvalidOperationException("Registry must not be read.") : Sessions;
        public void Add(ISession session) => throw new NotSupportedException();
        public void Remove(ISession session) => throw new NotSupportedException();
    }

    private sealed class Session(long id, PlayerIdentity? identity, bool closed = false, long? loginConnectionId = null) : ISessionWithFeatures, IConnection, ISessionFeatures
    {
        internal IPlayerSession? Player { get; init; } = identity is null ? null : new PlayerSession(identity, loginConnectionId ?? id);
        internal bool Closed { get; set; } = closed;
        public long Id => id;
        public IConnection Connection => this;
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features => this;
        public int Capacity => 1;
        public int Count => Player is null ? 0 : 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 12345);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public bool IsClosed => Closed;
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask CloseAsync(string reason) => throw new NotSupportedException();
        public IDisposable Register<T>(T feature) where T : class => throw new NotSupportedException();
        public T Get<T>() where T : class => TryGet<T>(out var result) ? result : throw new InvalidOperationException();
        public bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class
        {
            feature = typeof(T) == typeof(IPlayerSession) ? Player as T : null;
            return feature is not null;
        }
    }
}
