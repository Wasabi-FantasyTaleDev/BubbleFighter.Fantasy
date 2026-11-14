using System.Collections.Concurrent;
using System.Net;
using System.IO.Pipelines;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Companion.Tests;

public sealed class CompanionStateFeedTests
{
    private const string Type = CompanionRoomCreatePlugin.ContextType;

    [Fact]
    public async Task RoomCreationIsActiveOnlyInTheMenuAndPlazaAndEachChangeAdvancesTheRevision()
    {
        var hub = new FakeHub();
        var feed = Feed(hub);
        var session = new FeedSession(1);
        var player = Login(session, feed);

        feed.Mark(session, resend: true);
        await feed.FlushAsync(default);
        var first = Assert.Single(hub.Sent);
        Assert.Equal(1, first.Revision);
        Assert.False(first.Active);

        player.MoveTo(SessionPhase.MainMenu);
        await feed.FlushAsync(default);
        Assert.Equal((2, true), (hub.Sent[^1].Revision, hub.Sent[^1].Active));

        // A refused create or join changes no scene, and a repeated signal for the same state sends nothing.
        feed.Mark(session, resend: false);
        await feed.FlushAsync(default);
        Assert.Equal(2, hub.Sent.Count);

        player.MoveTo(SessionPhase.Room);
        await feed.FlushAsync(default);
        Assert.Equal((3, false), (hub.Sent[^1].Revision, hub.Sent[^1].Active));

        // A round and its return to the same room stay inactive.
        player.MoveTo(SessionPhase.Match);
        await feed.FlushAsync(default);
        player.MoveTo(SessionPhase.Room);
        await feed.FlushAsync(default);
        Assert.Equal(3, hub.Sent.Count);

        player.MoveTo(SessionPhase.Plaza);
        await feed.FlushAsync(default);
        Assert.Equal((4, true), (hub.Sent[^1].Revision, hub.Sent[^1].Active));
        Assert.All(hub.Sent, sent => Assert.Equal(first.ContextId, sent.ContextId));
    }

    [Fact]
    public async Task AReconnectResendsTheCurrentStateWithItsRevisionAndTheSnapshotMatchesIt()
    {
        var hub = new FakeHub();
        var feed = Feed(hub);
        var session = new FeedSession(1);
        var player = Login(session, feed);
        player.MoveTo(SessionPhase.MainMenu);
        await feed.FlushAsync(default);
        var sent = Assert.Single(hub.Sent);

        feed.Mark(session, resend: true);
        await feed.FlushAsync(default);
        Assert.Equal(sent, hub.Sent[^1]);
        Assert.Equal(sent, await feed.SnapshotAsync(session, default));
    }

    [Fact]
    public async Task AFullQueueKeepsTheLatestStateUntilItCanBeSent()
    {
        var hub = new FakeHub();
        var feed = Feed(hub);
        var session = new FeedSession(1);
        var player = Login(session, feed);
        player.MoveTo(SessionPhase.MainMenu);
        await feed.FlushAsync(default);

        hub.Busy = true;
        player.MoveTo(SessionPhase.Room);
        await feed.FlushAsync(default);
        await feed.FlushAsync(default);
        Assert.Single(hub.Sent);

        hub.Busy = false;
        await feed.FlushAsync(default);
        Assert.Equal((2, false), (hub.Sent[^1].Revision, hub.Sent[^1].Active));
    }

    [Fact]
    public async Task ARetryingWorkerDeliversTheInactiveStateOnItsOwn()
    {
        var hub = new FakeHub();
        var feed = Feed(hub, retry: TimeSpan.FromMilliseconds(20));
        var session = new FeedSession(1);
        var player = Login(session, feed);
        using var stop = new CancellationTokenSource();
        var worker = feed.RunAsync(stop.Token).AsTask();
        try
        {
            player.MoveTo(SessionPhase.MainMenu);
            await hub.WaitForAsync(1);
            hub.Busy = true;
            player.MoveTo(SessionPhase.Room);
            await Task.Delay(100);
            hub.Busy = false;
            await hub.WaitForAsync(2);
            Assert.False(hub.Sent[^1].Active);
        }
        finally
        {
            await stop.CancelAsync();
            await worker;
        }
    }

    [Fact]
    public async Task ALauncherWithoutTheCapabilityGetsNothingAndLeavesNoState()
    {
        var hub = new FakeHub { Supported = false };
        var feed = Feed(hub);
        var session = new FeedSession(1);
        var player = Login(session, feed);
        player.MoveTo(SessionPhase.MainMenu);
        feed.Mark(session, resend: true);
        await feed.FlushAsync(default);
        Assert.Empty(hub.Sent);
        Assert.Equal(0, feed.Retained);
    }

    [Fact]
    public async Task ANewLoginGetsANewContextAndAClosedConnectionIsForgotten()
    {
        var hub = new FakeHub();
        var feed = Feed(hub);
        var old = new FeedSession(1);
        Login(old, feed).MoveTo(SessionPhase.MainMenu);
        await feed.FlushAsync(default);
        var before = Assert.Single(hub.Sent);

        old.Close();
        feed.Forget(old.Connection);
        Assert.Equal(0, feed.Retained);

        var replacement = new FeedSession(2);
        Login(replacement, feed).MoveTo(SessionPhase.MainMenu);
        await feed.FlushAsync(default);
        Assert.NotEqual(before.ContextId, hub.Sent[^1].ContextId);
        Assert.Equal(1, hub.Sent[^1].Revision);
    }

