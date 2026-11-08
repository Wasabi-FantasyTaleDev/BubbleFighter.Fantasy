using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class RoomBrowserTests
{
    [Fact]
    public async Task RealAuthenticatedDirectoryProjectsFriendlyRoomsAndRevokesTheirAliasesOnLeaseRelease()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", ["rooms"]),
            ServerDirectoryTests.Node("b", "independent", ["rooms"]), ServerDirectoryTests.Node("hidden", "other", []));
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        var grants = new Dictionary<string, LeaseGrant>();
        foreach (var node in options.Nodes)
        {
            var grant = Assert.IsType<LeaseGrant>(directory.Register(node.Id, Guid.NewGuid().ToString("N")));
            grants.Add(node.Id, grant);
            Assert.True(directory.Observe(node.Id, new(grant.Token, grant.Epoch, 1, false,
                new string(node.Id == "a" ? 'a' : 'b', 32), [new("kr-1256", true, 1)],
                [Remote() with { NodeId = node.Id }])));
        }
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var clientOptions = new RoomDirectoryClientOptions
        {
            NodeId = "a", DirectoryUrl = server.Addresses.Single(), AccessKey = options.Nodes[0].HeartbeatKey,
            Builds = ["kr-1256"],
        };
        using var client = new RoomDirectoryClient(clientOptions, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            { BaseAddress = new(clientOptions.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan });
        using var browser = new RoomBrowser(Options(), client);
        var session = new Session(1);
        var rows = await browser.ListAsync(session, [Local()], default);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Local", rows[0].Name);
        var selection = Assert.IsType<RoomBrowserSelection>(await browser.ResolveAsync(session, rows[1].RoomId, default));
        Assert.Equal("b", selection.NodeId);
        Assert.False(selection.Local);
        Assert.False(selection.JoinAllowed);
        Assert.True(directory.Release("b", grants["b"].Token));
        Assert.Null(await browser.ResolveAsync(session, rows[1].RoomId, default));
        Assert.Single(await browser.ListAsync(session, [Local()], default));
    }

    [Fact]
    public async Task PluginEffectsRemoveTheBrowserAndCancelBorrowedServiceOperationsOnUnload()
    {
        var services = new ServiceRegistry();
        using var discovery = services.Register<IFederatedRoomDiscovery>(new Discovery());
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow
        {
            Id = "browser", Plugin = "cluster.room-browser", Source = "test",
            Config = new Dictionary<string, object?> { ["node_id"] = "a", ["client_build"] = "kr-1256" },
        }, services, events, NullLogger.Instance);
        IRoomBrowser? borrowed = null;
        try
        {
            await new RoomBrowserPlugin().ActivateAsync(context, default);
            borrowed = Assert.Single(services.GetAll<IRoomBrowser>());
            Assert.Single(await borrowed.ListAsync(new Session(1), [Local()], default));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(services.GetAll<IRoomBrowser>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => borrowed!.ListAsync(new Session(2), [], default).AsTask());
    }

    [Theory]
    [InlineData("max_aliases_per_connection", 0)]
    [InlineData("max_aliases_per_connection", 32768)]
    public void BrowserBudgetsAreRejectedBeforeActivation(string field, int value)
    {
        var config = new PluginConfig(new PluginRow
        {
            Id = "browser", Plugin = "cluster.room-browser", Source = "test",
            Config = new Dictionary<string, object?>
                { ["node_id"] = "a", ["client_build"] = "kr-1256", [field] = value },
        });
        Assert.Throws<ConfigException>(() => config.Bind<RoomBrowserOptions>());
    }

    [Fact]
    public async Task ADisappearedRoomCannotBecomeClickableAgainWithoutANewList()
    {
        var remote = Remote();
        var discovery = new Discovery { Rooms = [remote] };
        using var browser = new RoomBrowser(Options(), discovery);
        var session = new Session(1);
        var row = Assert.Single(await browser.ListAsync(session, [], default));
        discovery.Rooms = [];
        Assert.Null(await browser.ResolveAsync(session, row.RoomId, default));
        discovery.Rooms = [remote];
        Assert.Null(await browser.ResolveAsync(session, row.RoomId, default));
        Assert.Equal(row.RoomId, Assert.Single(await browser.ListAsync(session, [], default)).RoomId);
        Assert.NotNull(await browser.ResolveAsync(session, row.RoomId, default));
    }

    [Fact]
    public async Task LocalRegistryOverridesItsHeartbeatAndCollidingRemoteIdsStayDistinct()
    {
        var local = Local();
        var remote = Remote();
        var discovery = new Discovery { Rooms = [remote, remote with { NodeId = "a" }] };
        using var browser = new RoomBrowser(Options(), discovery);
        var session = new Session(1);
        var rows = await browser.ListAsync(session, [local], default);
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].RoomId, rows[1].RoomId);
        Assert.Equal("Local", rows[0].Name);
        Assert.Equal(new RoomBrowserSelection(true, "a", 0, 1, local.InstanceId, true),
            await browser.ResolveAsync(session, rows[0].RoomId, default));
        Assert.Equal(new RoomBrowserSelection(false, "b", 1, 1, remote.InstanceId, false),
            await browser.ResolveAsync(session, rows[1].RoomId, default));
        Assert.Equal(2, discovery.Calls);
    }

    [Fact]
    public async Task InformationalDirectorySummariesDoNotPreventBrowsingNativeRows()
    {
        var remote = Remote();
        using var browser = new RoomBrowser(Options(), new Discovery
            { Rooms = [remote, remote with { NodeId = "c", InstanceId = Guid.Empty, Listing = null }] });
        var row = Assert.Single(await browser.ListAsync(new Session(1), [], default));
        Assert.Equal("Remote", row.Name);
    }

    [Fact]
    public async Task ClosedConnectionsRetainTheirBudgetUntilTheirPendingOperationActuallyEnds()
    {
        var options = Options();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Discovery { BeforeRead = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); } };
        using var browser = new RoomBrowser(options, discovery);
        var first = new Session(1);
        var pending = browser.ListAsync(first, [], default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        browser.OnClosed(new(first.Connection, "closed"));
        Assert.Equal(1, browser.RetainedConnections);
        release.SetResult();
        await Assert.ThrowsAsync<RoomBrowserUnavailableException>(() => pending);
        Assert.Equal(0, browser.RetainedConnections);
        discovery.BeforeRead = null;
        Assert.Empty(await browser.ListAsync(new Session(2), [], default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteClickRechecksTheExactIncarnationAndEpoch(bool changeEpoch)
    {
        var remote = Remote();
        var discovery = new Discovery { Rooms = [remote] };
        using var browser = new RoomBrowser(Options(), discovery);
        var session = new Session(1);
        var row = Assert.Single(await browser.ListAsync(session, [], default));
        discovery.Rooms = [changeEpoch ? remote with { Epoch = 2 } : remote with { InstanceId = Guid.NewGuid() }];
        Assert.Null(await browser.ResolveAsync(session, row.RoomId, default));
        var replacement = Assert.Single(await browser.ListAsync(session, [], default));
        Assert.NotEqual(row.RoomId, replacement.RoomId);
        Assert.Null(await browser.ResolveAsync(session, row.RoomId, default));
    }

    [Fact]
    public async Task DirectoryFailureListsOnlyLocalRoomsAndRevokesRemoteAliasesWithoutRecyclingThem()
    {
        var discovery = new Discovery { Rooms = [Remote()] };
        using var browser = new RoomBrowser(Options(), discovery);
        var session = new Session(1);
        var row = Assert.Single(await browser.ListAsync(session, [], default));
        discovery.Failure = new HttpRequestException("unavailable");
        var local = Local();
        var only = Assert.Single(await browser.ListAsync(session, [local], default));
        Assert.Equal("Local", only.Name);
        Assert.NotEqual(row.RoomId, only.RoomId);
        Assert.Null(await browser.ResolveAsync(session, row.RoomId, default));
        Assert.Equal(new RoomBrowserSelection(true, "a", 0, 1, local.InstanceId, true),
            await browser.ResolveAsync(session, only.RoomId, default));
        discovery.Failure = null;
        Assert.Equal(row.RoomId, Assert.Single(await browser.ListAsync(session, [], default)).RoomId);
    }

    [Fact]
    public async Task ClosingOneConnectionReleasesItsBudgetWithoutTouchingAnotherConnectionWithTheSameNumericId()
    {
        using var browser = new RoomBrowser(Options(), new Discovery());
        var first = new Session(1);
        var replacement = new Session(1);
        await browser.ListAsync(first, [Local()], default);
        await browser.ListAsync(replacement, [], default);
        Assert.Equal(2, browser.RetainedConnections);
        browser.OnClosed(new(first.Connection, "closed"));
        Assert.Equal(1, browser.RetainedConnections);
        var row = Assert.Single(await browser.ListAsync(replacement, [Local()], default));
        browser.OnClosed(new(first.Connection, "duplicate close"));
        Assert.NotNull(await browser.ResolveAsync(replacement, row.RoomId, default));
    }

    [Fact]
    public async Task AbortedOrFailedListSendsCanInvalidateTheVisibleViewWithoutRecyclingItsHistory()
    {
        using var browser = new RoomBrowser(Options(), new Discovery());
        var session = new Session(1);
        var local = Local();
        var row = Assert.Single(await browser.ListAsync(session, [local], default));
        browser.Invalidate(session);
        Assert.Null(await browser.ResolveAsync(session, row.RoomId, default));
        Assert.Equal(row.RoomId, Assert.Single(await browser.ListAsync(session, [local], default)).RoomId);
    }

    [Fact]
    public async Task ConcurrentRequestsAreRefusedAndCannotPublishALateList()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Discovery { BeforeRead = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); } };
        using var browser = new RoomBrowser(Options(), discovery);
        var session = new Session(1);
        var pending = browser.ListAsync(session, [Local()], default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<RoomBrowserUnavailableException>(() => browser.ListAsync(session, [], default).AsTask());
        release.SetResult();
        await Assert.ThrowsAsync<RoomBrowserUnavailableException>(() => pending);
        discovery.BeforeRead = null;
        Assert.Null(await browser.ResolveAsync(session, 1, default));
        Assert.Single(await browser.ListAsync(session, [Local()], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseOrSceneChangesDuringFetchCannotInstallAliases(bool changeScene)
    {
        var session = new Session(1);
        using var browser = new RoomBrowser(Options(), new Discovery
        {
            BeforeRead = _ =>
            {
                if (changeScene) session.Player.MoveTo(SessionPhase.Plaza);
                else session.Connection.CloseAsync("ended").GetAwaiter().GetResult();
                return ValueTask.CompletedTask;
            },
        });
        await Assert.ThrowsAsync<RoomBrowserUnavailableException>(() => browser.ListAsync(session, [Local()], default).AsTask());
    }

    [Fact]
    public async Task CancellationClearsOldVisibilityAndDisposalCancelsAnActiveFetch()
    {
        var discovery = new Discovery();
        var browser = new RoomBrowser(Options(), discovery);
        var session = new Session(1);
        var row = Assert.Single(await browser.ListAsync(session, [Local()], default));
        using var cancel = new CancellationTokenSource();
        discovery.BeforeRead = ct => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ListAsync(session, [], cancel.Token).AsTask());
        Assert.Null(await browser.ResolveAsync(session, row.RoomId, default));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.BeforeRead = async ct => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        var pending = browser.ListAsync(session, [], default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        browser.Dispose();
        await Assert.ThrowsAsync<RoomBrowserUnavailableException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => browser.ListAsync(session, [], default).AsTask());
    }

    [Fact]
    public async Task SharedPlayerStateDoesNotAuthorizeAMatchConnection()
    {
        using var browser = new RoomBrowser(Options(), new Discovery());
        var session = new Session(1, ownerId: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.ListAsync(session, [], default).AsTask());
    }

    private static RoomBrowserOptions Options() => new() { NodeId = "a", ClientBuild = "kr-1256", MaxAliasesPerConnection = 8 };
    private static PublishedRoomSnapshot Local() => new(Guid.NewGuid(), new(1, "Local", "Host", 3, 1, 8, false), "map", "Waiting");
    private static FederatedRoom Remote() => new("b", 1, "kr-1256", 1, "Remote", "3", "map", "Waiting", 1, 8, false, false)
        { InstanceId = Guid.NewGuid(), Listing = new(1, "Remote", "Host", 3, 1, 8, false) };

    private sealed class Discovery : IFederatedRoomDiscovery
    {
        internal IReadOnlyList<FederatedRoom> Rooms { get; set; } = [];
        internal Exception? Failure { get; set; }
        internal Func<CancellationToken, ValueTask>? BeforeRead { get; set; }
        internal int Calls { get; private set; }
        public async ValueTask<IReadOnlyList<FederatedRoom>> ListAsync(string clientBuild, CancellationToken cancellationToken)
        {
            Calls++;
            if (BeforeRead is { } before) await before(cancellationToken);
            if (Failure is { } failure) throw failure;
            return Rooms;
        }
    }

    private sealed class Session : ISessionWithFeatures
    {
        internal PlayerSession Player { get; }
        internal Session(long id, long? ownerId = null)
        {
            Id = id; Connection = new Connection(id);
            Player = new(new(7, "user", "Player", 3), ownerId ?? id);
            Player.MoveTo(SessionPhase.MainMenu);
            Features.Register<IPlayerSession>(Player);
        }
        public long Id { get; }
        public IConnection Connection { get; }
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "test");
        public ISessionFeatures Features { get; } = new SessionFeatures(4);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
