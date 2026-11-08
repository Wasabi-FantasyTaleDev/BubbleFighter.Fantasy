using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Cluster;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class ServerDirectoryTests
{
    [Fact]
    public void OptionalShortNamesRoundTripWithoutChangingFullNamesOrEmptyEntries()
    {
        var options = Options(Node("a", "formal", []));
        options.Selectors[1].ShortName = "Region";
        options.Selectors = [.. options.Selectors, new() { Id = 3, Name = "Reserved", ShortName = "Soon" }];
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options, new Clock());
        foreach (var entries in new[] { directory.ListServers("kr-1256"), directory.ListSelectionServers("kr-1256") })
        {
            Assert.Null(entries[0].ShortName);
            Assert.Equal("a", entries[1].Name);
            Assert.Equal("Region", entries[1].ShortName);
            Assert.Equal("Soon", entries[2].ShortName);
            Assert.Empty(entries[2].Targets);
            var json = System.Text.Json.JsonSerializer.Serialize(entries);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            Assert.False(document.RootElement[0].TryGetProperty("short_name", out _));
            Assert.Equal("Region", document.RootElement[1].GetProperty("short_name").GetString());
            var restored = System.Text.Json.JsonSerializer.Deserialize<ServerSelectorEntry[]>(json)!;
            Assert.Equal("a", restored[1].Name);
            Assert.Equal("Region", restored[1].ShortName);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    public void PresentShortNamesRejectInvalidText(string text)
    {
        var options = Options(Node("a", "formal", []));
        options.Selectors[1].ShortName = text;
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void ShortNamesShareTheDisplayNameTextBudget()
    {
        var options = Options(Node("a", "formal", []));
        options.Selectors[1].ShortName = new string('x', 128);
        Assert.Empty(options.Validate());
        options.Selectors[1].ShortName += "x";
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void ShortNamesRejectUnpairedSurrogatesAndAcceptCompletePairs()
    {
        var options = Options(Node("a", "formal", []));
        foreach (var text in new[] { "\uD800", "\uDC00", "\uD800x" })
        {
            options.Selectors[1].ShortName = text;
            Assert.NotEmpty(options.Validate());
        }
        options.Selectors[1].ShortName = "\uD840\uDC00";
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void NodeBudgetCanBeRaisedWithoutChangingTheNativeDirectoryShape()
    {
        var options = Options(Enumerable.Range(1, 129).Select(id => Node("node-" + id, "formal", [])).ToArray());
        Assert.NotEmpty(options.Validate());
        options.MaxNodes = 129;
        Assert.Empty(options.Validate());

        var entries = new ServerDirectory(options, new Clock()).ListSelectionServers("kr-1256");
        Assert.Equal(130, entries.Count);
        Assert.Equal("node-129", Assert.Single(entries[^1].Targets).NodeId);
    }

    [Fact]
    public void DirectoryPreservesEveryConfiguredSelectorBeyondThirtyTwoIncludingEmptyEntries()
    {
        var options = Options(Node("a", "formal", []));
        options.Selectors = [.. options.Selectors, .. Enumerable.Range(3, 63)
            .Select(id => new SelectorOptions { Id = id, Name = "Server " + id })];
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options, new Clock());

        var entries = directory.ListSelectionServers("kr-1256");
        Assert.Equal(65, entries.Count);
        Assert.Equal(Enumerable.Range(1, 65), entries.Select(entry => entry.Id));
        Assert.All(entries.Skip(2), entry => Assert.Empty(entry.Targets));
    }

    [Fact]
    public void AdmissionMembershipRetainsConfiguredNodesWhilePlacementStillRequiresActualHealth()
    {
        var (directory, time) = Create(Node("a", "formal", []));
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        var configured = Assert.Single(directory.ListSelectionServers("kr-1256")[0].Targets);
        Assert.False(configured.Online);
        Assert.Null(configured.ObservedAtUtc);
        Report(directory, "a");
        Assert.True(Assert.Single(directory.ListSelectionServers("kr-1256")[0].Targets).Online);
        Assert.Single(directory.ListServers("kr-1256")[0].Targets);
        time.Advance(16);
        var expired = Assert.Single(directory.ListSelectionServers("kr-1256")[0].Targets);
        Assert.False(expired.Online);
        Assert.NotNull(expired.ObservedAtUtc);
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
    }

    [Fact]
    public void OnlyAnAcceptedReportCreatesObservationMetadata()
    {
        var (directory, _) = Create(Node("a", "formal", []));
        void AssertUnobserved()
        {
            var target = Assert.Single(directory.ListServers("kr-1256")[1].Targets);
            Assert.Null(target.ObservedAtUtc);
            Assert.Null(target.ExpiresAtUtc);
            Assert.Null(target.StatusRevision);
        }
        AssertUnobserved();
        var grant = Grant(directory, "a");
        AssertUnobserved();
        Assert.False(directory.Observe("a", new(grant.Token, grant.Epoch, 0, false, DataDomainId, [], [])));
        AssertUnobserved();
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, DataDomainId, [new("kr-1256", true, 2)], [])));
        var observed = Assert.Single(directory.ListServers("kr-1256")[1].Targets);
        Assert.Equal(DateTimeOffset.UnixEpoch, observed.ObservedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(15), observed.ExpiresAtUtc);
        Assert.NotNull(observed.StatusRevision);
        Assert.InRange(observed.StatusRevision.Length, 1, 128);
        Assert.DoesNotContain(grant.Token, observed.StatusRevision, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsAndRejectedReportsCannotRenewObservationAndExpiredMetadataRemainsVisible()
    {
        var (directory, time) = Create(Node("a", "formal", []));
        var instance = Guid.NewGuid().ToString("N");
        var grant = Assert.IsType<LeaseGrant>(directory.Register("a", instance));
        var report = new NodeHeartbeat(grant.Token, grant.Epoch, 1, false, DataDomainId, [new("kr-1256", true, 2)], []);
        Assert.True(directory.Observe("a", report));
        var initial = Assert.Single(directory.ListServers("kr-1256")[1].Targets);
        time.Advance(5);
        Assert.Equal(grant, directory.Register("a", instance));
        Assert.False(directory.Observe("a", report));
        Assert.Equal(initial, Assert.Single(directory.ListServers("kr-1256")[1].Targets));
        time.Advance(11);
        var expired = Assert.Single(directory.ListServers("kr-1256")[1].Targets);
        Assert.False(expired.Online);
        Assert.Equal(initial.ObservedAtUtc, expired.ObservedAtUtc);
        Assert.Equal(initial.ExpiresAtUtc, expired.ExpiresAtUtc);
        Assert.Equal(initial.StatusRevision, expired.StatusRevision);
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        var replacement = Grant(directory, "a");
        Assert.True(directory.Observe("a", report with { LeaseToken = replacement.Token, Epoch = replacement.Epoch }));
        var fresh = Assert.Single(directory.ListServers("kr-1256")[0].Targets);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(16), fresh.ObservedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(31), fresh.ExpiresAtUtc);
        Assert.NotEqual(initial.StatusRevision, fresh.StatusRevision);
    }

    [Fact]
    public void ReleaseCapsTheReportedExpiryWithoutMakingUpAnotherObservation()
    {
        var (directory, time) = Create(Node("a", "formal", []));
        var grant = Grant(directory, "a");
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, DataDomainId, [new("kr-1256", true, 2)], [])));
        var initial = Assert.Single(directory.ListServers("kr-1256")[0].Targets);
        time.Advance(3);
        Assert.True(directory.Release("a", grant.Token));
        var released = Assert.Single(directory.ListServers("kr-1256")[1].Targets);
        Assert.False(released.Online);
        Assert.Equal(initial.ObservedAtUtc, released.ObservedAtUtc);
        Assert.Equal(initial.StatusRevision, released.StatusRevision);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(3), released.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(100)]
    public void UtcClockChangesCannotExtendTheMonotonicObservationLease(int shift)
    {
        var (directory, time) = Create(Node("a", "formal", []));
        Report(directory, "a");
        var initial = Assert.Single(directory.ListServers("kr-1256")[0].Targets);
        time.Advance(5);
        time.ShiftUtc(shift);
        var target = Assert.Single(directory.ListServers("kr-1256")[0].Targets);
        Assert.True(target.ExpiresAtUtc <= initial.ExpiresAtUtc);
        Assert.True(target.ExpiresAtUtc <= time.GetUtcNow().AddSeconds(10));
        Assert.Equal(initial.ObservedAtUtc, target.ObservedAtUtc);
        Assert.Equal(initial.StatusRevision, target.StatusRevision);
    }

    [Fact]
    public void UnconfiguredManualEntryRemainsVisibleWithoutInventingAnEndpoint()
    {
        var options = Options(Node("a", "formal", []));
        options.Selectors = [.. options.Selectors, new() { Id = 3, Name = "Not configured", Nodes = [] }];
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options);
        var unconfigured = directory.ListServers("kr-1256")[2];
        Assert.Equal(3, unconfigured.Id);
        Assert.Equal("Not configured", unconfigured.Name);
        Assert.False(unconfigured.Automatic);
        Assert.Empty(unconfigured.Targets);
        Assert.Single(options.Nodes);
        Assert.All(directory.ListServers("kr-999"), entry => Assert.Empty(entry.Targets));
    }

    [Fact]
    public void AutomaticEntriesStillRequireCandidatesAndManualEntriesCannotHaveMultipleNodes()
    {
        var options = Options(Node("a", "formal", []), Node("b", "formal", []));
        options.Selectors[0].Nodes = [];
        Assert.NotEmpty(options.Validate());
        options.Selectors[0].Nodes = ["a"];
        options.Selectors[1].Nodes = ["a", "b"];
        Assert.NotEmpty(options.Validate());
        options.Selectors[1].Nodes = [];
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void FriendlyRoomsDoNotGrantDataOrJoinAccess()
    {
        var (directory, _) = Create(Node("a", "formal", ["friends"]), Node("b", "", ["friends"]), Node("isolated", "", []));
        Report(directory, "b", rooms: [Room("b")]);
        Report(directory, "isolated", rooms: [Room("isolated")]);
        Assert.Equal(4, directory.ListServers("kr-1256").Count);
        var room = Assert.Single(directory.ListRooms("a", "kr-1256"));
        Assert.Equal("b", room.NodeId);
        Assert.False(room.JoinAllowed);
        Assert.DoesNotContain(directory.ListRooms("isolated", "kr-1256"), room => room.NodeId == "b");
    }

    [Fact]
    public void SharedDataAndRoomGroupsStillRequireActualRemoteRouting()
    {
        var (directory, _) = Create(Node("a", "formal", ["rooms"]), Node("b", "formal", ["rooms"]));
        Report(directory, "b", rooms: [Room("b")]);
        Assert.False(Assert.Single(directory.ListRooms("a", "kr-1256")).JoinAllowed);
        Assert.True(Assert.Single(directory.ListRooms("b", "kr-1256")).JoinAllowed);
    }

    [Fact]
    public void AutomaticPlacementExcludesTheWrongDatabaseWithoutRemovingHealthyCandidates()
    {
        var options = Options(Node("a", "formal", []), Node("b", "formal", []));
        options.Selectors[0].Nodes = ["a", "b"];
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options);
        var a = Grant(directory, "a");
        var b = Grant(directory, "b");
        Assert.True(directory.Observe("a", new(a.Token, a.Epoch, 1, false, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", [new("kr-1256", true, 1)], [])));
        Assert.True(directory.Observe("b", new(b.Token, b.Epoch, 1, false, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", [new("kr-1256", true, 1)], [])));

        Assert.Equal("a", Assert.Single(directory.ListServers("kr-1256")[0].Targets).NodeId);
        Assert.Single(directory.ListServers("kr-1256")[1].Targets);
        Assert.False(directory.ListServers("kr-1256")[2].Targets[0].Online);

        Assert.True(directory.Observe("b", new(b.Token, b.Epoch, 2, false, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", [new("kr-1256", true, 1)], [])));
        Assert.Equal(2, directory.ListServers("kr-1256")[0].Targets.Count);
    }

    [Fact]
    public void AWrongDatabaseCannotBecomeEligibleWhenItIsTheOnlyOnlineNode()
    {
        var (directory, _) = Create(Node("a", "formal", []));
        var a = Grant(directory, "a");
        Assert.True(directory.Observe("a", new(a.Token, a.Epoch, 1, false,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", [new("kr-1256", true, 1)], [])));
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        Assert.False(Assert.Single(directory.ListServers("kr-1256")[1].Targets).Online);
    }

    [Fact]
    public void IndependentNodesMayReportWithoutAnIdentityButSharedNodesMayNot()
    {
        var (directory, _) = Create(Node("a", "formal", []), Node("b", "", []));
        var a = Grant(directory, "a");
        var b = Grant(directory, "b");
        Assert.False(directory.Observe("a", new(a.Token, a.Epoch, 1, false, null, [new("kr-1256", true, 1)], [])));
        Assert.True(directory.Observe("b", new(b.Token, b.Epoch, 1, false, null, [new("kr-1256", true, 1)], [])));
        Assert.True(directory.ListServers("kr-1256")[2].Targets[0].Online);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("a label is not a database identity")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public void AutomaticSelectorsRequireAnExplicitAuthoritativeDatabaseIdentity(string identity)
    {
        var options = Options(Node("a", "formal", []));
        options.Selectors[0].PersistentDomainId = identity;
        Assert.Contains(options.Validate(), error => error.ErrorMessage!.Contains("persistent_domain_id", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticPoolsCannotDisagreeAboutTheSameDataDomain()
    {
        var options = Options(Node("a", "formal", []));
        options.Selectors = [options.Selectors[0], new()
        {
            Id = 3, Name = "Other automatic", Automatic = true, Nodes = ["a"],
            PersistentDomainId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        }];
        Assert.NotEmpty(options.Validate());
        options.Selectors[1].PersistentDomainId = DataDomainId.ToUpperInvariant();
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options);
        Report(directory, "a");
        Assert.All(directory.ListServers("kr-1256"), selector => Assert.Single(selector.Targets));
    }

    [Fact]
    public void ExpiryHidesRoomsAndRemovesAutomaticCandidatesButRetainsManualEntries()
    {
        var (directory, time) = Create(Node("a", "formal", ["rooms"]));
        Report(directory, "a", rooms: [Room("a")]);
        Assert.Single(directory.ListServers("kr-1256")[0].Targets);
        time.Advance(16);
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        Assert.False(Assert.Single(directory.ListServers("kr-1256")[1].Targets).Online);
        Assert.Empty(directory.ListRooms("a", "kr-1256"));
    }

    [Fact]
    public void DrainingFullAndUnreadyNodesCannotReceiveAutomaticPlacements()
    {
        var (directory, _) = Create(Node("a", "formal", []));
        var grant = Grant(directory, "a");
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 1, true, DataDomainId, [new("kr-1256", true, 1)], [])));
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 2, false, DataDomainId, [new("kr-1256", true, 8)], [])));
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 3, false, DataDomainId, [new("kr-1256", false, 0)], [])));
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
    }

    [Fact]
    public void ReplacementLeaseFencesLateHeartbeatAndRelease()
    {
        var (directory, time) = Create(Node("a", "formal", []));
        var instance = Guid.NewGuid().ToString("N");
        var old = Assert.IsType<LeaseGrant>(directory.Register("a", instance));
        Assert.Null(directory.Register("a", Guid.NewGuid().ToString("N")));
        time.Advance(16);
        var fresh = Grant(directory, "a");
        Assert.True(fresh.Epoch > old.Epoch);
        Assert.NotEqual(old.Token, fresh.Token);
        Assert.False(directory.Observe("a", new(old.Token, old.Epoch, 100, false, DataDomainId, [], [])));
        Assert.False(directory.Release("a", old.Token));
        Assert.True(directory.Observe("a", new(fresh.Token, fresh.Epoch, 1, false, DataDomainId, [new("kr-1256", true, 2)], [])));
        Assert.False(directory.Observe("a", new(fresh.Token, fresh.Epoch, 1, false, DataDomainId, [new("kr-1256", true, 0)], [])));
        Assert.Equal(2, directory.ListServers("kr-1256")[0].Targets.Single().Players);
    }

    [Fact]
    public void RegistrationRetriesCannotKeepADeadNodeAlive()
    {
        var (directory, time) = Create(Node("a", "formal", []));
        var instance = Guid.NewGuid().ToString("N");
        var initial = Assert.IsType<LeaseGrant>(directory.Register("a", instance));
        time.Advance(10);
        Assert.Equal(initial, directory.Register("a", instance));
        time.Advance(6);
        Assert.NotEqual(initial.Token, Assert.IsType<LeaseGrant>(directory.Register("a", instance)).Token);
    }

    [Fact]
    public void UnknownBuildOversizeAndForeignRoomMetadataAreNotTrusted()
    {
        var (directory, _) = Create(Node("a", "formal", ["rooms"]));
        var grant = Grant(directory, "a");
        Assert.False(directory.Observe("unknown", new(grant.Token, grant.Epoch, 1, false, DataDomainId, [], [])));
        Assert.False(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, DataDomainId, [new("kr-999", true, 0)], [])));
        Assert.False(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, DataDomainId, [], [Room("a") with { Title = new string('x', 257) }])));
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, DataDomainId, [new("kr-1256", true, 0)], [Room("forged")])));
        var room = Assert.Single(directory.ListRooms("a", "kr-1256"));
        Assert.Equal("a", room.NodeId);
        Assert.Equal(grant.Epoch, room.Epoch);
        Assert.All(directory.ListServers("kr-999"), selector => Assert.Empty(selector.Targets));
    }

    internal static DirectoryNodeOptions Node(string id, string data, string[] groups) => new()
    {
        Id = id, Name = id, DataScope = data, RoomDirectories = groups, HeartbeatKey = new string('x', 32) + id,
        Endpoints = [new() { Build = "kr-1256", Host = id + ".example", Port = 7100, Capacity = 8 }],
    };

    internal static DirectoryOptions Options(params DirectoryNodeOptions[] nodes) => new()
    {
        Nodes = nodes,
        Selectors = [new() { Id = 1, Name = "Automatic", Automatic = true, PersistentDomainId = DataDomainId, Nodes = [nodes[0].Id] },
            .. nodes.Select((node, index) => new SelectorOptions { Id = index + 2, Name = node.Name, Nodes = [node.Id] })],
    };

    private static (ServerDirectory Directory, Clock Time) Create(params DirectoryNodeOptions[] nodes)
    {
        var options = Options(nodes);
        Assert.Empty(options.Validate());
        var time = new Clock();
        return (new(options, time), time);
    }
    private const string DataDomainId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static LeaseGrant Grant(ServerDirectory directory, string node) => Assert.IsType<LeaseGrant>(directory.Register(node, Guid.NewGuid().ToString("N")));
    private static void Report(ServerDirectory directory, string node, FederatedRoom[]? rooms = null)
    {
        var grant = Grant(directory, node);
        Assert.True(directory.Observe(node, new(grant.Token, grant.Epoch, 1, false, DataDomainId, [new("kr-1256", true, 1)], rooms ?? [])));
    }
    private static FederatedRoom Room(string node) => new(node, 999, "kr-1256", 1, "Open room", "normal", null, "Waiting", 1, 8, false, true);
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private int _utcShift;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks).AddSeconds(_utcShift);
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
        internal void ShiftUtc(int seconds) => _utcShift += seconds;
    }
}
