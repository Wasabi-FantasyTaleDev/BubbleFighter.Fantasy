using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Cluster;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class RoomPreparationPermitTests
{
    [Fact]
    public void PermitPinsTheAuthenticatedNodesAccountBuildRoomCreationAndUnresolvedEndpoint()
    {
        var rig = new Rig();
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        Assert.Equal(64, permit.Token.Length);
        var claim = Assert.IsType<RoomPreparationClaim>(rig.Directory.ClaimRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token)));
        Assert.Equal("a", claim.SourceNodeId);
        Assert.Equal("b", claim.DestinationNodeId);
        Assert.Equal(rig.Leases["a"].Epoch, claim.SourceEpoch);
        Assert.Equal(rig.Leases["b"].Epoch, claim.DestinationEpoch);
        Assert.Equal("kr-1256", claim.Build);
        Assert.Equal(7, claim.RoomId);
        Assert.Equal(rig.Room.InstanceId, claim.InstanceId);
        Assert.Equal(23, claim.UserNumber);
        Assert.Equal(Rig.Domain, claim.PersistentDomainId);
        Assert.Equal("b.example", claim.DestinationHost);
        Assert.Equal(7100, claim.DestinationPort);
        Assert.Null(rig.Directory.ClaimRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token)));
        Assert.False(Assert.Single(rig.Directory.ListRooms("a", "kr-1256")).JoinAllowed);
    }

    [Theory]
    [InlineData("source-lease")]
    [InlineData("source-epoch")]
    [InlineData("target-epoch")]
    [InlineData("creation")]
    [InlineData("room")]
    [InlineData("build")]
    [InlineData("user")]
    [InlineData("self")]
    public void ForgedPreparationInputsCannotIssueAPermit(string scenario)
    {
        var rig = new Rig();
        var request = scenario switch
        {
            "source-lease" => rig.Request with { SourceLeaseToken = "wrong" },
            "source-epoch" => rig.Request with { SourceEpoch = 99 },
            "target-epoch" => rig.Request with { DestinationEpoch = 99 },
            "creation" => rig.Request with { InstanceId = Guid.NewGuid() },
            "room" => rig.Request with { RoomId = 8 },
            "build" => rig.Request with { Build = "kr-812" },
            "user" => rig.Request with { UserNumber = 0 },
            _ => rig.Request with { DestinationNodeId = "a" },
        };
        Assert.Null(rig.Directory.IssueRoomPreparation("a", request));
        Assert.NotNull(rig.Directory.IssueRoomPreparation("a", rig.Request));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("join")]
    [InlineData("visibility")]
    [InlineData("authority")]
    [InlineData("independent-authority")]
    [InlineData("missing-authority")]
    [InlineData("database")]
    [InlineData("source-database")]
    [InlineData("draining")]
    [InlineData("not-ready")]
    [InlineData("node-full")]
    [InlineData("room-full")]
    [InlineData("playing")]
    public void SharingVisibilityNeverSubstitutesForLiveRoutingAndPersistentAuthority(string scenario)
    {
        var rig = new Rig();
        if (scenario == "data") rig.Options.Nodes[1].DataScope = "independent";
        if (scenario == "join") rig.Options.Nodes[1].JoinScope = "";
        if (scenario == "visibility") rig.Options.Nodes[1].RoomDirectories = [];
        if (scenario == "authority") rig.Options.Nodes[1].Authentication!.AuthorityId = "other";
        if (scenario == "independent-authority") rig.Options.Nodes[1].Authentication!.Mode = "own-authority";
        if (scenario == "missing-authority") rig.Options.Nodes[1].Authentication = null;
        if (scenario == "database") rig.Report("b", domain: new string('b', 32));
        if (scenario == "source-database") rig.Report("a", domain: new string('b', 32));
        if (scenario == "draining") rig.Report("b", draining: true);
        if (scenario == "not-ready") rig.Report("b", ready: false);
        if (scenario == "node-full") rig.Report("b", players: 8);
        if (scenario == "room-full") rig.Report("b", room: rig.Room with { Players = 8, Listing = rig.Room.Listing! with { Occupants = 8 } });
        if (scenario == "playing") rig.Report("b", room: rig.Room with { Phase = "Playing", Listing = rig.Room.Listing! with { MatchInProgress = true } });
        Assert.Null(rig.Directory.IssueRoomPreparation("a", rig.Request));
    }

    [Theory]
    [InlineData("source-released")]
    [InlineData("destination-restarted")]
    [InlineData("creation-reused")]
    [InlineData("database-changed")]
    [InlineData("visibility-revoked")]
    public void ClaimRevalidatesAuthorityInsteadOfTrustingEarlierDirectorySuccess(string scenario)
    {
        var rig = new Rig();
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        var targetLease = rig.Leases["b"];
        if (scenario == "source-released") Assert.True(rig.Directory.Release("a", rig.Leases["a"].Token));
        if (scenario == "destination-restarted")
        {
            Assert.True(rig.Directory.Release("b", targetLease.Token));
            targetLease = Assert.IsType<LeaseGrant>(rig.Directory.Register("b", Guid.NewGuid().ToString("N")));
        }
        if (scenario == "creation-reused") rig.Report("b", room: rig.Room with { InstanceId = Guid.NewGuid() });
        if (scenario == "database-changed") rig.Report("b", domain: new string('b', 32));
        if (scenario == "visibility-revoked") rig.Options.Nodes[1].RoomDirectories = [];
        Assert.Null(rig.Directory.ClaimRoomPreparation("b", new(targetLease.Token, permit.Token)));
    }

    [Fact]
    public void WrongNodeOrProcessCannotClaimOrCancelAnotherNodesPermit()
    {
        var rig = new Rig();
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        Assert.Null(rig.Directory.ClaimRoomPreparation("a", new(rig.Leases["a"].Token, permit.Token)));
        Assert.Null(rig.Directory.ClaimRoomPreparation("b", new("wrong", permit.Token)));
        Assert.False(rig.Directory.CancelRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token)));
        Assert.False(rig.Directory.CancelRoomPreparation("a", new("wrong", permit.Token)));
        Assert.NotNull(rig.Directory.ClaimRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token)));
    }

    [Theory]
    [InlineData(-86400)]
    [InlineData(86400)]
    public void CapacityIsBoundedAndMonotonicExpiryDoesNotFollowWallClockChanges(int utcShift)
    {
        var rig = new Rig();
        rig.Options.MaxRoomPreparationPermits = 1;
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        rig.Time.ShiftUtc(utcShift);
        rig.Time.Advance(999);
        Assert.Null(rig.Directory.IssueRoomPreparation("a", rig.Request));
        rig.Time.Advance(1);
        Assert.NotNull(rig.Directory.IssueRoomPreparation("a", rig.Request));
        Assert.Null(rig.Directory.ClaimRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token)));
    }

    [Fact]
    public void SourceCancellationReleasesBudgetAndPreventsReplay()
    {
        var rig = new Rig();
        rig.Options.MaxRoomPreparationPermits = 1;
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        Assert.True(rig.Directory.CancelRoomPreparation("a", new(rig.Leases["a"].Token, permit.Token)));
        Assert.False(rig.Directory.CancelRoomPreparation("a", new(rig.Leases["a"].Token, permit.Token)));
        Assert.Null(rig.Directory.ClaimRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token)));
        Assert.NotNull(rig.Directory.IssueRoomPreparation("a", rig.Request));
    }

    [Fact]
    public async Task ConcurrentClaimsHaveExactlyOneWinner()
    {
        var rig = new Rig();
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => rig.Directory.ClaimRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token))));
        Assert.Single(await Task.WhenAll(attempts), result => result is not null);
    }

    [Fact]
    public void CredentialsAreAbsentFromRecordDiagnostics()
    {
        var rig = new Rig();
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        Assert.DoesNotContain(rig.Leases["a"].Token, rig.Request.ToString());
        Assert.DoesNotContain(permit.Token, permit.ToString());
        Assert.DoesNotContain(permit.Token, new RoomPreparationRedemption(rig.Leases["b"].Token, permit.Token).ToString());
    }

    [Fact]
    public async Task RealHttpAuthenticatesBothRolesAndRequiresTheCurrentDestinationLease()
    {
        var rig = new Rig();
        await using var server = await DirectoryServer.StartAsync(rig.Options, rig.Directory, default);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        using var anonymous = await http.PostAsJsonAsync("/v1/nodes/a/room-preparations", rig.Request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", rig.Options.Nodes[0].HeartbeatKey);
        using var malformed = await http.PostAsync("/v1/nodes/a/room-preparations", new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var issued = await http.PostAsJsonAsync("/v1/nodes/a/room-preparations", rig.Request);
        issued.EnsureSuccessStatusCode();
        var permit = Assert.IsType<RoomPreparationPermit>(await issued.Content.ReadFromJsonAsync<RoomPreparationPermit>());
        var redemption = new RoomPreparationRedemption(rig.Leases["b"].Token, permit.Token);
        using var wrongNodeKey = await http.PostAsJsonAsync("/v1/nodes/b/room-preparations/claim", redemption);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongNodeKey.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", rig.Options.Nodes[1].HeartbeatKey);
        using var wrongProcess = await http.PostAsJsonAsync("/v1/nodes/b/room-preparations/claim", redemption with { LeaseToken = "wrong" });
        Assert.Equal(HttpStatusCode.Conflict, wrongProcess.StatusCode);
        using var accepted = await http.PostAsJsonAsync("/v1/nodes/b/room-preparations/claim", redemption);
        accepted.EnsureSuccessStatusCode();
        Assert.Equal(23, (await accepted.Content.ReadFromJsonAsync<RoomPreparationClaim>())!.UserNumber);
        using var replay = await http.PostAsJsonAsync("/v1/nodes/b/room-preparations/claim", redemption);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task RealHttpCancellationRequiresTheSourceKeyAndCurrentProcessLease()
    {
        var rig = new Rig();
        await using var server = await DirectoryServer.StartAsync(rig.Options, rig.Directory, default);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        var permit = Assert.IsType<RoomPreparationPermit>(rig.Directory.IssueRoomPreparation("a", rig.Request));
        async Task<HttpStatusCode> Cancel(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/nodes/a/room-preparations")
            { Content = JsonContent.Create(new RoomPreparationRedemption(rig.Leases["a"].Token, permit.Token)) };
            request.Headers.Authorization = new("Bearer", key);
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Cancel(rig.Options.Nodes[1].HeartbeatKey));
        Assert.Equal(HttpStatusCode.NoContent, await Cancel(rig.Options.Nodes[0].HeartbeatKey));
        Assert.Equal(HttpStatusCode.Conflict, await Cancel(rig.Options.Nodes[0].HeartbeatKey));
        Assert.Null(rig.Directory.ClaimRoomPreparation("b", new(rig.Leases["b"].Token, permit.Token)));
    }

    [Fact]
    public async Task DisabledDirectoryDoesNotPublishPreparationRoutes()
    {
        var rig = new Rig();
        rig.Options.RoomPreparationPermits = false;
        Assert.Null(rig.Directory.IssueRoomPreparation("a", rig.Request));
        await using var server = await DirectoryServer.StartAsync(rig.Options, rig.Directory, default);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rig.Options.Nodes[0].HeartbeatKey);
        using var response = await http.PostAsJsonAsync("/v1/nodes/a/room-preparations", rig.Request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class Rig
    {
        internal const string Domain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal DirectoryOptions Options { get; }
        internal ServerDirectory Directory { get; }
        internal Clock Time { get; } = new();
        internal Dictionary<string, LeaseGrant> Leases { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
        internal FederatedRoom Room { get; } = new("b", 1, "kr-1256", 7, "Shared room", "4", null, "Waiting", 1, 8, true, false)
        { InstanceId = Guid.NewGuid(), Listing = new(7, "Shared room", "Host", 4, 1, 8, true) };
        internal RoomPreparationRequest Request => new(Leases["a"].Token, Leases["a"].Epoch, "b", Leases["b"].Epoch,
            "kr-1256", Room.RoomId, Room.InstanceId, 23);

        internal Rig()
        {
            Options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "shared", ["rooms"]),
                ServerDirectoryTests.Node("b", "shared", ["rooms"]));
            Options.Listen = "127.0.0.1:0";
            Options.RoomPreparationPermits = true;
            Options.RoomPreparationPermitLifetimeMs = 1000;
            foreach (var node in Options.Nodes)
            {
                node.JoinScope = "shared-rooms";
                node.Authentication = new() { AuthorityId = "authority", ApiUrl = "https://authority.example", Mode = "master-issued" };
            }
            Assert.Empty(Options.Validate());
            Directory = new(Options, Time);
            foreach (var node in Options.Nodes)
            {
                Leases.Add(node.Id, Assert.IsType<LeaseGrant>(Directory.Register(node.Id, Guid.NewGuid().ToString("N"))));
                _sequences.Add(node.Id, 0);
                Report(node.Id);
            }
        }

        internal void Report(string node, string domain = Domain, bool draining = false, bool ready = true, int players = 1, FederatedRoom? room = null)
        {
            var lease = Leases[node];
            Assert.True(Directory.Observe(node, new(lease.Token, lease.Epoch, ++_sequences[node], draining, domain,
                [new("kr-1256", ready, players)], node == "b" ? [room ?? Room] : [])));
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        private int _utcShift;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_timestamp).AddSeconds(_utcShift);
        internal void Advance(int milliseconds) => _timestamp += milliseconds;
        internal void ShiftUtc(int seconds) => _utcShift += seconds;
    }
}
