using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class MigrationBrokerTests
{
    [Theory]
    [InlineData("enabled", true)]
    [InlineData("directory-disabled", false)]
    [InlineData("source-disabled", false)]
    [InlineData("destination-disabled", false)]
    [InlineData("independent", false)]
    [InlineData("lease-lost", false)]
    [InlineData("scope", false)]
    [InlineData("capacity", false)]
    [InlineData("issuer", false)]
    [InlineData("domain", false)]
    [InlineData("draining", false)]
    public async Task RemoteJoinHintsRequireExplicitBuildSupportAndCurrentSharedAccountRouting(string state, bool allowed)
    {
        await using var rig = await Rig.StartAsync(independent: state == "independent", enabled: state != "directory-disabled");
        if (state == "source-disabled") rig.Options.Nodes[0].NativeMigrationBuilds = [];
        if (state == "destination-disabled") rig.Options.Nodes[1].NativeMigrationBuilds = [];
        if (state == "lease-lost") Assert.True(rig.Directory.Release("a", rig.Leases["a"].Current!.Token));
        if (state == "scope") rig.Options.Nodes[1].JoinScope = "other";
        if (state == "capacity") rig.Options.Nodes[1].Endpoints[0].Capacity = 1;
        if (state == "issuer") rig.Options.Nodes[1].Authentication!.AuthorityId = "other";
        if (state == "domain") rig.Report("b", new string('b', 32));
        if (state == "draining")
        {
            var lease = rig.Leases["b"].Current!;
            var room = Assert.Single(rig.Directory.ListRooms("b", "kr-1256"));
            Assert.True(rig.Directory.Observe("b", new(lease.Token, lease.Epoch, 2, true, new string('a', 32),
                [new("kr-1256", true, 1)], [room])));
        }
        Assert.Equal(allowed, Assert.Single(rig.Directory.ListRooms("a", "kr-1256")).JoinAllowed);
        if (!allowed && state is "source-disabled" or "destination-disabled")
            await StatusAsync(HttpStatusCode.Conflict, () => rig.A.RequestAsync(rig.Request, default).AsTask());
    }

    [Fact]
    public async Task MigrationBuildDeclarationsMustNameDistinctExistingListeners()
    {
        await using var rig = await Rig.StartAsync();
        rig.Options.Nodes[0].NativeMigrationBuilds = ["missing"];
        Assert.Contains(rig.Options.Validate(), error => error.ErrorMessage!.Contains("native_migration_builds", StringComparison.Ordinal));
        rig.Options.Nodes[0].NativeMigrationBuilds = ["kr-1256", "kr-1256"];
        Assert.Contains(rig.Options.Validate(), error => error.ErrorMessage!.Contains("native_migration_builds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HomeTargetLookupUsesTheCurrentLeaseAndDoesNotCreateAnArrival()
    {
        await using var rig = await Rig.StartAsync();
        var first = await rig.A.ResolveAsync("b", "kr-1256", default);
        Assert.Equal(new MigrationTarget("b", rig.Leases["b"].Current!.Epoch), first);
        Assert.True(rig.Directory.Release("b", rig.Leases["b"].Current!.Token));
        rig.Leases["b"].Publish(rig.Directory.Register("b", Guid.NewGuid().ToString("N")));
        Assert.Null(await rig.A.ResolveAsync("b", "kr-1256", default));
        rig.Report("b");
        var second = await rig.A.ResolveAsync("b", "kr-1256", default);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Epoch, second.Epoch);
        Assert.Null(await rig.A.ResolveAsync("a", "kr-1256", default));
        Assert.Null(await rig.A.ResolveAsync("b", "kr-812", default));
    }

    [Fact]
    public async Task IndependentDomainsCannotResolveSharedAccountHomeTargets()
    {
        await using var rig = await Rig.StartAsync(independent: true);
        Assert.Null(await rig.A.ResolveAsync("b", "kr-1256", default));
    }

    [Fact]
    public async Task HomeTargetLookupRequiresTheSourceNodeKeyAndItsCurrentLease()
    {
        await using var rig = await Rig.StartAsync();
        using var noKey = new HttpClient { BaseAddress = new(rig.Address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await noKey.GetAsync("/v1/nodes/a/migrations/targets/b/kr-1256")).StatusCode);
        using var oldLease = rig.AuthenticatedClient("a");
        Assert.True(rig.Directory.Release("a", rig.Leases["a"].Current!.Token));
        rig.Leases["a"].Publish(rig.Directory.Register("a", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Conflict, (await oldLease.GetAsync("/v1/nodes/a/migrations/targets/b/kr-1256")).StatusCode);
    }

    [Fact]
    public async Task HttpFailureRacingWithShutdownDoesNotFaultTheOwnedReceiverTask()
    {
        using var stop = new CancellationTokenSource();
        var lease = new NodeLeaseSource("a", "https://directory.example");
        lease.Publish(new("lease", 1, 30));
        using var broker = new MigrationBroker(new() { NodeId = "a", AccessKey = new string('k', 32) }, lease,
            new HttpClient(new ShutdownFailure(stop)) { BaseAddress = new("https://directory.example") }, NullLogger.Instance);
        using var receiver = broker.Serve((_, _) => ValueTask.FromResult(Accepted()), (_, _) => ValueTask.CompletedTask);
        await broker.RunAsync(stop.Token);
    }

    private sealed class ShutdownFailure(CancellationTokenSource stop) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            stop.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task DisposedReceiverCanBeReplacedWithoutRestartingTheBroker()
    {
        await using var rig = await Rig.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = rig.B.Serve(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Accepted();
        }, (_, _) => { cleaned.TrySetResult(); return ValueTask.CompletedTask; });
        using var requestStop = new CancellationTokenSource();
        var abandoned = rig.A.RequestAsync(rig.Request, requestStop.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receiver.Dispose();
        await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var replacement = rig.B.Serve((_, _) => ValueTask.FromResult(Accepted()), (_, _) => ValueTask.CompletedTask);
        Assert.True((await rig.A.RequestAsync(rig.Request with { MigrationId = new string('b', 32) }, default)).Accepted);
        await requestStop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        receiver.Dispose();
        Assert.True((await rig.A.RequestAsync(rig.Request with { MigrationId = new string('c', 32) }, default)).Accepted);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("kind")]
    [InlineData("address")]
    [InlineData("room-scene")]
    [InlineData("home-scene")]
    public async Task MalformedAuthenticatedRequestsAreRejectedBeforeDelivery(string malformed)
    {
        await using var rig = await Rig.StartAsync();
        using var http = rig.AuthenticatedClient("a");
        var json = JsonSerializer.Serialize(rig.Request, MigrationTransport.Json);
        json = malformed switch
        {
            "null" => "null",
            "missing" => "{}",
            "duplicate" => json.Insert(1, "\"source_node\":\"a\","),
            "unknown" => json.Insert(1, "\"unexpected\":true,"),
            "kind" => json.Replace("\"kind\":0", "\"kind\":99", StringComparison.Ordinal),
            "address" => json.Replace("203.0.113.7", "::1", StringComparison.Ordinal),
            "room-scene" => JsonSerializer.Serialize(rig.Request with { HomeScene = SessionPhase.Plaza }, MigrationTransport.Json),
            "home-scene" => JsonSerializer.Serialize(rig.Request with
                { Kind = MigrationKind.Home, Room = null, HomeScene = SessionPhase.Room }, MigrationTransport.Json),
            _ => throw new InvalidOperationException(),
        };
        using var response = await http.PostAsync("/v1/nodes/a/migrations", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealDirectoryRoutesOffersAndCancellationBetweenLeasedBrokers(bool refuse)
    {
        await using var rig = await Rig.StartAsync();
        var expected = refuse ? new MigrationOffer(null, 0, RoomJoinRefusal.WrongPassword, TimeSpan.Zero) : Accepted();
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        MigrationRequest? received = null;
        using var receiver = rig.B.Serve((request, _) => { received = request; return ValueTask.FromResult(expected); },
            (id, _) => { cancelled.TrySetResult(id); return ValueTask.CompletedTask; });
        var offered = await rig.A.RequestAsync(rig.Request, default);
        Assert.Equal(expected, offered);
        Assert.Equal(rig.Request, received);
        Assert.DoesNotContain("private-password", received!.ToString(), StringComparison.Ordinal);
        await rig.A.CancelAsync("b", rig.Request.MigrationId, default);
        Assert.Equal(rig.Request.MigrationId, await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await rig.A.CancelAsync("b", rig.Request.MigrationId, default);
        Assert.Throws<InvalidOperationException>(() => rig.B.Serve((_, _) => ValueTask.FromResult(Accepted()), (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task DirectoryTimeoutReturns504AndDeliversCancellationWhenTheReceiverReturns()
    {
        await using var rig = await Rig.StartAsync(timeout: 500);
        await StatusAsync(HttpStatusCode.GatewayTimeout, () => rig.A.RequestAsync(rig.Request, default).AsTask());
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = rig.B.Serve((_, _) => throw new InvalidOperationException("An expired request must not be accepted."),
            (_, _) => { cancelled.TrySetResult(); return ValueTask.CompletedTask; });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PendingRequestsReserveCapacityAndDuplicateIdsCannotReplaceThem()
    {
        await using var rig = await Rig.StartAsync(capacity: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = rig.B.Serve(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Accepted();
        }, (_, _) => ValueTask.CompletedTask);
        var first = rig.A.RequestAsync(rig.Request, default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await StatusAsync(HttpStatusCode.Conflict, () => rig.A.RequestAsync(rig.Request, default).AsTask());
            await StatusAsync(HttpStatusCode.ServiceUnavailable, () => rig.A.RequestAsync(rig.Request with
            { MigrationId = new string('b', 32), UserNum = 8, Room = rig.Request.Room! with { UserNumber = 8 } }, default).AsTask());
        }
        finally { release.TrySetResult(); }
        Assert.True((await first).Accepted);
    }

    [Fact]
    public async Task AReplacedTargetLeaseCannotAcceptAnOldRoomSelection()
    {
        await using var rig = await Rig.StartAsync();
        Assert.True(rig.Directory.Release("b", rig.Leases["b"].Current!.Token));
        rig.Leases["b"].Publish(rig.Directory.Register("b", Guid.NewGuid().ToString("N")));
        rig.Report("b");
        await StatusAsync(HttpStatusCode.Conflict, () => rig.A.RequestAsync(rig.Request with { DestinationEpoch = 1 }, default).AsTask());
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task GuestTrustRequiresDifferentDataScopesAndSharedAccountsRequireTheSameDatabase(bool independent, bool guest, bool allowed)
    {
        await using var rig = await Rig.StartAsync(independent: independent);
        using var receiver = rig.B.Serve((_, _) => ValueTask.FromResult(Accepted()), (_, _) => ValueTask.CompletedTask);
        var request = rig.Request with { Guest = guest ? new("Visitor", "Origin", 1, [100], 0) : null };
        if (allowed) Assert.True((await rig.A.RequestAsync(request, default)).Accepted);
        else await StatusAsync(HttpStatusCode.Conflict, () => rig.A.RequestAsync(request, default).AsTask());
    }

    [Theory]
    [InlineData("join")]
    [InlineData("visibility")]
    [InlineData("database")]
    public async Task AVisibleRoomDoesNotGrantMigrationWithoutItsLiveTrustChecks(string revoked)
    {
        await using var rig = await Rig.StartAsync();
        if (revoked == "join") rig.Options.Nodes[1].JoinScope = "";
        if (revoked == "visibility") rig.Options.Nodes[1].RoomDirectories = [];
        if (revoked == "database") rig.Report("b", new string('b', 32));
        await StatusAsync(HttpStatusCode.Conflict, () => rig.A.RequestAsync(rig.Request, default).AsTask());
    }

    [Fact]
    public async Task NativeSocketBindingCannotBeSubmittedWithOnlyTheNodeKeyOrAnotherSourcesIdentity()
    {
        await using var rig = await Rig.StartAsync();
        using var http = new HttpClient { BaseAddress = new(rig.Address) };
        using (var response = await http.PostAsJsonAsync("/v1/nodes/a/migrations", rig.Request, MigrationTransport.Json))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rig.Options.Nodes[0].HeartbeatKey);
        using (var response = await http.PostAsJsonAsync("/v1/nodes/a/migrations", rig.Request, MigrationTransport.Json))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        http.DefaultRequestHeaders.Add(MigrationTransport.LeaseHeader, rig.Leases["a"].Current!.Token);
        http.DefaultRequestHeaders.Add(MigrationTransport.EpochHeader, "1");
        using (var response = await http.PostAsJsonAsync("/v1/nodes/a/migrations", rig.Request with { SourceNode = "c" }, MigrationTransport.Json))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CallbackFailureRefusesAndCleansUpWithoutExposingThePassword()
    {
        await using var rig = await Rig.StartAsync();
        var cancelled = 0;
        using var receiver = rig.B.Serve((_, _) => throw new IOException("callback failure"),
            (_, _) => { Interlocked.Increment(ref cancelled); return ValueTask.CompletedTask; });
        Assert.False((await rig.A.RequestAsync(rig.Request, default)).Accepted);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public async Task AcceptedRequestsExpireMonotonicallyAndReleaseTheReservedInboxCapacity()
    {
        var clock = new Clock();
        await using var rig = await Rig.StartAsync(capacity: 1, clock: clock);
        using var receiver = rig.B.Serve((_, _) => ValueTask.FromResult(Accepted()), (_, _) => ValueTask.CompletedTask);
        Assert.True((await rig.A.RequestAsync(rig.Request, default)).Accepted);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await rig.A.RequestAsync(rig.Request with { MigrationId = new string('b', 32) }, default)).Accepted);
    }

    [Theory]
    [InlineData(SessionPhase.MainMenu)]
    [InlineData(SessionPhase.Plaza)]
    public async Task HomeRequestsUseSharedAuthorityWithoutRequiringARoom(SessionPhase scene)
    {
        await using var rig = await Rig.StartAsync();
        using var receiver = rig.B.Serve((request, _) =>
        {
            Assert.Equal(MigrationKind.Home, request.Kind);
            Assert.Null(request.Room);
            Assert.Equal(scene, request.HomeScene);
            return ValueTask.FromResult(Accepted());
        }, (_, _) => ValueTask.CompletedTask);
        Assert.True((await rig.A.RequestAsync(rig.Request with { Kind = MigrationKind.Home, Room = null, HomeScene = scene }, default)).Accepted);
    }

    [Fact]
    public async Task DisabledDirectoryHasNoMigrationRoutes()
    {
        await using var rig = await Rig.StartAsync(enabled: false);
        await StatusAsync(HttpStatusCode.NotFound, () => rig.A.RequestAsync(rig.Request, default).AsTask());
    }

    private static MigrationOffer Accepted() => new(IPAddress.Loopback, 7100, null, TimeSpan.FromSeconds(10));
    private static async Task StatusAsync(HttpStatusCode status, Func<Task<MigrationOffer>> action)
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(action);
        Assert.Equal(status, error.StatusCode);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<string, long> _sequence = [];
        private DirectoryServer _server = null!;
        private Task[] _workers = [];
        internal DirectoryOptions Options { get; private set; } = null!;
        internal ServerDirectory Directory { get; private set; } = null!;
        internal Dictionary<string, NodeLeaseSource> Leases { get; } = [];
        internal MigrationBroker A { get; private set; } = null!;
        internal MigrationBroker B { get; private set; } = null!;
        internal string Address => _server.Addresses.Single();
        private readonly FederatedRoom _room = new("b", 1, "kr-1256", 7, "Room", "4", null, "Waiting", 1, 8, true, false)
        { InstanceId = Guid.NewGuid(), Listing = new(7, "Room", "Host", 4, 1, 8, true) };
        internal MigrationRequest Request => new(new string('a', 32), MigrationKind.Room, "a", "b", Leases["b"].Current!.Epoch,
            "kr-1256", 7, IPAddress.Parse("203.0.113.7"), new(7, _room.InstanceId, 7, "private-password"))
        { SourceChannel = 10, HomeNode = "a" };

        internal static async Task<Rig> StartAsync(int timeout = 5000, int capacity = 8, bool independent = false, bool enabled = true,
            TimeProvider? clock = null)
        {
            var rig = new Rig();
            rig.Options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "shared", ["rooms"]),
                ServerDirectoryTests.Node("b", independent ? "other" : "shared", ["rooms"]));
            rig.Options.Listen = "127.0.0.1:0";
            rig.Options.LeaseSeconds = 300;
            rig.Options.Migrations = enabled;
            rig.Options.MaxPendingMigrations = capacity;
            rig.Options.MigrationRequestTimeoutMs = timeout;
            foreach (var node in rig.Options.Nodes)
            {
                node.JoinScope = "friends";
                node.NativeMigrationBuilds = ["kr-1256"];
                node.Authentication = new() { AuthorityId = "authority", ApiUrl = "https://authority.example", Mode = "master-issued" };
            }
            Assert.Empty(rig.Options.Validate());
            rig.Directory = new(rig.Options, clock);
            rig._server = await DirectoryServer.StartAsync(rig.Options, rig.Directory, default);
            foreach (var node in rig.Options.Nodes)
            {
                var lease = new NodeLeaseSource(node.Id, rig.Address);
                lease.Publish(rig.Directory.Register(node.Id, Guid.NewGuid().ToString("N")));
                rig.Leases.Add(node.Id, lease);
                rig.Report(node.Id, independent && node.Id == "b" ? new string('b', 32) : new string('a', 32));
            }
            rig.A = rig.Broker("a"); rig.B = rig.Broker("b");
            rig._workers = [rig.A.RunAsync(rig._stop.Token).AsTask(), rig.B.RunAsync(rig._stop.Token).AsTask()];
            return rig;
        }

        internal void Report(string node, string domain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        {
            var lease = Leases[node].Current!;
            _sequence[node] = _sequence.GetValueOrDefault(node) + 1;
            Assert.True(Directory.Observe(node, new(lease.Token, lease.Epoch, _sequence[node], false, domain,
                [new("kr-1256", true, 1)], node == "b" ? [_room] : [])));
        }

        internal HttpClient AuthenticatedClient(string node)
        {
            var http = new HttpClient { BaseAddress = new(Address) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", Options.Nodes.Single(n => n.Id == node).HeartbeatKey);
            http.DefaultRequestHeaders.Add(MigrationTransport.LeaseHeader, Leases[node].Current!.Token);
            http.DefaultRequestHeaders.Add(MigrationTransport.EpochHeader, Leases[node].Current!.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return http;
        }

        private MigrationBroker Broker(string node) => new(new MigrationBrokerOptions
        {
            NodeId = node, DirectoryUrl = Address, AccessKey = Options.Nodes.Single(n => n.Id == node).HeartbeatKey,
            PollWaitSeconds = 1, RetryDelayMs = 100,
        }, Leases[node], new HttpClient { BaseAddress = new(Address), Timeout = Timeout.InfiniteTimeSpan }, NullLogger.Instance);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(10));
            A.Dispose(); B.Dispose();
            await _server.DisposeAsync();
            _stop.Dispose();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
