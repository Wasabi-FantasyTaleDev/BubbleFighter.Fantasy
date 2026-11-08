using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Listeners;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class MigrationDestinationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedRoomAndHomeArrivalsAreBoundOnceAndResolveDnsForEveryRequest(bool home)
    {
        await using var rig = new Rig();
        var request = home ? rig.Request with { Kind = MigrationKind.Home, Room = null, HomeNode = "b" } : rig.Request;
        var offer = await rig.Destination.AcceptAsync(request, default);
        Assert.True(offer.Accepted);
        Assert.Equal(TimeSpan.FromSeconds(10), offer.Lifetime);
        Assert.Equal(IPAddress.Loopback, offer.Ipv4);
        Assert.Equal(home ? 0 : 1, rig.Rooms.Prepared);
        var arrival = Assert.IsType<MigrationArrival>(rig.Arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(rig.Arrivals, request.ClientAddress)));
        Assert.Equal("game", arrival.ListenerId);
        Assert.Equal(home, arrival.Reservation is null);
        Assert.Equal(offer.Lifetime, arrival.Lifetime);
        Assert.Null(rig.Arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(rig.Arrivals, request.ClientAddress)));
        Assert.True((await rig.Destination.AcceptAsync(request with { MigrationId = new string('b', 32) }, default)).Accepted);
        Assert.Equal(2, rig.Resolutions);
        // The consumer owns a consumed reservation, including its failure cleanup.
        if (arrival.Reservation is { } reservation) await rig.Rooms.CancelAsync(reservation, default);
    }

    [Fact]
    public async Task RefusalIsForwardedAndArrivalCapacityFailureReleasesThePreparedSeat()
    {
        await using var rig = new Rig(capacity: 1);
        rig.Rooms.Refusal = RoomJoinRefusal.WrongPassword;
        Assert.Equal(RoomJoinRefusal.WrongPassword, (await rig.Destination.AcceptAsync(rig.Request, default)).Refusal);
        Assert.Equal(0, rig.Arrivals.Count);
        rig.Rooms.Refusal = null;
        Assert.True((await rig.Destination.AcceptAsync(rig.Request, default)).Accepted);
        var another = rig.Request with { MigrationId = new string('b', 32), UserNum = 8, Room = rig.Request.Room! with { UserNumber = 8 } };
        Assert.False((await rig.Destination.AcceptAsync(another, default)).Accepted);
        Assert.Equal(1, rig.Rooms.Cancelled);
        Assert.Single(rig.Rooms.Held);
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("ipv6")]
    [InlineData("epoch")]
    [InlineData("node")]
    [InlineData("build")]
    public async Task InvalidDestinationNeverPreparesASeat(string failure)
    {
        await using var rig = new Rig();
        rig.DnsFailure = failure == "dns";
        rig.Ipv6Only = failure == "ipv6";
        var request = failure switch
        {
            "epoch" => rig.Request with { DestinationEpoch = 2 },
            "node" => rig.Request with { DestinationNode = "c" },
            "build" => rig.Request with { ClientBuild = "kr-1256" },
            _ => rig.Request,
        };
        Assert.False((await rig.Destination.AcceptAsync(request, default)).Accepted);
        Assert.Equal(0, rig.Rooms.Prepared);
        Assert.Equal(0, rig.Arrivals.Count);
    }

    [Fact]
    public async Task ExpiredSeatSurvivesReadSideExpiryAndFailedCleanupUntilRetrySucceeds()
    {
        await using var rig = new Rig(capacity: 1);
        Assert.True((await rig.Destination.AcceptAsync(rig.Request, default)).Accepted);
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(rig.Arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(rig.Arrivals, rig.Request.ClientAddress)));
        Assert.Null(rig.Arrivals.Cancel(rig.Request.MigrationId));
        rig.Rooms.FailCancel = true;
        await rig.Destination.SweepAsync(default);
        Assert.Single(rig.Arrivals.Collect());
        Assert.Single(rig.Rooms.Held);
        rig.Rooms.FailCancel = false;
        await rig.Destination.SweepAsync(default);
        Assert.Empty(rig.Rooms.Held);
        Assert.Empty(rig.Arrivals.Collect());
        Assert.True((await rig.Destination.AcceptAsync(rig.Request, default)).Accepted);
    }

    [Fact]
    public async Task CancellationRetainsFailedCleanupAndUnloadReleasesRemainingSeats()
    {
        await using var rig = new Rig();
        Assert.True((await rig.Destination.AcceptAsync(rig.Request, default)).Accepted);
        rig.Rooms.FailCancel = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Destination.CancelAsync(rig.Request.MigrationId, default).AsTask());
        Assert.Null(rig.Arrivals.TryConsume("kr-812", 7, MigrationTestConnection.Accept(rig.Arrivals, rig.Request.ClientAddress)));
        rig.Rooms.FailCancel = false;
        await rig.Destination.CancelAsync(rig.Request.MigrationId, default);
        await rig.Destination.CancelAsync(rig.Request.MigrationId, default);
        Assert.Empty(rig.Rooms.Held);
        Assert.True((await rig.Destination.AcceptAsync(rig.Request, default)).Accepted);
        await rig.Destination.DisposeAsync();
        Assert.Empty(rig.Rooms.Held);
        Assert.Empty(rig.Arrivals.Collect());
    }

    [Fact]
    public async Task LeaseChangeDuringPreparationReleasesTheSeatWithoutCreatingAnArrival()
    {
        await using var rig = new Rig();
        rig.Rooms.AfterPrepare = () => rig.Lease.Publish(new("new", 2, 30));
        Assert.False((await rig.Destination.AcceptAsync(rig.Request, default)).Accepted);
        Assert.Empty(rig.Rooms.Held);
        Assert.Equal(0, rig.Arrivals.Count);
    }

    [Fact]
    public void ConfigurationRejectsNumericHostsDuplicateBuildsAndMissingEndpoints()
    {
        var options = Rig.Options();
        Assert.Empty(options.Validate());
        options.Endpoints[0].ListenerId = "";
        Assert.NotEmpty(options.Validate());
        options.Endpoints[0].ListenerId = "game";
        options.Endpoints[0].Host = "127.0.0.1";
        Assert.NotEmpty(options.Validate());
        options.Endpoints[0].Host = "game.example";
        options.Endpoints = [options.Endpoints[0], options.Endpoints[0]];
        Assert.NotEmpty(options.Validate());
        options.Endpoints = [];
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void DestinationRequiresTheConfiguredActiveGameListenerForEachBuild()
    {
        var options = Rig.Options();
        Assert.Throws<ConfigException>(() => MigrationDestinationPlugin.ValidateListeners(options, []));
        Assert.Throws<ConfigException>(() => MigrationDestinationPlugin.ValidateListeners(options, [new Listener("other", "kr-812/game")]));
        Assert.Throws<ConfigException>(() => MigrationDestinationPlugin.ValidateListeners(options, [new Listener("game", "kr-1256/game")]));
        Assert.Throws<ConfigException>(() => MigrationDestinationPlugin.ValidateListeners(options, [new Listener("game", "kr-812/relay")]));
        Assert.Throws<ConfigException>(() => MigrationDestinationPlugin.ValidateListeners(options,
            [new Listener("game", "kr-812/game"), new Listener("game", "kr-812/game")]));
        MigrationDestinationPlugin.ValidateListeners(options, [new Listener("game", "kr-812/game")]);
    }

    private sealed class Listener(string id, string protocol) : IListenerEndpoint
    {
        public string RowId => id;
        public string Protocol => protocol;
        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 7100);
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal Clock Clock { get; } = new();
        internal Rooms Rooms { get; } = new();
        internal NodeLeaseSource Lease { get; } = new("b", "https://directory.example");
        internal MigrationArrivals Arrivals { get; }
        internal MigrationDestination Destination { get; }
        internal bool DnsFailure;
        internal bool Ipv6Only;
        internal int Resolutions;
        internal MigrationRequest Request { get; } = new(new string('a', 32), MigrationKind.Room, "a", "b", 1, "kr-812", 7,
            IPAddress.Parse("203.0.113.7"), new(3, Guid.NewGuid(), 7, "private-password")) { HomeNode = "a", SourceChannel = 10 };

        internal Rig(int capacity = 4, bool home = true)
        {
            Lease.Publish(new("lease", 1, 30));
            Arrivals = new(capacity, Clock);
            Destination = new(Options(), Lease, Arrivals, Arrivals, [Rooms], (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Resolutions++;
                if (DnsFailure) throw new SocketException((int)SocketError.HostNotFound);
                return Task.FromResult<IPAddress[]>(Ipv6Only ? [IPAddress.IPv6Loopback] : [IPAddress.IPv6Loopback, IPAddress.Loopback]);
            }, NullLogger.Instance, home ? [new HomeAdmission()] : []);
        }

        internal static MigrationDestinationOptions Options() => new()
        { NodeId = "b", Endpoints = [new() { ClientBuild = "kr-812", ListenerId = "game", Host = "game.example", Port = 7100 }] };
        public ValueTask DisposeAsync() => Destination.DisposeAsync();
    }

    private sealed class HomeAdmission : IMigrationHomeAdmission
    {
        public string ClientBuild => "kr-812";
        public ValueTask<bool> EnterAsync(ISession session, BubbleFighter.Fantasy.Contracts.Sessions.SessionPhase scene, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task HomePreparationRequiresAnEnabledHomeSceneAndTheOriginalNode()
    {
        await using var disabled = new Rig(home: false);
        Assert.False((await disabled.Destination.AcceptAsync(disabled.Request with { Kind = MigrationKind.Home, Room = null, HomeNode = "b" }, default)).Accepted);
        await using var enabled = new Rig();
        Assert.False((await enabled.Destination.AcceptAsync(enabled.Request with { Kind = MigrationKind.Home, Room = null }, default)).Accepted);
    }

    private sealed class Rooms : IRoomAdmissionDirectory
    {
        internal int Prepared;
        internal int Cancelled;
        internal bool FailCancel;
        internal Action? AfterPrepare;
        internal RoomJoinRefusal? Refusal;
        internal HashSet<RoomAdmissionReservation> Held { get; } = [];
        public string ClientBuild => "kr-812";
        public ValueTask<RoomAdmissionPreparation> PrepareAsync(RoomAdmissionRequest request, CancellationToken ct)
        {
            Prepared++;
            if (Refusal is not null) return ValueTask.FromResult(new RoomAdmissionPreparation(null, Refusal));
            var reservation = new RoomAdmissionReservation(Guid.NewGuid(), request.RoomId, request.InstanceId, request.UserNumber);
            Held.Add(reservation);
            AfterPrepare?.Invoke();
            return ValueTask.FromResult(new RoomAdmissionPreparation(reservation, null));
        }
        public ValueTask CancelAsync(RoomAdmissionReservation reservation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailCancel) throw new IOException("Seat cancellation failed.");
            if (Held.Remove(reservation)) Cancelled++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> JoinAsync(ISession session, RoomAdmissionReservation reservation, CancellationToken ct) =>
            throw new InvalidOperationException("Preparation must not join an unauthenticated game connection.");
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
