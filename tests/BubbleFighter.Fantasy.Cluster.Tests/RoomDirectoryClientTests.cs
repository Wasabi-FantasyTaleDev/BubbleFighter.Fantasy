using System.Net;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class RoomDirectoryClientTests
{
    [Theory]
    [InlineData("http://directory.example/path")]
    [InlineData("http://directory.example?token=secret")]
    [InlineData("http://user:password@directory.example")]
    [InlineData("http://directory.example:0")]
    [InlineData("file:///tmp/directory")]
    public void ConfigurationRefusesNonOriginDirectoryAddresses(string address)
    {
        var options = Options();
        options.DirectoryUrl = address;
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void ConfigurationRequiresExplicitNodeCredentialsAndDistinctBuilds()
    {
        var options = Options();
        Assert.Empty(options.Validate());
        options.AccessKey = "";
        Assert.NotEmpty(options.Validate());
        options = Options();
        options.Builds = ["kr-1256", "kr-1256"];
        Assert.NotEmpty(options.Validate());
        options = Options();
        options.NodeId = "../other";
        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("under_score")]
    [InlineData("dot.name")]
    public void NodeIdentityUsesTheSameAlphabetAsTheDirectory(string nodeId)
    {
        var options = Options();
        options.NodeId = nodeId;
        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData("max_rooms", 0)]
    [InlineData("max_rooms", 65537)]
    [InlineData("max_response_bytes", 1023)]
    [InlineData("max_response_bytes", 4194305)]
    [InlineData("max_room_settings_bytes", 0)]
    [InlineData("max_room_settings_bytes", 1048577)]
    [InlineData("max_concurrent_requests", 0)]
    [InlineData("max_concurrent_requests", 33)]
    [InlineData("request_timeout_seconds", 0)]
    [InlineData("request_timeout_seconds", 31)]
    public void DeploymentBudgetsAreValidatedBeforeActivation(string field, int value)
    {
        var config = new PluginConfig(new PluginRow
        {
            Id = "rooms", Plugin = "cluster.room-directory", Source = "test",
            Config = new Dictionary<string, object?>
            {
                ["node_id"] = "a", ["directory_url"] = "http://directory.example",
                ["access_key"] = new string('k', 32), ["builds"] = new[] { "kr-1256" },
                [field] = value,
            },
        });
        Assert.Throws<ConfigException>(() => config.Bind<RoomDirectoryClientOptions>());
    }

    [Fact]
    public async Task AnAlreadyCancelledCallerDoesNotSpendARequestSlot()
    {
        var calls = 0;
        using var client = Client(Options(), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Json([]));
        });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListAsync("kr-1256", cancel.Token).AsTask());
        Assert.Equal(0, calls);
        Assert.Empty(await client.ListAsync("kr-1256", default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FetchUsesTheRequestingNodesCredentialAndDoesNotCacheAFormerList()
    {
        var calls = 0;
        using var client = Client(Options(), (request, _) =>
        {
            Assert.Equal("/v1/nodes/a/rooms/kr-1256", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(new string('k', 32), request.Headers.Authorization.Parameter);
            return Task.FromResult(++calls == 1 ? Json([Room()]) : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        Assert.Single(await client.ListAsync("kr-1256", default));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("kr-1256", default).AsTask());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AnUnconfiguredBuildIsRefusedBeforeAnyHttpRequest()
    {
        using var client = Client(Options(), (_, _) => throw new InvalidOperationException("Unexpected network request."));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync("cn-324", default).AsTask());
    }

    [Theory]
    [InlineData("{\"schema\":1}")]
    [InlineData("{\"schema\":1,\"schema\":1,\"rooms\":[]}")]
    [InlineData("{\"schema\":1,\"rooms\":[],\"extra\":true}")]
    [InlineData("{\"schema\":1,\"rooms\":[{}]}")]
    public async Task MalformedResponsesAreNotAcceptedAsAnEmptyDirectory(string json)
    {
        using var client = Client(Options(), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAsync<JsonException>(() => client.ListAsync("kr-1256", default).AsTask());
    }

    [Fact]
    public async Task ResponseBoundsSchemaAndMediaTypeAreEnforced()
    {
        var options = Options();
        options.MaxRooms = 1;
        using (var client = Client(options, (_, _) => Task.FromResult(Json([Room(), Room() with { RoomId = 2 }]))))
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync("kr-1256", default).AsTask());
        options = Options();
        options.MaxResponseBytes = 1024;
        using (var client = Client(options, (_, _) => Task.FromResult(Json([Room() with { Title = new string('x', 2048) }]))))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("kr-1256", default).AsTask());
        using (var client = Client(Options(), (_, _) => Task.FromResult(Json([], schema: 2))))
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync("kr-1256", default).AsTask());
        using (var client = Client(Options(), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{}") })))
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync("kr-1256", default).AsTask());
    }

    [Fact]
    public async Task AResponseWithoutAContentLengthStillUsesTheByteBudget()
    {
        var options = Options();
        options.MaxResponseBytes = 1024;
        using var client = Client(options, (_, _) =>
        {
            var content = new UnmeasuredContent(Encoding.UTF8.GetBytes(new string('x', 2048)));
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("kr-1256", default).AsTask());
    }

    [Fact]
    public async Task ForeignBuildDuplicateIdentityAndInvalidRoomValuesAreRefused()
    {
        FederatedRoom[][] cases =
        [
            [Room() with { ClientBuild = "cn-324" }],
            [Room(), Room() with { Epoch = 2 }],
            [Room() with { NodeId = "../foreign" }],
            [Room() with { NodeId = "Upper" }],
            [Room() with { NodeId = "dot.name" }],
            [Room() with { Epoch = 0 }],
            [Room() with { Players = 9 }],
            [Room() with { Title = "broken\nname" }],
            [Room() with { Mode = null! }],
        ];
        foreach (var rooms in cases)
        {
            using var client = Client(Options(), (_, _) => Task.FromResult(Json(rooms)));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync("kr-1256", default).AsTask());
        }
    }

    [Fact]
    public async Task AFullRequestBudgetRejectsRatherThanQueuesAndCancellationReturnsTheSlot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var options = Options();
        options.MaxConcurrentRequests = 1;
        options.RequestTimeoutSeconds = 30;
        using var client = Client(options, async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return Json([]);
        });
        using var cancel = new CancellationTokenSource();
        var pending = client.ListAsync("kr-1256", cancel.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListAsync("kr-1256", default).AsTask());
        Assert.Equal(1, calls);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(await client.ListAsync("kr-1256", default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisposalCancelsTheOwnedHttpRequestAndRefusesNewCalls()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options();
        options.RequestTimeoutSeconds = 30;
        using var client = Client(options, async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json([]);
        });
        var pending = client.ListAsync("kr-1256", default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListAsync("kr-1256", default).AsTask());
    }

    [Fact]
    public async Task TheFetchDeadlineCancelsTransportAndReturnsItsSlot()
    {
        var options = Options();
        options.RequestTimeoutSeconds = 1;
        options.MaxConcurrentRequests = 1;
        var calls = 0;
        using var client = Client(options, async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json([]);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListAsync("kr-1256", default).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(await client.ListAsync("kr-1256", default));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterHeadersStopsBodyBufferingAndReturnsTheSlot(bool useDeadline)
    {
        var options = Options();
        options.MaxConcurrentRequests = 1;
        options.RequestTimeoutSeconds = useDeadline ? 1 : 30;
        using var body = new PendingContent();
        var calls = 0;
        using var client = Client(options, (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = body }
            : Json([])));
        using var cancel = new CancellationTokenSource();
        var pending = client.ListAsync("kr-1256", cancel.Token).AsTask();
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!useDeadline) cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(body.CancellationObserved);
        Assert.True(body.Disposed);
        Assert.Empty(await client.ListAsync("kr-1256", default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RealDirectorySharesFriendlyRoomsWithoutSharingDataAndRemovesExpiredRooms()
    {
        var options = ServerDirectoryTests.Options(
            ServerDirectoryTests.Node("a", "formal", ["visible"]),
            ServerDirectoryTests.Node("friend", "independent", ["visible"]),
            ServerDirectoryTests.Node("private", "separate", []));
        options.Listen = "127.0.0.1:0";
        var clock = new Clock();
        var directory = new ServerDirectory(options, clock);
        foreach (var node in options.Nodes)
        {
            var grant = Assert.IsType<LeaseGrant>(directory.Register(node.Id, Guid.NewGuid().ToString("N")));
            Assert.True(directory.Observe(node.Id, new(grant.Token, grant.Epoch, 1, false,
                node.Id == "a" ? new string('a', 32) : new string('b', 32),
                [new("kr-1256", true, 1)], [Room() with { NodeId = node.Id }])));
        }
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var clientOptions = Options();
        clientOptions.DirectoryUrl = server.Addresses.Single();
        clientOptions.AccessKey = options.Nodes[0].HeartbeatKey;
        using var client = new RoomDirectoryClient(clientOptions, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(clientOptions.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan });
        var rooms = await client.ListAsync("kr-1256", default);
        Assert.Equal(["a", "friend"], rooms.Select(room => room.NodeId));
        Assert.True(rooms[0].JoinAllowed);
        Assert.False(rooms[1].JoinAllowed);
        var foreignOptions = Options();
        foreignOptions.NodeId = "friend";
        foreignOptions.DirectoryUrl = server.Addresses.Single();
        foreignOptions.AccessKey = clientOptions.AccessKey;
        using (var foreign = new RoomDirectoryClient(foreignOptions, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(foreignOptions.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan }))
        {
            var refusal = await Assert.ThrowsAsync<HttpRequestException>(() => foreign.ListAsync("kr-1256", default).AsTask());
            Assert.Equal(HttpStatusCode.Unauthorized, refusal.StatusCode);
        }
        clock.Advance(options.LeaseSeconds + 1);
        Assert.Empty(await client.ListAsync("kr-1256", default));
    }

    [Fact]
    public async Task NativeRoomMetadataSurvivesAuthenticatedHeartbeatAndDiscovery()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", ["shared"]));
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        var grant = Assert.IsType<LeaseGrant>(directory.Register("a", Guid.NewGuid().ToString("N")));
        var room = NativeRoom();
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, new string('a', 32),
            [new("kr-1256", true, 1)], [room])));
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var config = Options();
        config.DirectoryUrl = server.Addresses.Single();
        config.AccessKey = options.Nodes[0].HeartbeatKey;
        using var client = new RoomDirectoryClient(config, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(config.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan });
        var received = Assert.Single(await client.ListAsync("kr-1256", default));
        Assert.Equal(room.InstanceId, received.InstanceId);
        Assert.Equal(grant.Epoch, received.Epoch);
        Assert.Equal("Host", received.Listing!.MasterNickname);
        Assert.True(received.Listing.HasPassword);
        Assert.Equal(new byte[] { 1, 2 }, received.Listing.BuildSettings!.Bytes.ToArray());
        Assert.Equal(new byte[] { 3 }, received.Listing.BuildSettings.Change.ToArray());
        Assert.Equal(new byte[] { 4 }, Assert.Single(received.Listing.BuildSettings.RoundSelections).Bytes.ToArray());
        Assert.Empty(received.Listing.BuildSettings.SessionPrefix.ToArray());
    }

    [Fact]
    public void DirectoryOwnsNativeSettingsAndEachReaderGetsDetachedBuffers()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        var directory = new ServerDirectory(options);
        var grant = Assert.IsType<LeaseGrant>(directory.Register("a", Guid.NewGuid().ToString("N")));
        var bytes = new byte[] { 1, 2 };
        var change = new byte[] { 3 };
        var round = new byte[] { 4 };
        RoomRoundSelection[] selections = [new(1, round)];
        var room = NativeRoom();
        room = room with { Listing = room.Listing! with { BuildSettings = new("kr-1256", bytes, change)
            { RoundSelections = selections } } };
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, new string('a', 32),
            [new("kr-1256", true, 1)], [room])));
        bytes[0] = 10;
        change[0] = 11;
        round[0] = 12;
        selections[0] = new(2, new byte[] { 13 });
        var first = Assert.Single(directory.ListRooms("a", "kr-1256")).Listing!.BuildSettings!;
        Assert.Equal(new byte[] { 1, 2 }, first.Bytes.ToArray());
        Assert.Equal(new byte[] { 3 }, first.Change.ToArray());
        Assert.Equal(1, Assert.Single(first.RoundSelections).Index);
        Assert.Equal(new byte[] { 4 }, first.RoundSelections[0].Bytes.ToArray());
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(first.Bytes, out var exposed));
        exposed.Array![exposed.Offset] = 20;
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(first.RoundSelections[0].Bytes, out exposed));
        exposed.Array![exposed.Offset] = 21;
        var second = Assert.Single(directory.ListRooms("a", "kr-1256")).Listing!.BuildSettings!;
        Assert.Equal(new byte[] { 1, 2 }, second.Bytes.ToArray());
        Assert.Equal(new byte[] { 4 }, second.RoundSelections[0].Bytes.ToArray());
    }

    [Fact]
    public async Task InvalidNativeRoomMetadataIsRefusedByBothReportStorageAndDiscovery()
    {
        var room = NativeRoom();
        var listing = room.Listing!;
        var settings = listing.BuildSettings!;
        FederatedRoom[] invalid = [
            room with { InstanceId = Guid.Empty },
            room with { Listing = null },
            room with { RoomId = 65536, Listing = listing with { RoomId = 65536 } },
            room with { Listing = listing with { RoomId = 2 } },
            room with { Listing = listing with { Name = "different" } },
            room with { Listing = listing with { ModeId = 4 } },
            room with { Mode = "03" },
            room with { Listing = listing with { Occupants = 2 } },
            room with { Players = 0, Listing = listing with { Occupants = 0 } },
            room with { Listing = listing with { Capacity = 7 } },
            room with { Listing = listing with { HasPassword = false } },
            room with { Listing = listing with { MasterNickname = "bad\nname" } },
            room with { Listing = listing with { MatchInProgress = true } },
            room with { Phase = "Closed", Listing = listing with { MatchInProgress = true } },
            room with { Listing = listing with { BuildSettings = settings with { SessionPrefix = new byte[] { 9 } } } },
            room with { Listing = listing with { BuildSettings = settings with { Mode = -1 } } },
            room with { Listing = listing with { BuildSettings = settings with { Mode = 4 } } },
            room with { Listing = listing with { BuildSettings = settings with { RoundSelections = null! } } },
            room with { Listing = listing with { BuildSettings = settings with { RoundSelections = [new(3, new byte[] { 1 })] } } },
            room with { Listing = listing with { BuildSettings = settings with { RoundSelections = [new(1, default), new(1, default)] } } },
            room with { Listing = listing with { BuildSettings = settings with { Bytes = new byte[33] } } },
        ];
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        options.MaxRoomSettingsBytes = 32;
        var directory = new ServerDirectory(options);
        var grant = Assert.IsType<LeaseGrant>(directory.Register("a", Guid.NewGuid().ToString("N")));
        foreach (var malformed in invalid)
        {
            Assert.False(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, new string('a', 32),
                [new("kr-1256", true, 1)], [malformed])));
            var config = Options();
            config.MaxRoomSettingsBytes = 32;
            using var client = Client(config, (_, _) => Task.FromResult(Json([malformed])));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync("kr-1256", default).AsTask());
        }
        Assert.True(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, new string('a', 32),
            [new("kr-1256", true, 1)], [room])));
    }

    private static FederatedRoom NativeRoom() => Room() with
    {
        Mode = "3",
        Locked = true,
        InstanceId = Guid.NewGuid(),
        Listing = new(1, "Room", "Host", 3, 1, 8, true, new("kr-812", new byte[] { 1, 2 }, new byte[] { 3 })
        { RoundSelections = [new(1, new byte[] { 4 })] }),
    };

    private static RoomDirectoryClientOptions Options() => new()
    {
        NodeId = "a", DirectoryUrl = "http://directory.example", AccessKey = new string('k', 32), Builds = ["kr-1256"],
    };

    private static FederatedRoom Room() => new("a", 1, "kr-1256", 1, "Room", "normal", null, "Waiting", 1, 8, false, false);

    private static HttpResponseMessage Json(FederatedRoom[] rooms, int schema = 1) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { schema, rooms }, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    private static RoomDirectoryClient Client(RoomDirectoryClientOptions options,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(options,
        new HttpClient(new Handler(send)) { BaseAddress = new Uri(options.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan });

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class UnmeasuredContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class PendingContent : HttpContent
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CancellationObserved { get; private set; }
        internal bool Disposed { get; private set; }

        internal PendingContent() => Headers.ContentType = new("application/json");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Body buffering must pass a cancellation token.");

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
