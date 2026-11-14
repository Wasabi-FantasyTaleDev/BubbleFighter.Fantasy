using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class MigrationArrivalTests
{
    private static readonly IPAddress Player = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task OnlyTheObservedOpenConnectionOnTheDestinationListenerCanConsume()
    {
        var arrivals = new MigrationArrivals(1, new Clock());
        var arrival = Arrival("0123456789abcdef0123456789abcdef");
        arrivals.Expect(arrival);
        var unobserved = new MigrationTestConnection(new IPEndPoint(Player, 40000));
        Assert.Null(arrivals.TryConsume("kr-812", 7, unobserved));
        var wrong = MigrationTestConnection.Accept(arrivals, Player, "other-game");
        Assert.Null(arrivals.TryConsume("kr-812", 7, wrong));
        Assert.Throws<InvalidOperationException>(() => arrivals.Observe(new(wrong, "game")));
        var closed = MigrationTestConnection.Accept(arrivals, Player);
        await closed.CloseAsync("closed before migration");
        Assert.Null(arrivals.TryConsume("kr-812", 7, closed));
        var nonIp = new MigrationTestConnection(new DnsEndPoint("game.example", 40000));
        arrivals.Observe(new(nonIp, "game"));
        Assert.Null(arrivals.TryConsume("kr-812", 7, nonIp));
        Assert.Equal(1, arrivals.Count);
        var correct = MigrationTestConnection.Accept(arrivals, Player);
        Assert.Equal(unobserved.Id, correct.Id);
        Assert.Equal(arrival, arrivals.TryConsume("kr-812", 7, correct));
        Assert.Null(arrivals.TryConsume("kr-812", 7, correct));
    }

    [Fact]
    public void TheCleanupSignalIsCompleteExactlyWhileAnArrivalIsHeld()
    {
        var arrivals = new MigrationArrivals(2, new Clock());
        Assert.False(arrivals.WhenAnyTracked().IsCompleted);
        var consumed = Arrival("0123456789abcdef0123456789abcdef");
        arrivals.Expect(consumed);
        Assert.True(arrivals.WhenAnyTracked().IsCompleted);
        Assert.Equal(consumed, arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(arrivals, Player)));
        Assert.False(arrivals.WhenAnyTracked().IsCompleted);

        // A retired arrival still holds its seat, so the clean-up must keep running until it completes it.
        var retired = Arrival("ffffffffffffffffffffffffffffffff");
        arrivals.Expect(retired);
        var waiting = arrivals.WhenAnyTracked();
        Assert.Equal(retired, arrivals.Retire(retired.Request.MigrationId));
        Assert.True(waiting.IsCompleted);
        Assert.True(arrivals.WhenAnyTracked().IsCompleted);
        arrivals.Complete(retired);
        Assert.False(arrivals.WhenAnyTracked().IsCompleted);
    }

    [Fact]
    public async Task PluginObservesTransportEventsAndRemovesItsObservationHandlerOnUnload()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "arrivals", Plugin = "cluster.migration-arrivals", Source = "test" },
            services, events, NullLogger.Instance);
        IMigrationArrivals arrivals;
        try
        {
            await new MigrationArrivalsPlugin().ActivateAsync(context, default);
            arrivals = services.Get<IMigrationArrivals>();
            var arrival = Arrival("0123456789abcdef0123456789abcdef");
            arrivals.Expect(arrival);
            var connection = new MigrationTestConnection(new IPEndPoint(Player, 40000));
            Assert.Null(arrivals.TryConsume("kr-812", 7, connection));
            await events.SerialAsync(new ConnectionAcceptedEvent(connection, "game"), default);
            Assert.Equal(arrival, arrivals.TryConsume("kr-812", 7, connection));
        }
        finally { await context.UnwindAsync(); }
        Assert.False(services.TryGet<IMigrationArrivals>(out _));
        arrivals.Expect(Arrival("ffffffffffffffffffffffffffffffff"));
        var afterUnload = new MigrationTestConnection(new IPEndPoint(Player, 40000));
        await events.SerialAsync(new ConnectionAcceptedEvent(afterUnload, "game"), default);
        Assert.Null(arrivals.TryConsume("kr-812", 7, afterUnload));
    }

    [Fact]
    public void AnArrivalIsConsumedOnceOnlyFromTheObservedAddressAndBuild()
    {
        var arrivals = new MigrationArrivals(4, new Clock());
        arrivals.Expect(Arrival("0123456789abcdef0123456789abcdef"));
        Assert.Null(arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(arrivals, IPAddress.Parse("203.0.113.8"))));
        Assert.Null(arrivals.TryConsume("kr-1256", 7, MigrationTestConnection.Accept(arrivals, Player)));
        Assert.Null(arrivals.TryConsume("kr-812", 8, MigrationTestConnection.Accept(arrivals, Player)));
        var consumed = arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(arrivals, Player.MapToIPv6()));
        Assert.NotNull(consumed);
        Assert.Null(arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(arrivals, Player)));
        Assert.Null(arrivals.Cancel("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void ArrivalsExpireWithTheMonotonicClockAndCanBeCancelled()
    {
        var clock = new Clock();
        var arrivals = new MigrationArrivals(4, clock);
        arrivals.Expect(Arrival("0123456789abcdef0123456789abcdef"));
        clock.Advance(10_000);
        Assert.Null(arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(arrivals, Player)));
        var expired = Assert.Single(arrivals.Collect());
        Assert.NotNull(expired.Reservation);
        arrivals.Complete(expired);
        arrivals.Expect(Arrival("ffffffffffffffffffffffffffffffff"));
        Assert.NotNull(arrivals.Cancel("ffffffffffffffffffffffffffffffff"));
        Assert.Equal(0, arrivals.Count);
    }

    [Fact]
    public void CapacityAndOnePendingMigrationPerAccountAreEnforced()
    {
        var arrivals = new MigrationArrivals(1, new Clock());
        arrivals.Expect(Arrival("0123456789abcdef0123456789abcdef"));
        Assert.Throws<InvalidOperationException>(() => arrivals.Expect(Arrival("ffffffffffffffffffffffffffffffff")));
        Assert.Throws<InvalidOperationException>(() => arrivals.Expect(Arrival("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", user: 9)));
        Assert.Throws<ArgumentException>(() => arrivals.Expect(Arrival("not-an-id")));
        Assert.Throws<ArgumentException>(() => arrivals.Expect(Arrival("dddddddddddddddddddddddddddddddd") with { ListenerId = "" }));
        Assert.Throws<ArgumentException>(() => arrivals.Expect(Arrival("dddddddddddddddddddddddddddddddd", kind: MigrationKind.Home)));
    }

    [Fact]
    public void RoomArrivalRetainsChannelHomeAndReadOnlyVisitorSnapshot()
    {
        var arrivals = new MigrationArrivals(1, new Clock());
        var arrival = Arrival("0123456789abcdef0123456789abcdef");
        var guest = new MigrationGuest("Visitor", "Origin", 12, [101, 202], 3);
        arrival = arrival with { Request = arrival.Request with { SourceChannel = 10, HomeNode = "node-a", Guest = guest } };
        arrivals.Expect(arrival);
        var consumed = Assert.IsType<MigrationArrival>(arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(arrivals, Player)));
        Assert.Equal(10, consumed.Request.SourceChannel);
        Assert.Equal("node-a", consumed.Request.HomeNode);
        Assert.Equal(guest, consumed.Request.Guest);
    }

    [Fact]
    public void HomeArrivalHasNoRoomReservationOrVisitorSnapshot()
    {
        var arrivals = new MigrationArrivals(1, new Clock());
        var room = Arrival("0123456789abcdef0123456789abcdef");
        var home = room with { Request = room.Request with { Kind = MigrationKind.Home, Room = null }, Reservation = null };
        Assert.Throws<ArgumentException>(() => arrivals.Expect(home with { Reservation = room.Reservation }));
        Assert.Throws<ArgumentException>(() => arrivals.Expect(home with { Request = home.Request with { Room = room.Request.Room } }));
        Assert.Throws<ArgumentException>(() => arrivals.Expect(home with
        {
            Request = home.Request with { Guest = new("Visitor", "Origin", 1, [], 0) },
        }));
        Assert.Throws<ArgumentException>(() => arrivals.Expect(home with { Request = home.Request with { Kind = (MigrationKind)2 } }));
        arrivals.Expect(home);
        Assert.Equal(home, arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(arrivals, Player)));
    }

    private static MigrationArrival Arrival(string id, long user = 7, MigrationKind kind = MigrationKind.Room)
    {
        var room = new RoomAdmissionRequest(3, Guid.NewGuid(), (int)user, "");
        return new(new(id, kind, "node-a", "node-b", 1, "kr-812", user, Player, room),
            new(Guid.NewGuid(), 3, room.InstanceId, (int)user), TimeSpan.FromSeconds(10)) { ListenerId = "game" };
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(int milliseconds) => _timestamp += milliseconds;
    }
}
