using System.Buffers;
using BubbleFighter.Fantasy.RoomCore;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>Exercises opaque native admission through the same room leases and bounded lifecycle used by account-bearing joins.</summary>
public sealed class NativeMatchCapabilityTests
{
    [Theory]
    [InlineData("kr-812", 20)]
    [InlineData("cn-717", 20)]
    [InlineData("cn-324", 18)]
    [InlineData("cn-279", 12)]
    [InlineData("cn-406", 20)]
    [InlineData("id-400", 20)]
    [InlineData("id-1809", 20)]
    public async Task IssuedNativeKeyGrantsOnlyItsSeatAndDoesNotInventAnAccountField(string build, int length)
    {
        var (registry, room, seat) = Room(build);
        var issued = await registry.IssueCapabilityAsync(seat.Game, length, default);
        Assert.Equal(length, issued.Length);
        var credential = issued.ToArray();
        Assert.True(MemoryMarshal.TryGetArray(issued, out var offered));
        offered.Array![offered.Offset] ^= 0xFF;
        var replies = new Replies(registry.Protocol)
        {
            BeforeScene = () => Assert.True(seat.RelayInitialized),
        };
        using var lifecycle = Lifecycle(registry, replies);
        var relay = new Session(10, build);
        await lifecycle.AuthenticateCapabilityAsync(relay, new(credential), default);
        Assert.Same(relay, seat.Relay);
        Assert.False(seat.RelayInitialized);
        Assert.IsType<MatchAuthenticationAccepted>(Assert.Single(replies.Sent));
        await lifecycle.RegisterSceneIdentityAsync(relay, new(1000000, "host"), default);
        Assert.Equal(new MatchSceneAccepted(1000000), replies.Sent[1]);
        await lifecycle.NameMapAsync(relay, new(1000000, "host", room.Setting.Map.Unknown68), default);
        Assert.True(registry.IsMatchBound(relay));
        var modern = new Session(11, build);
        seat.EndpointAdvertised = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.BindAsync(modern,
            new(Bytes(seat.Key.Write), seat.UserNum, seat.Nickname), default).AsTask());
        Assert.Same(relay, seat.Relay);
        Assert.False(registry.IsMatchBound(modern));
    }

    [Theory]
    [InlineData("kr-812", 20)]
    [InlineData("cn-324", 18)]
    [InlineData("cn-279", 12)]
    public async Task ForgedOrCrossProfileKeysNeverReceiveAnAcceptance(string build, int length)
    {
        var (registry, _, seat) = Room(build);
        var issued = await registry.IssueCapabilityAsync(seat.Game, length, default);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = Lifecycle(registry, replies);
        var forged = issued.ToArray();
        forged[^1] ^= 0x80;
        foreach (var (profile, key) in new[] { (build, forged), ("kr-999", issued.ToArray()) })
        {
            var relay = new Session(10, profile);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateCapabilityAsync(relay,
                new(key), default).AsTask());
            Assert.True(relay.Connection.IsClosed);
            Assert.Empty(replies.Sent);
            Assert.Null(seat.Relay);
            Assert.False(registry.TryGetSessionOfConnection(relay.Connection.Id, out _));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeAndAccountSceneMessagesCannotBypassEachOthersIdentityChecks(bool nativeJoin)
    {
        var (registry, _, seat) = Room("kr-812");
        var replies = new Replies(registry.Protocol);
        using var lifecycle = Lifecycle(registry, replies);
        var relay = new Session(10, "kr-812");
        if (nativeJoin)
        {
            var issued = await registry.IssueCapabilityAsync(seat.Game, 20, default);
            await lifecycle.AuthenticateCapabilityAsync(relay, new(issued), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.RegisterSceneAsync(relay,
                new(seat.UserNum, 1000000, seat.Nickname), default).AsTask());
        }
        else
        {
            seat.EndpointAdvertised = true;
            await lifecycle.AuthenticateAsync(relay, new(Bytes(seat.Key.Write), seat.UserNum, seat.Nickname), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.RegisterSceneIdentityAsync(relay,
                new(1000000, seat.Nickname), default).AsTask());
        }
        Assert.Single(replies.Sent);
        Assert.True(relay.Connection.IsClosed);
        Assert.Null(seat.Relay);
    }

    [Theory]
    [InlineData(1100000, "host")]
    [InlineData(1000000, "guest")]
    public async Task NativeReportMustMatchTheLeasedActorAndNickname(int entityId, string nickname)
    {
        var (registry, _, seat) = Room("kr-812");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 20, default);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = Lifecycle(registry, replies);
        var relay = new Session(10, "kr-812");
        await lifecycle.AuthenticateCapabilityAsync(relay, new(issued), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.RegisterSceneIdentityAsync(relay,
            new(entityId, nickname), default).AsTask());
        Assert.Single(replies.Sent);
        Assert.Null(seat.Relay);
        Assert.True(relay.Connection.IsClosed);
    }

    [Fact]
    public async Task ReplacementAndRestartRevokeOldKeysWithoutRemovingTheGameSeat()
    {
        var (registry, room, seat) = Room("cn-324");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 18, default);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = Lifecycle(registry, replies);
        var old = new Session(10, "cn-324");
        var replacement = new Session(11, "cn-324");
        await lifecycle.AuthenticateCapabilityAsync(old, new(issued), default);
        await lifecycle.AuthenticateCapabilityAsync(replacement, new(issued), default);
        Assert.True(old.Connection.IsClosed);
        Assert.False(registry.TryGetSessionOfConnection(old.Connection.Id, out _));
        lifecycle.Disconnect(old.Connection.Id);
        Assert.Same(replacement, seat.Relay);
        room.ResetToWaiting();
        room.MarkStarting();
        var nextKey = await registry.IssueCapabilityAsync(seat.Game, 18, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateCapabilityAsync(
            new Session(12, "cn-324"), new(issued), default).AsTask());
        var next = new Session(13, "cn-324");
        await lifecycle.AuthenticateCapabilityAsync(next, new(nextKey), default);
        Assert.True(replacement.Connection.IsClosed);
        Assert.Same(next, seat.Relay);
        lifecycle.Disconnect(replacement.Connection.Id);
        Assert.Same(next, seat.Relay);
        Assert.Single(room.Seats);
        registry.Detach(seat.Game.Id);
        Assert.False(registry.IsMatchBound(next));
        Assert.False(registry.TryGetSessionOfConnection(next.Connection.Id, out _));
    }

    [Fact]
    public async Task ResultAllowsTheExistingLeaseButRejectsNewNativeAdmission()
    {
        var (registry, room, seat) = Room("kr-812");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 20, default);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = Lifecycle(registry, replies);
        var relay = new Session(10, "kr-812");
        await lifecycle.AuthenticateCapabilityAsync(relay, new(issued), default);
        await lifecycle.RegisterSceneIdentityAsync(relay, new(1000000, "host"), default);
        room.MarkResult();
        await lifecycle.NameMapAsync(relay, new(1000000, "host", room.Setting.Map.Unknown68), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateCapabilityAsync(
            new Session(11, "kr-812"), new(issued), default).AsTask());
        Assert.Same(relay, seat.Relay);
        Assert.False(relay.Connection.IsClosed);
    }

    [Fact]
    public async Task IssuanceRejectsInvalidLengthsDuplicatesAndInactiveSeats()
    {
        var (registry, room, seat) = Room("kr-812", 20);
        foreach (var length in new[] { -1, 0, 21, int.MaxValue })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.IssueCapabilityAsync(seat.Game, length, default).AsTask());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.IssueCapabilityAsync(seat.Game, 20, canceled.Token).AsTask());
        await registry.IssueCapabilityAsync(seat.Game, 20, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.IssueCapabilityAsync(seat.Game, 20, default).AsTask());
        room.ResetToWaiting();
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.IssueCapabilityAsync(seat.Game, 20, default).AsTask());
        room.MarkStarting();
        await seat.Game.Connection.CloseAsync("closed test game");
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.IssueCapabilityAsync(seat.Game, 20, default).AsTask());
    }

    [Fact]
    public async Task NativeHandshakeTimeoutAndUnloadUseTheSharedCleanup()
    {
        var (registry, _, seat) = Room("cn-279");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 12, default);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = Lifecycle(registry, replies, 1000);
        var pending = new Session(10, "cn-279");
        await lifecycle.AuthenticateCapabilityAsync(pending, new(issued), default);
        await ((Connection)pending.Connection).Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(seat.Relay);
        Assert.False(registry.TryGetSessionOfConnection(pending.Connection.Id, out _));
        var next = new Session(11, "cn-279");
        await lifecycle.AuthenticateCapabilityAsync(next, new(issued), default);
        lifecycle.Dispose();
        Assert.True(next.Connection.IsClosed);
        Assert.Null(seat.Relay);
        Assert.False(registry.TryGetSessionOfConnection(next.Connection.Id, out _));
    }

    [Fact]
    public async Task DirectSuccessIsInitializedBeforeTheJoinReplyAndDoesNotSendAReportReply()
    {
        var (registry, room, seat) = Room("cn-279");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 12, default);
        var replies = new Replies(registry.Protocol)
        {
            BeforeAuthentication = () => Assert.True(seat.RelayInitialized),
        };
        using var lifecycle = new MatchConnectionLifecycle(registry, replies, replies,
            new() { Protocol = registry.Protocol, RequireCapabilitySceneReport = false }, NullLogger.Instance);
        var relay = new Session(10, "cn-279");
        await lifecycle.AuthenticateCapabilityAsync(relay, new(issued), default);
        Assert.IsType<MatchAuthenticationAccepted>(Assert.Single(replies.Sent));
        await lifecycle.NameMapAsync(relay, new(1000000, "host", room.Setting.Map.Unknown68), default);
        Assert.True(registry.IsMatchBound(relay));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.RegisterSceneIdentityAsync(relay,
            new(1000000, "host"), default).AsTask());
        Assert.Single(replies.Sent);
        Assert.True(relay.Connection.IsClosed);
    }

    [Fact]
    public async Task DirectSuccessReplyFailureReleasesTheInitializedSeat()
    {
        var (registry, _, seat) = Room("cn-279");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 12, default);
        var replies = new Replies(registry.Protocol)
        {
            BeforeAuthentication = () => throw new IOException("test acknowledgement failure"),
        };
        using var lifecycle = new MatchConnectionLifecycle(registry, replies, replies,
            new() { Protocol = registry.Protocol, RequireCapabilitySceneReport = false }, NullLogger.Instance);
        var relay = new Session(10, "cn-279");
        await Assert.ThrowsAsync<IOException>(() => lifecycle.AuthenticateCapabilityAsync(relay, new(issued), default).AsTask());
        Assert.False(seat.RelayInitialized);
        Assert.Null(seat.Relay);
        Assert.True(relay.Connection.IsClosed);
        Assert.False(registry.TryGetSessionOfConnection(relay.Connection.Id, out _));
    }

    [Fact]
    public async Task ClosedReplacementCannotDisplaceAnInitializedNativeSeat()
    {
        var (registry, _, seat) = Room("kr-812");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 20, default);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = Lifecycle(registry, replies);
        var current = new Session(10, "kr-812");
        await lifecycle.AuthenticateCapabilityAsync(current, new(issued), default);
        await lifecycle.RegisterSceneIdentityAsync(current, new(1000000, "host"), default);
        var closed = new Session(11, "kr-812");
        await closed.Connection.CloseAsync("test already closed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AuthenticateCapabilityAsync(closed,
            new(issued), default).AsTask());
        Assert.Same(current, seat.Relay);
        Assert.True(seat.RelayInitialized);
        Assert.False(current.Connection.IsClosed);
        Assert.Equal(2, replies.Sent.Count);
        Assert.True(registry.TryGetSessionOfConnection(current.Connection.Id, out _));
        Assert.False(registry.TryGetSessionOfConnection(closed.Connection.Id, out _));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task DirectNativeSuccessOptionDoesNotSkipModernSceneOrAccountChecks(int reportAccount)
    {
        var (registry, _, seat) = Room("kr-812");
        seat.EndpointAdvertised = true;
        var replies = new Replies(registry.Protocol);
        using var lifecycle = new MatchConnectionLifecycle(registry, replies, replies,
            new() { Protocol = registry.Protocol, RequireCapabilitySceneReport = false }, NullLogger.Instance);
        var relay = new Session(10, "kr-812");
        await lifecycle.AuthenticateAsync(relay, new(Bytes(seat.Key.Write), seat.UserNum, seat.Nickname), default);
        Assert.False(seat.RelayInitialized);
        Assert.Single(replies.Sent);
        var scene = lifecycle.RegisterSceneAsync(relay, new(reportAccount, 1000000, "host"), default).AsTask();
        if (reportAccount == seat.UserNum)
        {
            await scene;
            Assert.True(seat.RelayInitialized);
            Assert.IsType<MatchSceneAccepted>(replies.Sent[1]);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scene);
            Assert.Single(replies.Sent);
            Assert.Null(seat.Relay);
            Assert.True(relay.Connection.IsClosed);
        }
    }

    [Fact]
    public async Task CanceledNativeAuthenticationReleasesCapacityForTheNextTransport()
    {
        var (registry, _, seat) = Room("kr-812");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 20, default);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = new MatchConnectionLifecycle(registry, replies, replies,
            new() { Protocol = registry.Protocol, MaxConnections = 1 }, NullLogger.Instance);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var rejected = new Session(10, "kr-812");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifecycle.AuthenticateCapabilityAsync(rejected,
            new(issued), canceled.Token).AsTask());
        Assert.Null(seat.Relay);
        Assert.Empty(replies.Sent);
        Assert.True(rejected.Connection.IsClosed);
        var next = new Session(11, "kr-812");
        await lifecycle.AuthenticateCapabilityAsync(next, new(issued), default);
        Assert.Same(next, seat.Relay);
        Assert.Single(replies.Sent);
    }

    [Fact]
    public async Task UnloadDuringDelayedNativeBindingCannotRestoreTheLeaseOrSendReplies()
    {
        var (registry, _, seat) = Room("kr-812");
        var issued = await registry.IssueCapabilityAsync(seat.Game, 20, default);
        var blocked = new DelayedCapabilityDirectory(registry);
        var replies = new Replies(registry.Protocol);
        using var lifecycle = new MatchConnectionLifecycle(blocked, replies, replies,
            new() { Protocol = registry.Protocol }, NullLogger.Instance);
        var relay = new Session(10, "kr-812");
        var pending = lifecycle.AuthenticateCapabilityAsync(relay, new(issued), default).AsTask();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.Dispose();
        blocked.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(seat.Relay);
        Assert.Empty(replies.Sent);
        Assert.True(relay.Connection.IsClosed);
        Assert.False(registry.TryGetSessionOfConnection(relay.Connection.Id, out _));
    }

    private sealed class DelayedCapabilityDirectory(IMatchCapabilityDirectory inner) : IMatchCapabilityDirectory
    {
        public string Protocol => inner.Protocol;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ReadOnlyMemory<byte>> IssueCapabilityAsync(ISession game, int ticketLength, CancellationToken ct) =>
            inner.IssueCapabilityAsync(game, ticketLength, ct);
        public async ValueTask<MatchSeatBinding> BindCapabilityAsync(ISession session, MatchCapabilityAuthenticate message, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Continue.Task;
            return await inner.BindCapabilityAsync(session, message, CancellationToken.None);
        }
        public ValueTask<MatchSeatBinding> BindAsync(ISession session, MatchAuthenticate message, CancellationToken ct) =>
            inner.BindAsync(session, message, ct);
        public bool IsCurrent(ISession session, MatchSeatBinding binding) => inner.IsCurrent(session, binding);
        public void MarkInitialized(ISession session, MatchSeatBinding binding) => inner.MarkInitialized(session, binding);
        public void Release(ISession session) => inner.Release(session);
    }

    private static (RoomRegistry Registry, PlayRoom Room, RoomSeat Seat) Room(string build, int maxBytes = 64)
    {
        var registry = new RoomRegistry(new() { RelayProtocol = build + "/relay", MaxMatchCapabilityBytes = maxBytes });
        var setting = new SessionSettingBlob(0, 0, "", 0, 0, 0, 0, "", 2,
            MapMetaBlob.Empty() with { Unknown4 = 1, Unknown68 = "rel_Village01_Byway" }, 0, 0, 0, 0, "", 0, 0, 0, 0, 0);
        var (room, seat) = registry.Create(new Session(1, build), setting, 7, "host");
        room.MarkStarting();
        return (registry, room, seat);
    }

    private static MatchConnectionLifecycle Lifecycle(RoomRegistry registry, Replies replies, int timeout = 15000) =>
        new(registry, replies, replies, new() { Protocol = registry.Protocol, HandshakeTimeoutMs = timeout }, NullLogger.Instance);

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class Replies(string protocol) : IMessageSender<MatchAuthenticationAccepted>, IMessageSender<MatchSceneAccepted>
    {
        public string Protocol => protocol;
        internal List<IMessage> Sent { get; } = [];
        internal Action? BeforeAuthentication { get; init; }
        internal Action? BeforeScene { get; init; }
        public ValueTask SendAsync(ISession session, MatchAuthenticationAccepted message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BeforeAuthentication?.Invoke();
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
        public ValueTask SendAsync(ISession session, MatchSceneAccepted message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BeforeScene?.Invoke();
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session(long id, string build) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask CloseAsync(string reason) { IsClosed = true; Closed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
