using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class RoomMigrationEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABlockedHomeNoticeFinishesBeforeTheRealSourceCanBeReleased(bool cancelCaller)
    {
        using var endpoint = new RoomMigrationEndpoint("kr-1256");
        var session = new RecordingSession(11);
        var player = new PlayerSession(new(11, "host", "Host", 1), session.Connection.Id);
        player.SetMigrationHome(new("home", 50));
        player.MoveTo(SessionPhase.MainMenu);
        using var playerFeature = session.RegisterFeature<IPlayerSession>(player);
        var notices = new BlockedNotice();
        await using var source = new MigrationSource(new()
        {
            ClientBuild = "kr-1256", Channel = 50, OperationTimeoutMs = cancelCaller ? 30000 : 1000,
        }, null!, new Leases(), null!, new Online(session), null, null!, null, TimeProvider.System,
            NullLogger.Instance, new UnavailableHome(), notices);
        var binding = endpoint.Bind(source);
        using var caller = new CancellationTokenSource();
        var call = endpoint.ReturnHomeAsync(session, caller.Token).AsTask();
        try
        {
            await notices.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var draining = binding.DisposeAsync().AsTask();
            Assert.False(draining.IsCompleted);
            if (cancelCaller) caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            await draining.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(notices.Exited.Task.IsCompleted);
            Assert.Equal(cancelCaller, caller.IsCancellationRequested);
            Assert.False(session.Connection.IsClosed);
        }
        finally
        {
            caller.Cancel();
            try { await call; } catch (OperationCanceledException) { }
            await binding.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachmentRefusesNewCallsAndDrainsTheCurrentCallBeforeReplacement(bool home)
    {
        using var endpoint = new RoomMigrationEndpoint("kr-1256");
        var source = new Source();
        var binding = endpoint.Bind(source);
        var session = RoomFlowTests.Rig.Player(11, "host");
        var selection = new RoomBrowserSelection(false, "b", 1, 4, Guid.NewGuid(), true);
        var call = home ? endpoint.ReturnHomeAsync(session, default).AsTask()
            : endpoint.BeginAsync(session, selection, "password", default).AsTask();
        Assert.Equal(1, source.Calls);
        var draining = binding.DisposeAsync().AsTask();
        Assert.False(draining.IsCompleted);
        Assert.Equal(RoomJoinRefusal.NoRoom, await endpoint.BeginAsync(session, selection, "password", default));
        await endpoint.ReturnHomeAsync(session, default);
        Assert.Equal(1, source.Calls);
        Assert.Throws<InvalidOperationException>(() => endpoint.Bind(new Source()));
        source.Release.SetResult();
        await call;
        await draining;
        var replacement = endpoint.Bind(new Source());
        await binding.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => endpoint.Bind(new Source()));
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task FailedCallsDrainAndNeverLeaveTheSourceAttached()
    {
        using var endpoint = new RoomMigrationEndpoint("kr-1256");
        var source = new Source();
        var binding = endpoint.Bind(source);
        var call = endpoint.ReturnHomeAsync(RoomFlowTests.Rig.Player(11, "host"), default).AsTask();
        var draining = binding.DisposeAsync().AsTask();
        source.Release.SetException(new IOException("source failed"));
        await Assert.ThrowsAsync<IOException>(() => call);
        await draining;
        await using var replacement = endpoint.Bind(new Source());
    }

    [Fact]
    public async Task DifferentBuildDuplicateAndPrematureOwnerDisposalAreRejected()
    {
        using var endpoint = new RoomMigrationEndpoint("kr-1256");
        Assert.Throws<InvalidOperationException>(() => endpoint.Bind(new Source("kr-812")));
        var binding = endpoint.Bind(new Source());
        Assert.Throws<InvalidOperationException>(() => endpoint.Bind(new Source()));
        Assert.Throws<InvalidOperationException>(endpoint.Dispose);
        await binding.DisposeAsync();
        endpoint.Dispose();
        Assert.Throws<ObjectDisposedException>(() => endpoint.Bind(new Source()));
    }

    [Fact]
    public async Task CallerCancellationDoesNotConsumeOrRetainABinding()
    {
        using var endpoint = new RoomMigrationEndpoint("kr-1256");
        var source = new Source();
        await using var binding = endpoint.Bind(source);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            endpoint.ReturnHomeAsync(RoomFlowTests.Rig.Player(11, "host"), cancelled.Token).AsTask());
        Assert.Equal(0, source.Calls);
    }

    private sealed class Source(string build = "kr-1256") : IRemoteRoomMigration
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public string ClientBuild => build;
        public async ValueTask<RoomJoinRefusal?> BeginAsync(ISession session, RoomBrowserSelection selection, string password, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Release.Task.WaitAsync(cancellationToken);
            return null;
        }
        public async ValueTask ReturnHomeAsync(ISession session, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockedNotice : IMessageSender<ServerNotice>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Protocol => "kr-1256/game";
        public async ValueTask SendAsync(ISession session, ServerNotice message, CancellationToken ct)
        {
            Entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Exited.SetResult(); }
        }
    }

    private sealed class Leases : INodeLeaseSource
    {
        public string NodeId => "a";
        public string DirectoryUrl => "http://directory.example";
        public NodeLeaseCredentials? Current => null;
    }

    private sealed class Online(ISession session) : IOnlinePlayers
    {
        public ISession? FindByUserNum(int userNum) => session;
        public ISession? FindByNickname(string nickname) => session;
    }

    private sealed class UnavailableHome : IMigrationTargetDirectory
    {
        public ValueTask<MigrationTarget?> ResolveAsync(string nodeId, string clientBuild, CancellationToken cancellationToken) =>
            ValueTask.FromResult<MigrationTarget?>(null);
    }
}
