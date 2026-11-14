using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>Checks loading barriers, single-player defaults, explicit overrides and stale-cookie revocation.</summary>
public sealed class UdpRelayCoordinatorTests
{
    /// <summary>With no match connection the loop holds no timer; the first registration starts it and the offer follows.</summary>
    [Fact]
    public async Task TheLoopSleepsUntilAMatchConnectionRegisters()
    {
        var game = new Session(1);
        var relay = new Session(11);
        var roster = new Roster([new(0, 1, 1, game, relay, true)]) { Stage = RoomStage.Playing, Override = true };
        using var directory = new MatchUdpDirectory(new() { Protocol = "test/relay" });
        Assert.False(directory.WhenAnyRegistered().IsCompleted);
        var control = new Control();
        var coordinator = new UdpRelayCoordinator(new() { TickMs = 10 }, roster, directory, control, NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var run = coordinator.RunAsync(stop.Token).AsTask();
        await Task.Delay(150);
        Assert.Empty(control.Offers);
        Assert.False(run.IsCompleted);

        var lease = directory.Register(relay, () => true);
        Assert.True(directory.WhenAnyRegistered().IsCompleted);
        var deadline = Environment.TickCount64 + 10_000;
        while (directory.StateOf(relay) == MatchUdpRelayState.Registered)
        {
            Assert.True(Environment.TickCount64 < deadline, "The loop did not wake for the registration.");
            await Task.Delay(5);
        }
        Assert.Equal(MatchUdpRelayState.Offered, directory.StateOf(relay));

        lease.Dispose();
        Assert.False(directory.WhenAnyRegistered().IsCompleted);
        stop.Cancel();
        await run;
    }

    [Theory]
    [InlineData(RoomStage.Waiting)]
    [InlineData(RoomStage.Starting)]
    [InlineData(RoomStage.Playing)]
    [InlineData(RoomStage.Result)]
    public async Task NonMatchScenesCannotOfferUdpEvenWithAForcedOverride(RoomStage stage)
    {
        var game = new Session(1);
        var relay = new Session(11);
        var roster = new Roster([new(0, 1, 1, game, relay, true)])
            { Stage = stage, Override = true, IsMatchRoom = false };
        using var directory = new MatchUdpDirectory(new() { Protocol = "test/relay" });
        using var lease = directory.Register(relay, () => true);
        var control = new Control();
        var coordinator = new UdpRelayCoordinator(new(), roster, directory, control, NullLogger.Instance);
        await coordinator.TickAsync(default);
        Assert.Empty(control.Offers);
        Assert.Equal(MatchUdpRelayState.Registered, directory.StateOf(relay));
    }

    [Fact]
    public async Task OffersWaitForRoundStartAndEveryInitializedRelayThenFollowRoomOverrides()
    {
        var a = new Session(1);
        var b = new Session(2);
        var relayA = new Session(11);
        var relayB = new Session(12);
        var roster = new Roster([new(0, 1, 1, a, relayA, true), new(1, 2, 2, b, relayB, false)]);
        using var directory = new MatchUdpDirectory(new() { Protocol = "test/relay" });
        using var first = directory.Register(relayA, () => !relayA.Connection.IsClosed);
        using var second = directory.Register(relayB, () => !relayB.Connection.IsClosed);
        var control = new Control();
        var coordinator = new UdpRelayCoordinator(new(), roster, directory, control, NullLogger.Instance);
        await coordinator.TickAsync(default);
        Assert.Empty(control.Offers);
        roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(default);
        Assert.Empty(control.Offers);
        roster.Seats[1] = roster.Seats[1] with { RelayInitialized = true };
        await coordinator.TickAsync(default);
        Assert.Equal(2, control.Offers.Count);
        var oldToken = control.Offers[0].Identity;
        var endpoint = new IPEndPoint(IPAddress.Loopback, 7603);
        Assert.True(directory.TryBind(oldToken, endpoint, out _));
        Assert.False(directory.TryGetEndpoint(relayA, out _));
        Assert.True(directory.Verify(relayA));
        Assert.True(directory.TryGetEndpoint(relayA, out _));
        await coordinator.TickAsync(default);
        Assert.Equal(2, control.Offers.Count);

        roster.Override = false;
        await coordinator.TickAsync(default);
        Assert.Equal(2, control.Withdrawals.Count);
        Assert.False(directory.TryResolve(oldToken, endpoint, out _));
        Assert.False(directory.TryGetEndpoint(relayA, out _));
        roster.Seats.RemoveAt(1);
        await coordinator.TickAsync(default);
        Assert.Equal(2, control.Offers.Count);
        roster.Override = null;
        await coordinator.TickAsync(default);
        Assert.Equal(2, control.Offers.Count);
        roster.Override = true;
        await coordinator.TickAsync(default);
        Assert.Equal(3, control.Offers.Count);
        Assert.NotEqual(oldToken, control.Offers[^1].Identity);
        Assert.False(directory.TryBind(oldToken, endpoint, out _));
        roster.Override = null;
        await coordinator.TickAsync(default);
        Assert.Equal(3, control.Withdrawals.Count);
    }

    [Fact]
    public async Task StatusFollowsEachSeatFromWaitingThroughOfferToVerifiedUdp()
    {
        var a = new Session(1);
        var b = new Session(2);
        var relayA = new Session(11);
        var roster = new Roster([new(0, 1, 1, a, relayA, true), new(1, 2, 2, b, null, false)]);
        using var directory = new MatchUdpDirectory(new() { Protocol = "test/relay" });
        var status = new UdpRelayStatus("test/game", roster, directory);
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "match connection not registered for UDP"), status.Describe(a));
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "no match connection"), status.Describe(b));
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "not seated"), status.Describe(new Session(3)));
        using var lease = directory.Register(relayA, () => true);
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "waiting for the round"), status.Describe(a));
        roster.Stage = RoomStage.Playing;
        roster.Override = false;
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "room uses TCP"), status.Describe(a));
        roster.Override = true;
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "waiting for every match connection"), status.Describe(a));
        var control = new Control();
        roster.Seats.RemoveAt(1);
        await new UdpRelayCoordinator(new(), roster, directory, control, NullLogger.Instance).TickAsync(default);
        Assert.Equal(UdpPathState.Negotiating, status.Describe(a).State);
        var offered = new Session(4);
        var offeredRelay = new Session(14);
        roster.Seats.Add(new(1, 2, 4, offered, offeredRelay, true));
        using var offeredLease = directory.Register(offeredRelay, () => true);
        _ = await directory.IssueAsync(offeredRelay, 4, default);
        directory.Disable(offeredRelay);
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp,
            "fallback: the client gave up before any UDP probe reached the server; its UDP to the server port is blocked or lost"),
            status.Describe(offered));
        roster.Seats.RemoveAt(1);
        Assert.True(directory.TryBind(control.Offers[0].Identity, new IPEndPoint(IPAddress.Loopback, 7603), out _));
        Assert.Equal(new UdpPathStatus(UdpPathState.Negotiating, "bound, answering the challenge"), status.Describe(a));
        Assert.True(directory.Verify(relayA));
        Assert.Equal(new UdpPathStatus(UdpPathState.Udp, "verified"), status.Describe(a));
        directory.Disable(relayA);
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "fallback: the client reported the verified UDP path failed during the round"), status.Describe(a));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADisconnectDuringOfferDoesNotStopOtherSessionsButLiveFailuresRemainVisible(bool close)
    {
        var a = new Session(1);
        var b = new Session(2);
        var relayA = new Session(11);
        var relayB = new Session(12);
        var roster = new Roster([new(0, 1, 1, a, relayA, true), new(1, 2, 2, b, relayB, true)])
            { Stage = RoomStage.Playing, Override = true };
        using var directory = new MatchUdpDirectory(new() { Protocol = "test/relay" });
        using var first = directory.Register(relayA, () => true);
        using var second = directory.Register(relayB, () => true);
        var control = new Control { BeforeOffer = session =>
        {
            if (session.Id != relayA.Id) return;
            if (close)
            {
                session.Connection.CloseAsync("test disconnect").GetAwaiter().GetResult();
                roster.Seats.RemoveAt(0);
            }
            throw new IOException("Offer write failed.");
        } };
        var coordinator = new UdpRelayCoordinator(new(), roster, directory, control, NullLogger.Instance);
        if (!close)
            await Assert.ThrowsAsync<IOException>(() => coordinator.TickAsync(default).AsTask());
        else
        {
            await coordinator.TickAsync(default);
            Assert.Same(relayB, Assert.Single(control.Offers).Session);
            Assert.Equal(MatchUdpRelayState.Absent, directory.StateOf(relayA));
        }
    }

    private sealed class Roster(List<RoomSeatView> seats) : IRoomPeerRoster
    {
        public string Protocol => "test/game";
        public IReadOnlyList<RoomRosterView> Snapshot() => [Find(1)!];
        public async ValueTask<bool> ExecuteAsync(int roomId, Func<RoomRosterView, CancellationToken, ValueTask> operation, CancellationToken ct)
        {
            if (Find(roomId) is not { } view) return false;
            await operation(view, ct);
            return true;
        }
        internal List<RoomSeatView> Seats { get; } = seats;
        internal RoomStage Stage = RoomStage.Starting;
        internal bool? Override;
        internal bool IsMatchRoom = true;
        public RoomRosterView? Find(int roomId) => roomId == 1 ? new(1, Stage, 1, 1, Seats.ToArray(), false) { IsMatchRoom = IsMatchRoom, UdpOverride = Override } : null;
        public RoomRosterView? FindBySession(long sessionId) => Seats.Any(seat => seat.Game.Id == sessionId || seat.Relay?.Id == sessionId) ? Find(1) : null;
    }

    private sealed class Control : IMatchUdpRelayControl
    {
        public string Protocol => "test/relay";
        public ushort BoundPort => 7600;
        public int IdentityBytes => 4;
        internal List<(ISession Session, byte[] Identity)> Offers { get; } = [];
        internal List<ISession> Withdrawals { get; } = [];
        internal Action<ISession>? BeforeOffer;
        public ValueTask OfferAsync(ISession session, ushort port, ReadOnlyMemory<byte> identity, CancellationToken ct)
        { BeforeOffer?.Invoke(session); Assert.Equal(BoundPort, port); Offers.Add((session, identity.ToArray())); return ValueTask.CompletedTask; }
        public ValueTask WithdrawAsync(ISession session, CancellationToken ct)
        { Withdrawals.Add(session); return ValueTask.CompletedTask; }
    }

    private sealed class Session(long id) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new ConnectionStub(id);
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "kr-812");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ConnectionStub(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 7601);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
