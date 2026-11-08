using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Launcher;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class LaunchSelectionSourceTests
{
    [Fact]
    public void RefreshPreservesTheAdmittedShortNameAlongsideTheFullName()
    {
        var clock = new Clock();
        var target = Target("a", clock.Now);
        var directory = new Directory { Entries = [new(1, "Automatic", true, [target]),
            new(2, "Region (Provider)", false, [target]) { ShortName = "Region" }] };
        var source = new LaunchSelectionSource(Options(), directory, clock);
        var original = source.Capture("kr-812");
        directory.Entries = [directory.Entries[0], directory.Entries[1] with { Name = "Renamed", ShortName = "Changed" }];

        var refreshed = source.Refresh("kr-812", original);

        Assert.Equal("Region (Provider)", refreshed.Servers[1].Name);
        Assert.Equal("Region", refreshed.Servers[1].ShortName);
        Assert.Single(refreshed.Servers[1].Targets);
        Assert.Null(refreshed.Servers[0].ShortName);
    }

    [Fact]
    public void MembershipExcludesForeignAuthoritiesAndDataScopesWithoutInventingHealthForConfiguredNodes()
    {
        var clock = new Clock();
        var valid = Target("a", clock.Now);
        ServerNodeTarget[] targets = [valid, valid with { NodeId = "unknown", ObservedAtUtc = null, ExpiresAtUtc = null, StatusRevision = null },
            valid with { NodeId = "stale", ExpiresAtUtc = clock.Now }, valid with { NodeId = "full", Players = 8 },
            valid with { NodeId = "draining", Draining = true }, valid with { NodeId = "data", DataScope = "independent" },
            valid with { NodeId = "authority", Authentication = valid.Authentication! with { AuthorityId = "foreign" } },
            valid with { NodeId = "origin", Authentication = valid.Authentication! with { ApiUrl = "https://other.example" } },
            valid with { NodeId = "own", Authentication = valid.Authentication! with { Mode = "own-authority" } }];
        var directory = new Directory { Entries = [new(1, "Automatic", true, targets),
            .. targets.Take(5).Select((target, index) => new ServerSelectorEntry(index + 2, "Manual " + target.NodeId, false, [target]))] };
        var snapshot = new LaunchSelectionSource(Options(), directory, clock).Capture("kr-812");
        Assert.Equal(new[] { "a", "unknown", "stale", "full", "draining" }, snapshot.Servers[0].Targets.Select(target => target.NodeId));
        Assert.Null(snapshot.Servers[0].Targets[1].ObservedAtUtc);
        Assert.Null(snapshot.Servers[0].Targets[1].ExpiresAtUtc);
        Assert.Null(snapshot.Servers[0].Targets[1].StatusRevision);
        Assert.Equal("a", Assert.Single(snapshot.Servers[1].Targets).NodeId);
    }

    [Fact]
    public void RefreshCannotAddMembersChangeEndpointBindingsOrNativeSelectorIdentities()
    {
        var clock = new Clock();
        var a = Target("a", clock.Now);
        var directory = new Directory { Entries = [new(1, "Automatic", true, [a]), new(2, "Manual", false, [a])] };
        var source = new LaunchSelectionSource(Options(), directory, clock);
        var original = source.Capture("kr-812");
        clock.Now = clock.Now.AddSeconds(10);
        var fresh = a with { ObservedAtUtc = clock.Now, ExpiresAtUtc = clock.Now.AddSeconds(15), StatusRevision = "new-incarnation" };
        var added = Target("new-node", clock.Now);
        directory.Entries = [new(1, "Automatic", true, [fresh, added]),
            new(2, "Manual", false, [fresh]), new(4, "New member", false, [added])];
        var refreshed = source.Refresh("kr-812", original);
        Assert.Equal("a", Assert.Single(refreshed.Servers[0].Targets).NodeId);
        Assert.Equal("new-incarnation", refreshed.Servers[0].Targets[0].StatusRevision);
        Assert.Equal(2, refreshed.Servers[1].Id);
        Assert.Equal("a", Assert.Single(refreshed.Servers[1].Targets).NodeId);
        directory.Entries = [new(1, "Automatic", true, [fresh]), new(3, "Replacement native ID", false, [fresh])];
        Assert.All(source.Refresh("kr-812", original).Servers, entry => Assert.Empty(entry.Targets));
        var replaced = fresh with { Host = "replacement.example" };
        directory.Entries = [new(1, "Automatic", true, [replaced]), new(2, "Manual", false, [replaced])];
        Assert.All(source.Refresh("kr-812", original).Servers, entry => Assert.Empty(entry.Targets));
        directory.Entries = [new(1, "Automatic", true, [fresh]), new(2, "Manual", false, [fresh])];
        Assert.Equal("a", Assert.Single(source.Refresh("kr-812", original).Servers[0].Targets).NodeId);
    }

    [Fact]
    public void ManualMasterIssuedTargetMayKeepAnIndependentDataScope()
    {
        var clock = new Clock();
        var automatic = Target("a", clock.Now);
        var independent = automatic with { NodeId = "independent", DataScope = "" };
        var directory = new Directory
        {
            Entries =
            [
                new(1, "Automatic", true, [automatic]),
                new(2, "Primary", false, [automatic]),
                new(3, "Test", false, [independent]),
            ],
        };

        var snapshot = new LaunchSelectionSource(Options(), directory, clock).Capture("kr-812");

        Assert.Equal(["a", "independent"], snapshot.Servers.SelectMany(entry => entry.Targets).Select(target => target.NodeId).Distinct());
        Assert.Equal("", Assert.Single(snapshot.Servers.Single(entry => entry.Id == 3).Targets).DataScope);

        clock.Now = clock.Now.AddSeconds(5);
        directory.Entries =
        [
            new(1, "Automatic", true, [automatic with { ObservedAtUtc = clock.Now, ExpiresAtUtc = clock.Now.AddSeconds(15), StatusRevision = "automatic-next" }]),
            new(2, "Primary", false, [automatic]),
            new(3, "Test", false, [independent with { ObservedAtUtc = clock.Now, ExpiresAtUtc = clock.Now.AddSeconds(15), StatusRevision = "manual-next" }]),
        ];

        var refreshed = new LaunchSelectionSource(Options(), directory, clock).Refresh("kr-812", snapshot);
        var refreshedManual = Assert.Single(refreshed.Servers.Single(entry => entry.Id == 3).Targets);
        Assert.Equal("independent", refreshedManual.NodeId);
        Assert.Equal("manual-next", refreshedManual.StatusRevision);
    }

    [Theory]
    [InlineData("master-issued", "authority", "authority", "https://authority.example", true)]
    [InlineData("own-authority", "authority", "authority", "https://authority.example", false)]
    [InlineData("master-issued", "node", "authority", "https://authority.example", false)]
    [InlineData("master-issued", "authority", "foreign", "https://authority.example", false)]
    [InlineData("master-issued", "authority", "authority", "https://other.example", false)]
    public void IndependentManualAdmissionStillRequiresTheConfiguredMasterAuthority(
        string mode, string policy, string authority, string origin, bool admitted)
    {
        var clock = new Clock();
        var primary = Target("a", clock.Now);
        var independent = primary with { NodeId = "independent", DataScope = "separate",
            Authentication = new(authority, origin, mode, policy) };
        var directory = new Directory { Entries = [new(1, "Automatic", true, [primary, independent]),
            new(2, "Primary", false, [primary]), new(3, "Independent", false, [independent])] };
        var source = new LaunchSelectionSource(Options(), directory, clock);
        var snapshot = source.Capture("kr-812");

        Assert.Equal("a", Assert.Single(snapshot.Servers[0].Targets).NodeId);
        Assert.Equal(admitted ? 1 : 0, snapshot.Servers[2].Targets.Count);
        Assert.Equal(admitted ? 1 : 0, source.Refresh("kr-812", snapshot).Servers[2].Targets.Count);
        directory.Entries = [new(1, "Automatic", true, [primary]), new(2, "Primary", false, [primary]),
            new(3, "Independent", false, [independent with { Host = "replacement.example" }])];
        Assert.Empty(source.Refresh("kr-812", snapshot).Servers[2].Targets);
    }

    [Fact]
    public void IndependentManualAliasesRefreshByTheirOriginalSelectorIdentity()
    {
        var clock = new Clock();
        var primary = Target("a", clock.Now);
        var independent = primary with { NodeId = "independent", DataScope = "" };
        var directory = new Directory { Entries = [new(1, "Automatic", true, [primary]),
            new(2, "Primary", false, [primary]), new(3, "Independent A", false, [independent]),
            new(4, "Independent B", false, [independent])] };
        var source = new LaunchSelectionSource(Options(), directory, clock);
        var original = source.Capture("kr-812");

        var refreshed = source.Refresh("kr-812", original);
        Assert.Equal("independent", Assert.Single(refreshed.Servers[2].Targets).NodeId);
        Assert.Equal("independent", Assert.Single(refreshed.Servers[3].Targets).NodeId);
        directory.Entries = [new(1, "Automatic", true, [primary]), new(2, "Primary", false, [primary]),
            new(3, "Independent A", false, [independent]), new(5, "Independent B", false, [independent])];
        refreshed = source.Refresh("kr-812", original);
        Assert.Single(refreshed.Servers[2].Targets);
        Assert.Empty(refreshed.Servers[3].Targets);
    }

    [Fact]
    public void ExpiredHealthDoesNotBecomeFreshBecauseARefreshWasRequested()
    {
        var clock = new Clock();
        var a = Target("a", clock.Now);
        var directory = new Directory { Entries = [new(1, "Automatic", true, [a]), new(2, "Manual", false, [a])] };
        var source = new LaunchSelectionSource(Options(), directory, clock);
        var original = source.Capture("kr-812");
        clock.Now = clock.Now.AddSeconds(16);
        var refreshed = source.Refresh("kr-812", original);
        Assert.Equal(a.ExpiresAtUtc, Assert.Single(refreshed.Servers[0].Targets).ExpiresAtUtc);
        Assert.True(refreshed.Servers[0].Targets[0].ExpiresAtUtc < refreshed.GeneratedAtUtc);
        Assert.Equal(clock.Now, refreshed.GeneratedAtUtc);
        Assert.Throws<InvalidOperationException>(() => source.Refresh("kr-812", original with { DataScope = "foreign" }));
    }

    [Fact]
    public void MissingOrAmbiguousCanonicalNativeMappingFailsBeforePublishingTheSource()
    {
        var clock = new Clock();
        var a = Target("a", clock.Now);
        var directory = new Directory { Entries = [new(1, "Automatic", true, [a])] };
        var source = new LaunchSelectionSource(Options(), directory, clock);
        Assert.Throws<InvalidOperationException>(() => source.ValidateConfiguration());
        directory.Entries = [new(1, "Automatic", true, [a]), new(2, "Manual", false, [a]), new(3, "Ambiguous", false, [a])];
        Assert.Throws<InvalidOperationException>(() => source.ValidateConfiguration());
        directory.Entries = [new(1, "Automatic", true, [a]), new(2, "Manual", false, [a])];
        source.ValidateConfiguration();
    }

    private static LaunchSelectionOptions Options() => new()
    { LocalNodeId = "a", AuthorityId = "authority", AuthorityUrl = "https://authority.example", DataScope = "shared" };

    private static ServerNodeTarget Target(string id, DateTimeOffset now) => new(id, id + ".example", 7110, "shared", "", true, false, 0, 8)
    { Authentication = new("authority", "https://authority.example", "master-issued", "authority"),
        ObservedAtUtc = now, ExpiresAtUtc = now.AddSeconds(15), StatusRevision = "report-1" };

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Directory : IServerSelectionDirectory
    {
        public IReadOnlyList<string> ClientBuilds => ["kr-812"];
        internal IReadOnlyList<ServerSelectorEntry> Entries { get; set; } = [];
        public IReadOnlyList<ServerSelectorEntry> ListSelectionServers(string clientBuild) => Entries;
    }
}