    [Fact]
    public async Task AClosedConnectionIsNeitherReadNorSent()
    {
        var hub = new FakeHub();
        var feed = Feed(hub);
        var session = new FeedSession(1);
        var player = Login(session, feed);
        player.MoveTo(SessionPhase.MainMenu);
        session.Close();
        await feed.FlushAsync(default);
        Assert.Empty(hub.Sent);
        Assert.Equal(0, feed.Retained);
    }

    private static CompanionStateFeed<bool, RoomCreateContext> Feed(FakeHub hub, TimeSpan? retry = null) =>
        new(hub, Type, 1, CompanionRoomCreatePlugin.Available, RoomCreateContext.Next, retry ?? TimeSpan.FromSeconds(1), NullLogger.Instance);

    // The phase hook marks the session the way the lobby row's event does in a running server.
    private static PlayerSession Login(FeedSession session, CompanionStateFeed<bool, RoomCreateContext> feed)
    {
        var player = new PlayerSession(new(11, "alice", "Alice", 1), session.Connection.Id, () => feed.Mark(session, resend: false));
        session.RegisterFeature<IPlayerSession>(player);
        return player;
    }

    private sealed class FakeHub : ICompanionHub
    {
        private readonly Lock _gate = new();
        private readonly List<RoomCreateContext> _sent = [];
        public bool Supported { get; set; } = true;
        public bool Busy { get; set; }

        public List<RoomCreateContext> Sent { get { lock (_gate) return [.. _sent]; } }

        public IDisposable Route(string type, int version, CompanionHandler handler) => new Nothing();
        public IDisposable Offer(string type, int version) => new Nothing();
        public bool Supports(ISession session, string type, int version) => Supported && type == Type && version == 1;
        public IReadOnlyList<ISession> BoundSessions() => [];

        public bool Send(ISession session, string type, int version, object? payload)
        {
            if (!Supports(session, type, version) || Busy) return false;
            lock (_gate) _sent.Add(Assert.IsType<RoomCreateContext>(payload));
            return true;
        }

        public async Task WaitForAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Sent.Count < count) await Task.Delay(5, timeout.Token);
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private sealed class FeedSession(long id) : ISessionWithFeatures
    {
        private readonly FeedConnection _connection = new(id);
        public ISessionFeatures Features { get; } = new BubbleFighter.Fantasy.Tests.Shared.TestSessionFeatures();
        public long Id => id;
        public IConnection Connection => _connection;
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "kr-812");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
        public void Close() => _connection.Closed = true;
    }

    private sealed class FeedConnection(long id) : IConnection
    {
        public long Id => id;
        public bool Closed { get; set; }
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => Closed;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}

public sealed partial class CompanionLoopbackTests
{
    [Fact]
    public async Task TheRoomCreationContextReachesANegotiatedLauncherOnBindAndOnRequest()
    {
        using var offer = _hub.Offer(CompanionRoomCreatePlugin.ContextType, 1);
        var feed = new CompanionStateFeed<bool, RoomCreateContext>(_hub, CompanionRoomCreatePlugin.ContextType, 1,
            CompanionRoomCreatePlugin.Available, RoomCreateContext.Next, TimeSpan.FromSeconds(1), NullLogger.Instance);
        using var route = _hub.Route(CompanionRoomCreatePlugin.SnapshotType, 1,
            async (caller, _, ct) => await feed.SnapshotAsync(caller.Session, ct));
        _launches.Connected = session => feed.Mark(session, resend: true);
        var session = new Session(9, "kr-812");
        var player = new PlayerSession(new(11, "alice", "Alice", 1));
        session.RegisterFeature<IPlayerSession>(player);
        player.MoveTo(SessionPhase.MainMenu);

        var grant = _launches.Open("alice", "kr-812");
        await using var client = await Client.ConnectAsync(_endpoint, grant.Ticket);
        var epoch = (await client.HelloAsync("kr-812", grant.LaunchId, new()
        {
            [CompanionRoomCreatePlugin.ContextType] = 1, [CompanionRoomCreatePlugin.SnapshotType] = 1,
        })).GetProperty("epoch").GetString();
        _launches.Bind(new LauncherLaunchRedeemed(grant.LaunchId, "alice", "kr-812", session));
        Assert.Equal("state", (await client.ReceiveAsync()).GetProperty("kind").GetString());
        await feed.FlushAsync(default);

        var pushed = await client.ReceiveAsync();
        Assert.Equal("event", pushed.GetProperty("kind").GetString());
        Assert.Equal(CompanionRoomCreatePlugin.ContextType, pushed.GetProperty("type").GetString());
        var payload = pushed.GetProperty("payload");
        Assert.Equal(["active", "context_id", "revision"], payload.EnumerateObject().Select(field => field.Name).Order());
        Assert.True(payload.GetProperty("active").GetBoolean());
        Assert.Equal(1, payload.GetProperty("revision").GetInt64());

        player.MoveTo(SessionPhase.Room);
        await client.SendAsync(new { kind = "request", id = "ctx-1", type = CompanionRoomCreatePlugin.SnapshotType, version = 1, epoch, payload = new { } });
        var reply = (await client.ReceiveAsync()).GetProperty("payload");
        Assert.False(reply.GetProperty("active").GetBoolean());
        Assert.Equal(2, reply.GetProperty("revision").GetInt64());
        Assert.Equal(payload.GetProperty("context_id").GetString(), reply.GetProperty("context_id").GetString());
        Assert.Equal(1, feed.Retained);
    }
}
