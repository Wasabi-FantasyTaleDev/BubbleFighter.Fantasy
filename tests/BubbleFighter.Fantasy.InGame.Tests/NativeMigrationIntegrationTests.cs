using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class NativeMigrationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryClickTransfersOneWriterAndInstallsTheReservedRoom(bool acknowledge)
    {
        await using var f = await Fixture.StartAsync(acknowledge);
        await f.ClickAsync();
        Assert.Equal(new[] { "offer" }, f.Events);
        Assert.False(await f.OwnsAsync(f.Source));
        Assert.Empty(f.Authority.Holds);
        Assert.Equal(1, f.TargetRoom.PendingAdmissionCount);
        Assert.Single(f.TargetRoom.Seats);
        await f.ArriveAsync();
        // Both families end on the reply, the only step that leaves the native readiness flag set
        // (kr-812 sub_81C6F0 after sub_81C550; kr-1256 sub_9A6D30 after dispatcher sub_9D2FF0 case 10).
        Assert.Equal(acknowledge ? new[] { "offer", "load", "promote" } : new[] { "offer", "load", "promote", "reply" }, f.Events);
        if (acknowledge)
        {
            Assert.Single(f.TargetRoom.Seats);
            await f.Admission.AcknowledgeAsync(f.Arrived, new(), default);
            Assert.Equal(new[] { "complete", "reply" }, f.Events.TakeLast(2));
        }
        Assert.Equal(2, f.TargetRoom.Seats.Count);
        Assert.Equal(0, f.TargetRoom.PendingAdmissionCount);
        Assert.Single(f.TargetRooms.Entered.To(f.Arrived));
        Assert.True(await f.OwnsAsync(f.Arrived));
        Assert.False(await f.OwnsAsync(f.Source));
        Assert.Single(f.Authority.Holds);
        Assert.Equal(new MigrationHome("a", 50), f.Arrived.Feature<IPlayerSession>().MigrationHome);
        await f.Source.Connection.CloseAsync("client promoted its pending connection");
        await f.Departure.SweepAsync(default);
        await f.SourceAccounts.ReleaseAsync(f.Source);
        Assert.True(await f.OwnsAsync(f.Arrived));
        Assert.Single(f.Authority.Holds);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("timeout")]
    [InlineData("address")]
    [InlineData("duplicate")]
    [InlineData("restart")]
    [InlineData("source-close")]
    public async Task FailurePathsRetainOneWriterAndReleaseReservedSeats(string failure)
    {
        await using var f = await Fixture.StartAsync(acknowledge: true);
        await f.ClickAsync(failure == "password" ? "wrong" : "key");
        if (failure == "password")
        {
            Assert.Equal(RoomJoinRefusal.WrongPassword, Assert.Single(f.SourceRooms.Refused.To(f.Source)).Reason);
            Assert.Empty(f.Events);
        }
        else
        {
            if (failure == "duplicate")
            {
                await f.ClickAsync();
                Assert.Single(f.Events);
            }
            if (failure == "restart") await f.Destination.DisposeAsync();
            if (failure is "restart" or "address")
            {
                if (failure == "address") f.Arrived.Socket.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.23"), 1);
                f.Arrivals.Observe(new(f.Arrived.Connection, "game-b"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admission.MoveAsync(f.Arrived, new(2, 1), default).AsTask());
            }
            if (failure == "source-close")
            {
                await f.Source.Connection.CloseAsync("source lost");
                await f.SourceAccounts.ReleaseAsync(f.Source);
            }
            f.Clock.Advance(31);
            await f.Departure.SweepAsync(default);
            await f.Destination.SweepAsync(default);
        }
        Assert.Single(f.TargetRoom.Seats);
        Assert.Equal(1, f.TargetRoom.OccupantCount);
        Assert.Equal(0, f.TargetRoom.PendingAdmissionCount);
        Assert.Empty(f.TargetRooms.Entered.To(f.Arrived));
        Assert.False(await f.OwnsAsync(f.Arrived));
        Assert.Equal(failure != "source-close", await f.OwnsAsync(f.Source));
        Assert.Equal(failure == "source-close" ? 0 : 1, f.Authority.Holds.Count);
    }

    [Theory]
    [InlineData(false, "start")]
    [InlineData(true, "start")]
    [InlineData(false, "close")]
    [InlineData(true, "close")]
    [InlineData(false, "password")]
    [InlineData(true, "password")]
    public async Task RoomInvalidatedAfterOfferInstallsMenuAndKeepsOnlyTheDestinationWriter(bool acknowledge, string change)
    {
        await using var f = await Fixture.StartAsync(acknowledge);
        await f.ClickAsync();
        if (acknowledge) await f.ArriveAsync();
        lock (f.TargetRoom.Gate)
        {
            if (change == "start") f.TargetRoom.MarkStarting();
            if (change == "close") f.TargetRoom.RemoveSession(1);
            if (change == "password") f.TargetRoom.Password = "changed";
        }
        if (acknowledge) await f.Admission.AcknowledgeAsync(f.Arrived, new(), default);
        else await f.ArriveAsync();
        Assert.False(f.Arrived.Connection.IsClosed);
        Assert.Equal(SessionPhase.MainMenu, f.Arrived.Feature<IPlayerSession>().Phase);
        Assert.Empty(f.TargetRooms.Entered.To(f.Arrived));
        Assert.False(Assert.Single(f.TargetRooms.MainMenu.To(f.Arrived)).OfferTutorial);
        Assert.Equal(0, f.TargetRoom.PendingAdmissionCount);
        Assert.DoesNotContain(f.TargetRoom.Seats, seat => seat.UserNum == 2);
        Assert.True(await f.OwnsAsync(f.Arrived));
        Assert.False(await f.OwnsAsync(f.Source));
        Assert.Single(f.Authority.Holds);
        await f.Source.Connection.CloseAsync("client promoted its pending connection");
        await f.SourceAccounts.ReleaseAsync(f.Source);
        f.Clock.Advance(31);
        await f.Departure.SweepAsync(default);
        await f.Destination.SweepAsync(default);
        Assert.True(await f.OwnsAsync(f.Arrived));
        Assert.Single(f.Authority.Holds);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task ReturningFromRoomOrRecoveryMovesTheOnlyWriterBackHome(bool acknowledge, bool roomLost, bool plaza)
    {
        await using var f = await Fixture.StartAsync(acknowledge, plaza);
        f.EnableHomeReturn();
        await f.ClickAsync();
        if (roomLost) lock (f.TargetRoom.Gate) f.TargetRoom.MarkStarting();
        await f.ArriveAsync();
        if (acknowledge) await f.Admission.AcknowledgeAsync(f.Arrived, new(), default);
        Assert.True(await f.OwnsAsync(f.Arrived));
        f.ArrivalContinuation.Dispose();
        await f.Source.Connection.CloseAsync("client promoted its pending connection");
        await f.SourceAccounts.ReleaseAsync(f.Source);
        await f.Departure.SweepAsync(default);
        if (roomLost) await f.Admission.SweepAsync(default);
        else await f.TargetRooms.Flow.HandleLeaveAsync(f.Arrived, new(), default);
        Assert.Equal("home-offer", f.Events.Last());
        Assert.Empty(f.Authority.Holds);
        Assert.False(await f.OwnsAsync(f.Arrived));
        await f.ArriveHomeAsync();
        Assert.True(await f.OwnsAsync(f.HomeConnection));
        Assert.False(await f.OwnsAsync(f.Source));
        Assert.False(await f.OwnsAsync(f.Arrived));
        Assert.Single(f.Authority.Holds);
        Assert.Equal(plaza && !roomLost ? SessionPhase.Plaza : SessionPhase.MainMenu, f.HomeConnection.Feature<IPlayerSession>().Phase);
        Assert.Equal(new MigrationHome("a", 50), f.HomeConnection.Feature<IPlayerSession>().MigrationHome);
        if (plaza && !roomLost)
        {
            Assert.Single(f.SourceRooms.Plaza.To(f.HomeConnection));
            Assert.Empty(f.SourceRooms.MainMenu.To(f.HomeConnection));
        }
        else
        {
            Assert.False(Assert.Single(f.SourceRooms.MainMenu.To(f.HomeConnection)).OfferTutorial);
            Assert.Empty(f.SourceRooms.Plaza.To(f.HomeConnection));
        }
        Assert.Equal(0, f.TargetRoom.PendingAdmissionCount);
        Assert.DoesNotContain(f.TargetRoom.Seats, seat => seat.UserNum == 2);
        await f.Arrived.Connection.CloseAsync("client returned home");
        await f.TargetAccounts.ReleaseAsync(f.Arrived);
        await f.ReturnDeparture!.SweepAsync(default);
        Assert.True(await f.OwnsAsync(f.HomeConnection));
        Assert.Single(f.Authority.Holds);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly List<string> Events = [];
        internal readonly Authority Authority = new();
        internal readonly Clock Clock = new();
        internal readonly RecordingSession Source = new(2);
        internal readonly IncomingSession Arrived = new(20);
        internal readonly RoomFlowTests.Rig SourceRooms;
        internal readonly RoomFlowTests.Rig TargetRooms;

        private Fixture(bool plaza)
        {
            SourceRooms = new(options: new() { GameProtocol = "test/game", RoomAdmissionReservations = true }, migrationHome: true, plaza: plaza);
            TargetRooms = new(options: new() { GameProtocol = "test/game", RoomAdmissionReservations = true, ReturnDelayMs = 0 }, migrationHome: true, plaza: plaza);
        }
        internal Sessions.PlayRoom TargetRoom = null!;
        internal AccountSessions SourceAccounts = null!;
        internal AccountSessions TargetAccounts = null!;
        internal MigrationSource Departure = null!;
        internal MigrationDestination Destination = null!;
        internal MigrationAdmission Admission = null!;
        internal MigrationArrivals Arrivals = null!;
        internal MigrationBroker A = null!, B = null!;
        private DirectoryServer _server = null!;
        private RoomBrowser _browser = null!;
        private RoomDirectoryClient _discovery = null!;
        private IDisposable _receiver = null!;
        private IDisposable? _homeReceiver;
        private NodeLeaseSource _homeLease = null!, _targetLease = null!;
        private bool _acknowledge;
        internal IDisposable ArrivalContinuation = null!;
        internal readonly IncomingSession HomeConnection = new(30);
        internal MigrationSource? ReturnDeparture;
        private MigrationDestination? _homeDestination;
        private MigrationArrivals? _homeArrivals;
        private MigrationAdmission? _homeAdmission;
        private Task[] _workers = [];
        private readonly CancellationTokenSource _stop = new();
        private int _alias;

        internal static async Task<Fixture> StartAsync(bool acknowledge, bool plaza = false)
        {
            var f = new Fixture(plaza) { _acknowledge = acknowledge };
            try
            {
                var nodes = new[] { "a", "b" }.Select(id => new DirectoryNodeOptions
                {
                    Id = id, Name = id, DataScope = "shared", JoinScope = "shared", RoomDirectories = ["rooms"],
                    NativeMigrationBuilds = ["test"], HeartbeatKey = new string('k', 32) + id,
                    Authentication = new() { AuthorityId = "issuer", ApiUrl = "https://issuer.example", Mode = "master-issued" },
                    Endpoints = [new() { Build = "test", Host = id + ".example", Port = 7100, Capacity = 8 }],
                }).ToArray();
                var options = new DirectoryOptions
                {
                    Listen = "127.0.0.1:0", Migrations = true, LeaseSeconds = 300, Nodes = nodes,
                    Selectors = [new() { Id = 1, Name = "A", Nodes = ["a"] }, new() { Id = 2, Name = "B", Nodes = ["b"] }],
                };
                Assert.Empty(options.Validate());
                var directory = new ServerDirectory(options);
                f._server = await DirectoryServer.StartAsync(options, directory, default);
                var address = f._server.Addresses.Single();
                var leases = nodes.ToDictionary(n => n.Id, n => new NodeLeaseSource(n.Id, address));
                foreach (var node in nodes) leases[node.Id].Publish(directory.Register(node.Id, Guid.NewGuid().ToString("N")));
                f._homeLease = leases["a"]; f._targetLease = leases["b"];
                var roomId = await f.TargetRooms.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"), "key");
                f.TargetRoom = f.TargetRooms.Registry.Find(roomId)!;
                var published = Assert.Single(await f.TargetRooms.Registry.ListPublishedAsync(default));
                foreach (var node in nodes)
                {
                    var lease = leases[node.Id].Current!;
                    FederatedRoom[] rooms = node.Id == "a" ? [] : [new("b", lease.Epoch, "test", roomId, published.Entry.Name, published.Entry.ModeId.ToString(System.Globalization.CultureInfo.InvariantCulture), published.Map, published.Phase, published.Entry.Occupants, published.Entry.Capacity, published.Entry.HasPassword, false)
                        { InstanceId = published.InstanceId, Listing = published.Entry }];
                    Assert.True(directory.Observe(node.Id, new(lease.Token, lease.Epoch, 1, false, new string('a', 32), [new("test", true, 1)], rooms)));
                }
                MigrationBroker Broker(string node) => new(new() { NodeId = node, DirectoryUrl = address, AccessKey = nodes.Single(n => n.Id == node).HeartbeatKey, PollWaitSeconds = 1 },
                    leases[node], new HttpClient { BaseAddress = new(address), Timeout = Timeout.InfiniteTimeSpan }, NullLogger.Instance);
                f.A = Broker("a"); f.B = Broker("b");
                f.Arrivals = new(8, f.Clock);
                f.Destination = new(new() { NodeId = "b", Endpoints = [new() { ClientBuild = "test", ListenerId = "game-b", Host = "b.example", Port = 7100 }] },
                    leases["b"], f.Arrivals, f.Arrivals, [f.TargetRooms.Flow], (_, _) => Task.FromResult(new[] { IPAddress.Loopback }), NullLogger.Instance);
                f._receiver = f.B.Serve(f.Destination.AcceptAsync, f.Destination.CancelAsync);
                f._workers = [f.A.RunAsync(f._stop.Token).AsTask(), f.B.RunAsync(f._stop.Token).AsTask()];
                f.SourceAccounts = new(f.Authority, new(), NullLogger.Instance, handoffStore: f.Authority);
                f.TargetAccounts = new(f.Authority, new(), NullLogger.Instance, handoffStore: f.Authority);
                Assert.True(await f.SourceAccounts.TryAdmitAsync(f.Source, 2, default));
                var player = new PlayerSession(new(2, "guest", "Guest", 1), f.Source.Connection.Id);
                player.MoveTo(SessionPhase.MainMenu);
                f.Source.RegisterFeature<IPlayerSession>(player);
                f.ArrivalContinuation = f.Arrived.RegisterFeature<IInitialHandshakeContinuation>(new Continuation());
                f.Departure = new(new() { ClientBuild = "test", Channel = 50 }, f.A, leases["a"], f.SourceAccounts,
                    new Online(f.Source), null, new Sender<ChannelMoveOffered>(f.Events, "offer"), null, f.Clock, NullLogger.Instance, f.A,
                    new Sender<ServerNotice>(f.Events, "notice"));
                f.Admission = new(new() { Channels = [50] }, new Retained(f.Events), f.Arrivals, leases["b"], f.TargetAccounts,
                    f.TargetAccounts, f.TargetRooms.Flow, new Sender<MoveIntoReplied>(f.Events, "reply"), new Sender<PendingConnectionPromoted>(f.Events, "promote"),
                    acknowledge ? new Sender<PendingPromotionCompleted>(f.Events, "complete") : null, null, f.Clock, NullLogger.Instance, recovery: f.TargetRooms.Flow);
                f._discovery = new(new() { NodeId = "a", DirectoryUrl = address, AccessKey = nodes[0].HeartbeatKey, Builds = ["test"] }, new HttpClient { BaseAddress = new(address) });
                f._browser = new(new() { NodeId = "a", ClientBuild = "test" }, f._discovery);
                f.SourceRooms.Flow.Browser = f._browser;
                f.SourceRooms.Flow.Migration = f.Departure;
                var listing = new RoomFlowTests.Sink<RoomListSnapshot>();
                await f.SourceRooms.Flow.HandleListAsync(f.Source, new(0), listing, default);
                f._alias = Assert.Single(Assert.Single(listing.Sent).Message.Rooms).RoomId;
                Assert.True((await f._browser.ResolveAsync(f.Source, f._alias, default))!.JoinAllowed);
                return f;
            }
            catch
            {
                await f.DisposeAsync();
                throw;
            }
        }
        internal void EnableHomeReturn()
        {
            _homeArrivals = new(8, Clock);
            _homeDestination = new(new() { NodeId = "a", Endpoints = [new() { ClientBuild = "test", ListenerId = "game-a", Host = "a.example", Port = 7100 }] },
                _homeLease, _homeArrivals, _homeArrivals, [SourceRooms.Flow], (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
                NullLogger.Instance, [SourceRooms.Flow]);
            _homeReceiver = A.Serve(_homeDestination.AcceptAsync, _homeDestination.CancelAsync);
            ReturnDeparture = new(new() { ClientBuild = "test", Channel = 50 }, B, _targetLease, TargetAccounts,
                new Online(Arrived), null, new Sender<ChannelMoveOffered>(Events, "home-offer"), null, Clock, NullLogger.Instance, B,
                new Sender<ServerNotice>(Events, "notice"));
            TargetRooms.Flow.Migration = ReturnDeparture;
            _homeAdmission = new(new() { Channels = [50] }, new Retained(Events), _homeArrivals, _homeLease, SourceAccounts,
                SourceAccounts, SourceRooms.Flow, new Sender<MoveIntoReplied>(Events, "home-reply"),
                new Sender<PendingConnectionPromoted>(Events, "home-promote"),
                _acknowledge ? new Sender<PendingPromotionCompleted>(Events, "home-complete") : null, null, Clock, NullLogger.Instance,
                home: SourceRooms.Flow, recovery: SourceRooms.Flow);
            HomeConnection.RegisterFeature<IInitialHandshakeContinuation>(new Continuation());
        }

        internal async Task ArriveHomeAsync()
        {
            _homeArrivals!.Observe(new(HomeConnection.Connection, "game-a"));
            await _homeAdmission!.MoveAsync(HomeConnection, new(2, 1), default);
            if (_acknowledge) await _homeAdmission.AcknowledgeAsync(HomeConnection, new(), default);
        }

        internal ValueTask ClickAsync(string password = "key") => SourceRooms.Flow.HandleJoinAsync(Source, new(_alias, password), default);
        internal ValueTask ArriveAsync()
        {
            Arrivals.Observe(new(Arrived.Connection, "game-b"));
            return Admission.MoveAsync(Arrived, new(2, 1), default);
        }
        internal async Task<bool> OwnsAsync(ISession session) => session.TryFeature<IAccountSession>(out var owner) && await owner.IsHeldAsync(default);
        public async ValueTask DisposeAsync()
        {
            if (Departure is not null) await Departure.DisposeAsync();
            if (ReturnDeparture is not null) await ReturnDeparture.DisposeAsync();
            await _stop.CancelAsync();
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(10));
            _receiver?.Dispose();
            _homeReceiver?.Dispose();
            if (_homeAdmission is not null) await _homeAdmission.DisposeAsync();
            if (_homeDestination is not null) await _homeDestination.DisposeAsync();
            if (Admission is not null) await Admission.DisposeAsync();
            if (Destination is not null) await Destination.DisposeAsync();
            if (SourceAccounts is not null) await SourceAccounts.DisposeAsync();
            if (TargetAccounts is not null) await TargetAccounts.DisposeAsync();
            _browser?.Dispose(); _discovery?.Dispose(); A?.Dispose(); B?.Dispose();
            if (_server is not null) await _server.DisposeAsync();
            await SourceRooms.Flow.StopScenesAsync(); await TargetRooms.Flow.StopScenesAsync();
            await SourceRooms.Registry.StopAllAsync(); await TargetRooms.Registry.StopAllAsync();
            _stop.Dispose();
        }
    }
    private sealed class Retained(List<string> events) : IRetainedPlayerAdmission
    {
        private readonly Dictionary<long, IDisposable> _registrations = [];
        public string Protocol => "test/game";
        public async ValueTask<IPlayerSession> InstallAsync(ISession session, long userNum, CancellationToken cancellationToken)
        {
            Assert.True(await session.Feature<IAccountSession>().IsHeldAsync(cancellationToken));
            events.Add("load");
            var player = new PlayerSession(new(checked((int)userNum), "guest", "Guest", 1), session.Connection.Id);
            _registrations.Add(session.Connection.Id, session.RegisterFeature<IPlayerSession>(player));
            return player;
        }
        public void Remove(ISession session)
        {
            if (_registrations.Remove(session.Connection.Id, out var registration)) registration.Dispose();
        }
    }
    private sealed class Sender<T>(List<string> events, string name) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) { events.Add(name); return ValueTask.CompletedTask; }
    }
    private sealed class Online(ISession session) : IOnlinePlayers
    {
        public ISession? FindByUserNum(int userNum) => userNum == 2 ? session : null;
        public ISession? FindByNickname(string nickname) => session;
    }
    private sealed class Continuation : IInitialHandshakeContinuation
    {
        public void ContinueWithoutVersionVerification() { }
    }
    private sealed class IncomingSession(long id) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => id;
        internal IncomingSocket Socket { get; } = new(id);
        public IConnection Connection => Socket;
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 7100, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class IncomingSocket(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    // Models the shared authority: one hold per account and one handoff row per account.
    private sealed class Authority : IAccountOwnershipStore, IAccountHandoffStore
    {
        private readonly Lock _gate = new();
        internal ConcurrentDictionary<long, Hold> Holds { get; } = new();
        internal Dictionary<long, (string Migration, string Destination, string State)> Rows { get; } = [];

        public ValueTask<IAccountOwnership?> TryAcquireAsync(long userNum, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Rows.TryGetValue(userNum, out var row) && row.State == "pending") return ValueTask.FromResult<IAccountOwnership?>(null);
                return ValueTask.FromResult(Take(userNum));
            }
        }

        public ValueTask<IAccountOwnership?> TryClaimAsync(long userNum, string migrationId, CancellationToken cancellationToken) =>
            Settle(userNum, migrationId, "claimed");

        public ValueTask<IAccountOwnership?> TryWithdrawAsync(long userNum, string migrationId, CancellationToken cancellationToken) =>
            Settle(userNum, migrationId, "withdrawn");

        public async ValueTask HandOffAsync(IAccountOwnership ownership, AccountHandoff handoff, CancellationToken cancellationToken)
        {
            try
            {
                lock (_gate) Rows[handoff.UserNum] = (handoff.MigrationId, handoff.DestinationNode, "pending");
            }
            finally
            {
                await ownership.DisposeAsync();
            }
        }

        private ValueTask<IAccountOwnership?> Settle(long userNum, string migrationId, string state)
        {
            lock (_gate)
            {
                if (!Rows.TryGetValue(userNum, out var row) || row.State != "pending" || row.Migration != migrationId
                    || Holds.ContainsKey(userNum)) return ValueTask.FromResult<IAccountOwnership?>(null);
                Rows[userNum] = row with { State = state };
                return ValueTask.FromResult(Take(userNum));
            }
        }

        private IAccountOwnership? Take(long userNum)
        {
            var hold = new Hold(this, userNum);
            return Holds.TryAdd(userNum, hold) ? hold : null;
        }
    }

    private sealed class Hold(Authority authority, long userNum) : IAccountOwnership
    {
        private bool _released;
        public ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken) => ValueTask.FromResult(!_released);
        public ValueTask DisposeAsync()
        {
            _released = true;
            authority.Holds.TryRemove(new KeyValuePair<long, Hold>(userNum, this));
            return ValueTask.CompletedTask;
        }
    }

}
