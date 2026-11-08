using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherServerSelectionTests
{
    [Fact]
    public async Task IssuedGrantsFreezeAndSerializeOptionalShortNames()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var snapshot = Snapshot(clock.Now);
        var entries = snapshot.Servers.Select(entry => entry with { ShortName = "Region " + entry.Id }).ToArray();
        snapshot = snapshot with { Servers = entries };
        var issued = sessions.IssueForServers(bearer.AccessToken, "kr-812", snapshot, "a", "policy-1");
        entries[0] = entries[0] with { ShortName = "Changed" };

        var grant = Assert.IsType<LauncherServerSelectionGrant>(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812"));

        Assert.Equal("Region 1", grant.Snapshot.Servers[0].ShortName);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(grant));
        Assert.Equal("Region 1", json.RootElement.GetProperty("Snapshot").GetProperty("Servers")[0].GetProperty("short_name").GetString());
        Assert.Equal(snapshot.Servers[1].Name, grant.Snapshot.Servers[1].Name);
    }

    [Fact]
    public async Task TargetCountUsesTheConfiguredGrantBudgetInsteadOfAnUnrelatedPerSelectorLimit()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions { MaxSelectionTargets = 258 }, new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var snapshot = Snapshot(clock.Now);
        var template = snapshot.Servers[0].Targets[0];
        var targets = Enumerable.Range(1, 129).Select(id => template with { NodeId = "node-" + id, Host = "node-" + id + ".example" }).ToArray();
        snapshot = snapshot with { Servers = [new(1, "Automatic", true, targets), .. targets.Select((target, index) =>
            new ServerSelectorEntry(index + 2, "Server " + target.NodeId, false, [target]))] };

        var issued = sessions.IssueForServers(bearer.AccessToken, "kr-812", snapshot, "node-1", "policy-1");
        var grant = Assert.IsType<LauncherServerSelectionGrant>(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812"));
        Assert.Equal(129, grant.Snapshot.Servers[0].Targets.Count);
        Assert.Equal("player", (await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "node-129", default))?.Username);
    }

    [Fact]
    public async Task GrantRetainsEveryConfiguredSelectorWithoutAnUnrelatedThirtyTwoEntryLimit()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var snapshot = Snapshot(clock.Now);
        snapshot = snapshot with { Servers = [.. snapshot.Servers, .. Enumerable.Range(4, 62)
            .Select(id => new ServerSelectorEntry(id, "Server " + id, false, []))] };

        var issued = sessions.IssueForServers(bearer.AccessToken, "kr-812", snapshot, "a", "policy-1");
        var grant = Assert.IsType<LauncherServerSelectionGrant>(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812"));
        Assert.Equal(65, grant.Snapshot.Servers.Count);
        Assert.Equal(Enumerable.Range(1, 65), grant.Snapshot.Servers.Select(server => server.Id));
        Assert.Equal("player", sessions.Redeem(issued.Token, "kr-812"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("independent")]
    public async Task IndependentManualDataScopeStillReceivesAnAuthorityBoundSingleUseTicket(string dataScope)
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var snapshot = WithIndependent(Snapshot(clock.Now), dataScope);
        var issued = sessions.IssueForServers(bearer.AccessToken, "kr-812", snapshot, "a", "policy-1");
        var grant = Assert.IsType<LauncherServerSelectionGrant>(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812"));

        Assert.Equal(dataScope, Assert.Single(grant.Snapshot.Servers.Single(server => server.Id == 4).Targets).DataScope);
        Assert.DoesNotContain(grant.Snapshot.Servers[0].Targets, target => target.NodeId == "independent");
        Assert.Null(await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "unadmitted", default));
        Assert.Equal("player", (await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "independent", default))?.Username);
        Assert.Null(await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "a", default));
    }

    [Theory]
    [InlineData(true, "master-issued", "authority", "authority", "https://authority.example")]
    [InlineData(false, "own-authority", "authority", "authority", "https://authority.example")]
    [InlineData(false, "master-issued", "node", "authority", "https://authority.example")]
    [InlineData(false, "master-issued", "authority", "foreign", "https://authority.example")]
    [InlineData(false, "master-issued", "authority", "authority", "https://other.example")]
    public async Task IssuanceRejectsIndependentAutomaticOrUntrustedManualTargetsBeforeRetainingTickets(
        bool automatic, string mode, string policy, string authority, string origin)
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions { MaxSessions = 1 }, new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var snapshot = WithIndependent(Snapshot(clock.Now), "independent");
        var entries = snapshot.Servers.ToArray();
        var independent = entries[^1].Targets.Single();
        entries[^1] = entries[^1] with { Automatic = automatic,
            Targets = [independent with { Authentication = new(authority, origin, mode, policy) }] };

        Assert.Throws<ArgumentException>(() => sessions.IssueForServers(bearer.AccessToken, "kr-812",
            snapshot with { Servers = entries }, "a", "policy-1"));
        var ordinary = sessions.Issue(bearer.AccessToken, "kr-812");
        Assert.Equal("player", sessions.Redeem(ordinary.Token, "kr-812"));
    }

    [Fact]
    public async Task HttpLaunchAndRefreshAdmitAnIndependentManualNodeWithoutAddingItToAutomatic()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000,
            Center = new() { Enabled = true },
            Instances = [new() { Id = "kr-812", Build = "kr-812", Port = 7110, Name = [new() { Language = "en", Text = "Client" }] }] };
        var accounts = new MemoryLauncherAccounts(8);
        Assert.True(await accounts.RegisterAsync("player", "not-used-for-bearer-authentication", default));
        var sessions = new LauncherSessions(options, new MemoryLauncherSessions(8));
        var bearer = await sessions.SignInAsync("player", default);
        var source = new SelectionSource { Independent = true };
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions, players: null, directory: source, serverSelection: source);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.AccessToken);

        using var launched = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812", serverSelection = true });
        launched.EnsureSuccessStatusCode();
        var launch = await launched.Content.ReadFromJsonAsync<JsonElement>();
        var ticket = launch.GetProperty("ticket").GetString()!;
        var selection = launch.GetProperty("serverSelection");
        var grantId = selection.GetProperty("grantId").GetString()!;
        using var refreshed = await http.PostAsJsonAsync("/v1/launch/servers", new { build = "kr-812", ticket, grantId });
        refreshed.EnsureSuccessStatusCode();
        var entries = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("serverSelection").GetProperty("servers");
        Assert.Equal(4, entries.GetArrayLength());
        Assert.Equal("", entries[3].GetProperty("targets")[0].GetProperty("dataScope").GetString());
        Assert.DoesNotContain(entries[0].GetProperty("targets").EnumerateArray(), target => target.GetProperty("nodeId").GetString() == "independent");
        Assert.Equal("player", (await sessions.RedeemForNodeAsync(ticket, "kr-812", "independent", default))?.Username);
        using var closed = await http.PostAsJsonAsync("/v1/launch/servers", new { build = "kr-812", ticket, grantId });
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
    }

    [Fact]
    public async Task SelectionBudgetRefusesAnOversizedGrantBeforeRetainingATicket()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions { MaxSessions = 1, MaxSelectionTargets = 1 }, new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        Assert.Throws<ArgumentException>(() => sessions.IssueForServers(bearer.AccessToken, "kr-812", Snapshot(clock.Now), "a", "policy-1"));
        var ordinary = sessions.Issue(bearer.AccessToken, "kr-812");
        Assert.Equal("player", sessions.Redeem(ordinary.Token, "kr-812"));
    }

    [Fact]
    public async Task ScopedTicketRetainsSlowStartupLifetimeAndWrongNodeDoesNotConsumeIt()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var issued = sessions.IssueForServers(bearer.AccessToken, "kr-812", Snapshot(clock.Now), "a", "policy-1");
        var grant = Assert.IsType<LauncherServerSelectionGrant>(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812"));
        Assert.Equal(clock.Now.AddMinutes(10), issued.ExpiresAt);
        Assert.Equal(issued.ExpiresAt, grant.ExpiresAtUtc);
        Assert.NotEqual(issued.Token, grant.GrantId);
        clock.Now = clock.Now.AddSeconds(599);
        Assert.Null(await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "foreign", default));
        Assert.Null(await sessions.RedeemForNodeAsync(issued.Token, "kr-1256", "a", default));
        Assert.NotNull(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812", grant.GrantId));
        Assert.Equal("player", (await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "b", default))?.Username);
        Assert.Null(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812", grant.GrantId));
        Assert.Null(sessions.Redeem(issued.Token, "kr-812"));
    }

    [Fact]
    public async Task MembershipIsOwnedByTheTicketAndLocalRedemptionUsesItsExplicitNode()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var snapshot = Snapshot(clock.Now);
        var targets = (ServerNodeTarget[])snapshot.Servers[0].Targets;
        var issued = sessions.IssueForServers(bearer.AccessToken, "kr-812", snapshot, "issuer-not-in-pool", "policy-1");
        targets[0] = targets[0] with { NodeId = "foreign" };
        Assert.Null(sessions.Redeem(issued.Token, "kr-812"));
        Assert.Null(await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "foreign", default));
        var grant = Assert.IsType<LauncherServerSelectionGrant>(sessions.Selection(bearer.AccessToken, issued.Token, "kr-812"));
        Assert.Equal("a", grant.Snapshot.Servers[0].Targets[0].NodeId);
        Assert.Equal("player", (await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "a", default))?.Username);
    }

    [Fact]
    public async Task RefreshReadsRequireTheSameSessionBuildAndGrantAndCannotRenewExpiry()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(8), clock);
        var first = await sessions.SignInAsync("player", default);
        var other = await sessions.SignInAsync("player", default);
        var issued = sessions.IssueForServers(first.AccessToken, "kr-812", Snapshot(clock.Now), "a", "policy-1");
        var grant = Assert.IsType<LauncherServerSelectionGrant>(sessions.Selection(first.AccessToken, issued.Token, "kr-812"));
        Assert.Null(sessions.Selection(other.AccessToken, issued.Token, "kr-812", grant.GrantId));
        Assert.Null(sessions.Selection(first.AccessToken, issued.Token, "kr-1256", grant.GrantId));
        Assert.Null(sessions.Selection(first.AccessToken, issued.Token, "kr-812", "wrong-grant"));
        clock.Now = clock.Now.AddSeconds(599);
        Assert.Equal(issued.ExpiresAt, sessions.Selection(first.AccessToken, issued.Token, "kr-812", grant.GrantId)?.ExpiresAtUtc);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Null(sessions.Selection(first.AccessToken, issued.Token, "kr-812", grant.GrantId));
        Assert.Null(await sessions.RedeemForNodeAsync(issued.Token, "kr-812", "a", default));
    }

    [Fact]
    public async Task TwoAdmittedWorkersStillConsumeTheSameTicketExactlyOnce()
    {
        var clock = new Clock();
        var sessions = new LauncherSessions(new LauncherOptions(), new MemoryLauncherSessions(8), clock);
        var bearer = await sessions.SignInAsync("player", default);
        var issued = sessions.IssueForServers(bearer.AccessToken, "kr-812", Snapshot(clock.Now), "a", "policy-1");
        using var start = new ManualResetEventSlim();
        Task<LauncherTicketIdentity?> Redeem(string node) => Task.Run(async () =>
        {
            start.Wait();
            return await sessions.RedeemForNodeAsync(issued.Token, "kr-812", node, default);
        });
        var first = Redeem("a"); var second = Redeem("b");
        start.Set();
        Assert.Single(await Task.WhenAll(first, second), identity => identity is not null);
    }

    [Fact]
    public async Task HttpLaunchAndRefreshKeepGrantIdentityAndReportClosedAdmissionAfterRedemption()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000,
            Center = new() { Enabled = true },
            Instances = [new() { Id = "kr-812", Build = "kr-812", Port = 7110, Name = [new() { Language = "en", Text = "Client" }] }] };
        var accounts = new MemoryLauncherAccounts(8);
        Assert.True(await accounts.RegisterAsync("player", "not-used-for-bearer-authentication", default));
        var sessions = new LauncherSessions(options, new MemoryLauncherSessions(8));
        var bearer = await sessions.SignInAsync("player", default);
        var source = new SelectionSource();
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions, players: null, directory: source, serverSelection: source);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.AccessToken);
        using var bypass = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812", serverSelection = false });
        Assert.Equal(HttpStatusCode.Conflict, bypass.StatusCode);
        Assert.Equal("server_selection_required", (await bypass.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        using var legacyRequest = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812" });
        legacyRequest.EnsureSuccessStatusCode();
        var protectedLegacy = await legacyRequest.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(protectedLegacy.TryGetProperty("serverSelection", out _));
        Assert.Null(await sessions.RedeemForNodeAsync(protectedLegacy.GetProperty("ticket").GetString()!, "kr-812", "foreign", default));
        using var launched = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812", serverSelection = true });
        launched.EnsureSuccessStatusCode();
        var launch = await launched.Content.ReadFromJsonAsync<JsonElement>();
        var ticket = launch.GetProperty("ticket").GetString()!;
        var selection = launch.GetProperty("serverSelection");
        Assert.Equal(1, selection.GetProperty("schema").GetInt32());
        Assert.Equal(launch.GetProperty("expiresAt").GetDateTimeOffset(), selection.GetProperty("expiresAtUtc").GetDateTimeOffset());
        Assert.Equal(64, selection.GetProperty("policyRevision").GetString()!.Length);
        Assert.Equal("authority", selection.GetProperty("authorityId").GetString());
        Assert.True(selection.GetProperty("servers")[0].GetProperty("targets")[0].GetProperty("expires_at_utc").GetDateTimeOffset()
            < selection.GetProperty("expiresAtUtc").GetDateTimeOffset());
        var grantId = selection.GetProperty("grantId").GetString()!;
        using var refreshed = await http.PostAsJsonAsync("/v1/launch/servers", new { build = "kr-812", ticket, grantId });
        refreshed.EnsureSuccessStatusCode();
        var view = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("serverSelection");
        Assert.Equal(grantId, view.GetProperty("grantId").GetString());
        Assert.Equal(selection.GetProperty("policyRevision").GetString(), view.GetProperty("policyRevision").GetString());
        Assert.Equal(1, source.Refreshes);
        source.Automatic = false;
        using var disabled = await http.PostAsJsonAsync("/v1/launch/servers", new { build = "kr-812", ticket, grantId });
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal(1, source.Refreshes);
        source.Automatic = true;
        Assert.Equal("player", (await sessions.RedeemForNodeAsync(ticket, "kr-812", "b", default))?.Username);
        using var closed = await http.PostAsJsonAsync("/v1/launch/servers", new { build = "kr-812", ticket, grantId });
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Equal("admission_closed", (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(1, source.Refreshes);
    }

    [Fact]
    public async Task UnmountedSelectionCannotSilentlyIssueAnUnrestrictedTicket()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000,
            Instances = [new() { Id = "kr-812", Build = "kr-812", Port = 7110, Name = [new() { Language = "en", Text = "Client" }] }] };
        var accounts = new MemoryLauncherAccounts(8);
        Assert.True(await accounts.RegisterAsync("player", "not-used", default));
        var sessions = new LauncherSessions(options, new MemoryLauncherSessions(8));
        var bearer = await sessions.SignInAsync("player", default);
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.AccessToken);
        using var unsupported = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812", serverSelection = true });
        Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode);
        using var ordinary = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812" });
        ordinary.EnsureSuccessStatusCode();
        var launch = await ordinary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(launch.TryGetProperty("serverSelection", out _));
        Assert.Equal("player", sessions.Redeem(launch.GetProperty("ticket").GetString()!, "kr-812"));
    }

    [Theory]
    [InlineData(false, "native", true)]
    [InlineData(false, "gateway", true)]
    [InlineData(true, "native", true)]
    [InlineData(true, "gateway", false)]
    public async Task DisabledNativeAndManualModesNeverIssueAutomaticGrants(bool enabled, string mode, bool automatic)
    {
        var options = new LauncherOptions
        {
            Listen = "127.0.0.1:0", PasswordIterations = 100000, Center = new() { Enabled = enabled, Mode = mode },
            Instances = [new() { Id = "kr-812", Build = "kr-812", Port = 7110, Name = [new() { Language = "en", Text = "Client" }] }],
        };
        var accounts = new MemoryLauncherAccounts(8);
        Assert.True(await accounts.RegisterAsync("player", "not-used", default));
        var sessions = new LauncherSessions(options, new MemoryLauncherSessions(8));
        var bearer = await sessions.SignInAsync("player", default);
        var source = new SelectionSource { Automatic = automatic };
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions, players: null, directory: source, serverSelection: source);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.AccessToken);
        foreach (bool? requested in new bool?[] { null, false, true })
        {
            var body = new Dictionary<string, object> { ["build"] = "kr-812" };
            if (requested is { } flag) body["serverSelection"] = flag;
            using var response = await http.PostAsJsonAsync("/v1/launch", body);
            if (requested == true)
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("server_selection_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
            }
            else
            {
                response.EnsureSuccessStatusCode();
                var launch = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(launch.TryGetProperty("serverSelection", out _));
                Assert.Equal("player", sessions.Redeem(launch.GetProperty("ticket").GetString()!, "kr-812"));
            }
        }
        Assert.Equal(0, source.Captures);
        Assert.Equal(0, source.Refreshes);
    }

    [Fact]
    public async Task ConfiguredAutomaticModeWithoutItsIssuerRefusesLegacyAndExplicitRequests()
    {
        var options = new LauncherOptions
        {
            Listen = "127.0.0.1:0", PasswordIterations = 100000, Center = new() { Enabled = true, Mode = "gateway" },
            Instances = [new() { Id = "kr-812", Build = "kr-812", Port = 7110, Name = [new() { Language = "en", Text = "Client" }] }],
        };
        var accounts = new MemoryLauncherAccounts(8);
        Assert.True(await accounts.RegisterAsync("player", "not-used", default));
        var sessions = new LauncherSessions(options, new MemoryLauncherSessions(8));
        var bearer = await sessions.SignInAsync("player", default);
        var source = new SelectionSource();
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions, players: null, directory: source);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.AccessToken);
        using var legacy = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812" });
        using var explicitRequest = await http.PostAsJsonAsync("/v1/launch", new { build = "kr-812", serverSelection = true });
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, explicitRequest.StatusCode);
        Assert.Equal(0, source.Captures);
    }

    private static LauncherServerSelectionSnapshot Snapshot(DateTimeOffset now)
    {
        ServerNodeTarget Target(string id) => new(id, id + ".example", 7110, "shared", "", true, false, 0, 8)
        { Authentication = new("authority", "https://authority.example", "master-issued", "authority"),
            ObservedAtUtc = now, ExpiresAtUtc = now.AddSeconds(15), StatusRevision = "report-1" };
        return new("authority", "shared", now, [new(1, "Automatic", true, new[] { Target("a"), Target("b") }),
            new(2, "Manual A", false, [Target("a")]), new(3, "Manual B", false, [Target("b")])]);
    }

    private static LauncherServerSelectionSnapshot WithIndependent(LauncherServerSelectionSnapshot snapshot, string dataScope)
    {
        var target = snapshot.Servers[0].Targets[0] with { NodeId = "independent", Host = "independent.example", DataScope = dataScope };
        return snapshot with { Servers = [.. snapshot.Servers, new(4, "Independent", false, [target])] };
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SelectionSource : ILauncherServerSelection, IServerDirectory
    {
        public string LocalNodeId => "a";
        internal int Refreshes { get; private set; }
        internal int Captures { get; private set; }
        internal bool Automatic { get; set; } = true;
        internal bool Independent { get; set; }
        private LauncherServerSelectionSnapshot Current() => Independent
            ? WithIndependent(Snapshot(DateTimeOffset.UtcNow), "") : Snapshot(DateTimeOffset.UtcNow);
        public IReadOnlyList<ServerSelectorEntry> ListServers(string build) => Current().Servers
            .Where(server => Automatic || !server.Automatic).ToArray();
        public IReadOnlyList<FederatedRoom> ListRooms(string node, string build) => throw new NotSupportedException();
        public LauncherServerSelectionSnapshot Capture(string build) { Captures++; return Current(); }
        public LauncherServerSelectionSnapshot Refresh(string build, LauncherServerSelectionSnapshot original)
        { Refreshes++; return original with { GeneratedAtUtc = DateTimeOffset.UtcNow }; }
    }
}
