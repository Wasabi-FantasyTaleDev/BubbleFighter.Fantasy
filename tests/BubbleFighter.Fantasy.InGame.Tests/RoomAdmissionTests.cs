using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class RoomAdmissionTests
{
    [Theory]
    [InlineData(SessionPhase.Plaza, true, SessionPhase.Plaza)]
    [InlineData(SessionPhase.MainMenu, true, SessionPhase.MainMenu)]
    [InlineData(SessionPhase.Plaza, false, SessionPhase.MainMenu)]
    [InlineData(SessionPhase.Room, true, SessionPhase.Authenticated)]
    public async Task RetainedHomeRestoresOnlyTheRequestedIdleScene(SessionPhase requested, bool plazaBound, SessionPhase expected)
    {
        var options = new InGameOptions { GameProtocol = "test/game", RoomAdmissionReservations = true, HomeMigrationAdmission = true };
        var registry = new RoomRegistry(options);
        var menu = new RoomFlowTests.Sink<MainMenuRoomOpened>();
        var plaza = new RoomFlowTests.Sink<PlazaEntered>();
        var flow = new BubbleFighter.Fantasy.InGame.Handlers.RoomFlow(registry,
            new(null, null, null, null, MainMenu: menu, PlazaEntered: plazaBound ? plaza : null,
                PlazaMemberEntered: plazaBound ? new RoomFlowTests.Sink<PlazaMemberEntered>() : null,
                PlazaPositionReceived: plazaBound ? new RoomFlowTests.Sink<PlazaPositionReceived>() : null),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, options: options);
        try
        {
            var session = Player(9, loggedIn: false);
            var admission = (IMigrationHomeAdmission)flow;
            Assert.Equal(expected != SessionPhase.Authenticated, await admission.EnterAsync(session, requested, default));
            Assert.Equal(expected, session.Feature<IPlayerSession>().Phase);
            if (expected == SessionPhase.Plaza)
            {
                var installed = Assert.Single(plaza.Sent).Message;
                Assert.Equal(37, installed.You.Level);
                Assert.Empty(menu.Sent);
            }
            else if (expected == SessionPhase.MainMenu)
            {
                Assert.False(Assert.Single(menu.Sent).Message.OfferTutorial);
                Assert.Empty(plaza.Sent);
            }
            else { Assert.Empty(menu.Sent); Assert.Empty(plaza.Sent); }
            Assert.False(await admission.EnterAsync(session, requested, default));
        }
        finally { await flow.StopScenesAsync(); await registry.StopAllAsync(); }
    }

    [Fact]
    public async Task RetainedHomeUsesTheMainMenuInstallerWithoutOfferingTheTutorial()
    {
        var options = new InGameOptions { GameProtocol = "test/game", RoomAdmissionReservations = true, HomeMigrationAdmission = true };
        var registry = new RoomRegistry(options);
        var menu = new RoomFlowTests.Sink<BubbleFighter.Fantasy.Contracts.Lobby.MainMenuRoomOpened>();
        var flow = new BubbleFighter.Fantasy.InGame.Handlers.RoomFlow(registry,
            new(null, null, null, null, MainMenu: menu), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, options: options);
        try
        {
            var session = Player(9, loggedIn: false);
            Assert.True(await ((IMigrationHomeAdmission)flow).EnterAsync(session, SessionPhase.MainMenu, default));
            var installed = Assert.Single(menu.Sent).Message;
            Assert.Equal(2, installed.UserNumber);
            Assert.Equal(37, installed.Level);
            Assert.False(installed.OfferTutorial);
            Assert.Equal(SessionPhase.MainMenu, session.Feature<IPlayerSession>().Phase);
            Assert.False(await ((IMigrationHomeAdmission)flow).EnterAsync(session, SessionPhase.MainMenu, default));
            Assert.Single(menu.Sent);
        }
        finally { await flow.StopScenesAsync(); await registry.StopAllAsync(); }
    }

    private static RoomFlowTests.Rig Rig(int seats = 2, TimeProvider? clock = null) => new(options: new InGameOptions
    {
        GameProtocol = "test/game", MaxPlayersPerMatch = seats,
        RoomAdmissionReservations = true, RoomAdmissionLifetimeMs = 1000,
    }, admissionClock: clock);

    private static RecordingSession Player(long id, int user = 2, long? owner = null, string build = "test",
        bool loggedIn = true)
    {
        var session = new RecordingSession(id, new(build, "kr", 1256, 0, 0, "test"));
        var player = new PlayerSession(new(user, "guest", "Guest", 37), owner ?? session.Connection.Id);
        if (loggedIn) player.MoveTo(SessionPhase.MainMenu);
        session.RegisterFeature<IPlayerSession>(player);
        return session;
    }

    private static async Task<(PlayRoom Room, RoomAdmissionReservation Hold)> Prepare(RoomFlowTests.Rig rig, string password = "key")
    {
        var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"), password);
        var room = rig.Registry.Find(id)!;
        var result = await ((IRoomAdmissionDirectory)rig.Flow).PrepareAsync(new(id, room.InstanceId, 2, password), default);
        Assert.Null(result.Refusal);
        return (room, Assert.IsType<RoomAdmissionReservation>(result.Reservation));
    }

    [Fact]
    public async Task ReservedLastSeatSurvivesOrdinaryJoinAndUsesNormalEntryAndPeerAnnouncements()
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(3, "Other"), new(room.Id, "key"), default);
            Assert.Equal(RoomJoinRefusal.Full, Assert.Single(rig.Refused.Sent).Message.Reason);
            Assert.Single(room.Seats);
            var guest = Player(20);
            Assert.True(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(guest, hold, default));
            var entered = Assert.Single(rig.Entered.To(guest));
            Assert.Equal(room.Id, entered.Room.RoomId);
            Assert.Equal(37, Assert.Single(entered.Room.Seats, member => member.UserNum == 2).Level);
            Assert.Equal(SessionPhase.Room, guest.Feature<IPlayerSession>().Phase);
            Assert.Single(rig.SeatUpdated.Sent, update => update.Message.Seat.UserNum == 2);
            Assert.Equal(0, room.PendingAdmissionCount);
            Assert.False(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(Player(21), hold, default));
            Assert.Equal(2, room.Seats.Count);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData("password", RoomJoinRefusal.WrongPassword)]
    [InlineData("creation", RoomJoinRefusal.NoRoom)]
    [InlineData("hidden", RoomJoinRefusal.NoRoom)]
    [InlineData("training", RoomJoinRefusal.NoRoom)]
    [InlineData("playing", RoomJoinRefusal.Started)]
    [InlineData("closed", RoomJoinRefusal.NoRoom)]
    public async Task PreparationUsesAuthoritativeRoomIdentityPasswordAndLifecycle(string scenario, RoomJoinRefusal refusal)
    {
        var rig = Rig();
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"), "key");
            var room = rig.Registry.Find(id)!;
            lock (room.Gate)
            {
                if (scenario == "hidden") room.Listed = false;
                if (scenario == "training") room.ModeId = RoomRegistry.TrainingModeId;
                if (scenario == "playing") room.MarkPlaying();
                if (scenario == "closed") room.RemoveSession(1);
            }
            var request = new RoomAdmissionRequest(id, scenario == "creation" ? Guid.NewGuid() : room.InstanceId,
                2, scenario == "password" ? "wrong" : "key");
            var result = await ((IRoomAdmissionDirectory)rig.Flow).PrepareAsync(request, default);
            Assert.Equal(refusal, result.Refusal);
            Assert.Null(result.Reservation);
            Assert.Equal(0, room.PendingAdmissionCount);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task DuplicateAccountCannotAccumulateReservationsAndCancellationDoesNotReleaseAnotherHold()
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            var directory = (IRoomAdmissionDirectory)rig.Flow;
            var duplicate = await directory.PrepareAsync(new(room.Id, room.InstanceId, 2, "key"), default);
            Assert.Equal(RoomJoinRefusal.NoRoom, duplicate.Refusal);
            await directory.CancelAsync(hold with { UserNumber = 3 }, default);
            Assert.Equal(1, room.PendingAdmissionCount);
            await directory.CancelAsync(hold, default);
            await directory.CancelAsync(hold, default);
            Assert.Equal(0, room.PendingAdmissionCount);
            await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(3, "Other"), new(room.Id, "key"), default);
            Assert.Equal(2, room.Seats.Count);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData("account")]
    [InlineData("relay")]
    [InlineData("build")]
    [InlineData("login")]
    [InlineData("anonymous")]
    public async Task ReservationDoesNotAuthenticateANativeConnection(string scenario)
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            var directory = (IRoomAdmissionDirectory)rig.Flow;
            var session = scenario == "anonymous" ? new RecordingSession(20) : Player(20,
                user: scenario == "account" ? 3 : 2, owner: scenario == "relay" ? 99 : null,
                build: scenario == "build" ? "other" : "test", loggedIn: scenario != "login");
            Assert.False(await directory.JoinAsync(session, hold, default));
            Assert.Equal(1, room.PendingAdmissionCount);
            Assert.Single(room.Seats);
            Assert.True(await directory.JoinAsync(Player(21), hold, default));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("room")]
    [InlineData("creation")]
    [InlineData("account")]
    public async Task ForgedReservationCannotConsumeOrCancelAnExistingSeat(string scenario)
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            var changed = scenario switch
            {
                "id" => hold with { Id = Guid.NewGuid() },
                "room" => hold with { RoomId = room.Id + 1 },
                "creation" => hold with { InstanceId = Guid.NewGuid() },
                _ => hold with { UserNumber = 3 },
            };
            var directory = (IRoomAdmissionDirectory)rig.Flow;
            Assert.False(await directory.JoinAsync(Player(20, changed.UserNumber), changed, default));
            Assert.Equal(1, room.PendingAdmissionCount);
            Assert.True(await directory.JoinAsync(Player(21), hold, default));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task MonotonicExpiryReleasesCapacityWithoutATimerOrAClientRetry()
    {
        var clock = new Clock();
        var rig = Rig(clock: clock);
        try
        {
            var (room, hold) = await Prepare(rig);
            clock.Advance(999);
            Assert.True(room.IsFull);
            clock.Advance(1);
            Assert.False(room.IsFull);
            Assert.Equal(0, room.PendingAdmissionCount);
            Assert.False(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(Player(20), hold, default));
            await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(3, "Other"), new(room.Id, "key"), default);
            Assert.Equal(2, room.Seats.Count);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("password")]
    [InlineData("host-left")]
    public async Task RoomChangesInvalidatePreparedAdmission(string scenario)
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            lock (room.Gate)
            {
                if (scenario == "start") { room.MarkStarting(); room.ResetToWaiting(); }
                if (scenario == "password") room.Password = "changed";
                if (scenario == "host-left") room.RemoveSession(1);
            }
            Assert.False(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(Player(20), hold, default));
            Assert.Equal(0, room.PendingAdmissionCount);
            Assert.DoesNotContain(room.Seats, seat => seat.UserNum == 2);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task PreparationIsSerializedAfterTheRoomOperationAndRechecksItsPhase()
    {
        var rig = Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"));
            var room = rig.Registry.Find(id)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrier = room.Executor.ExecuteAsync(async ct =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                lock (room.Gate) room.MarkStarting();
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prepare = ((IRoomAdmissionDirectory)rig.Flow).PrepareAsync(new(id, room.InstanceId, 2, ""), default).AsTask();
            Assert.False(prepare.IsCompleted);
            release.SetResult();
            await barrier;
            Assert.Equal(RoomJoinRefusal.Started, (await prepare).Refusal);
            Assert.Equal(0, room.PendingAdmissionCount);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task SceneChangeWhileWaitingForRoomExecutorRevokesThePreparedConnection()
    {
        var rig = Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var (room, hold) = await Prepare(rig);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrier = room.Executor.ExecuteAsync(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var guest = Player(20);
            var join = ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(guest, hold, default).AsTask();
            Assert.False(join.IsCompleted);
            guest.Feature<IPlayerSession>().MoveTo(SessionPhase.Plaza);
            release.SetResult();
            await barrier;
            Assert.False(await join);
            Assert.Equal(0, room.PendingAdmissionCount);
            Assert.Single(room.Seats);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task ShutdownReleasesHoldsAndRefusesNewPreparations()
    {
        var rig = Rig();
        var (room, hold) = await Prepare(rig);
        await rig.Registry.StopAllAsync();
        Assert.Equal(0, room.PendingAdmissionCount);
        var directory = (IRoomAdmissionDirectory)rig.Flow;
        Assert.False(await directory.JoinAsync(Player(20), hold, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.PrepareAsync(new(room.Id, room.InstanceId, 2, "key"), default).AsTask());
        await directory.CancelAsync(hold, default);
    }

    [Fact]
    public async Task ClosingAPendingSeatRevokesItsHoldWithoutTreatingItAsAnOccupiedPlayer()
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            lock (room.Gate) Assert.True(room.SetSlotClosed(1, true));
            Assert.Equal(0, room.PendingAdmissionCount);
            Assert.False(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(Player(20), hold, default));
            Assert.Single(room.Seats);
            Assert.Equal(new[] { 1 }, room.ClosedSlots);
            lock (room.Gate) Assert.True(room.SetSlotClosed(1, false));
            var replacement = await ((IRoomAdmissionDirectory)rig.Flow).PrepareAsync(new(room.Id, room.InstanceId, 2, "key"), default);
            Assert.NotNull(replacement.Reservation);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotReserveASeat()
    {
        var rig = Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"));
            var room = rig.Registry.Find(id)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrier = room.Executor.ExecuteAsync(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var prepare = ((IRoomAdmissionDirectory)rig.Flow).PrepareAsync(new(id, room.InstanceId, 2, ""), cancellation.Token).AsTask();
            Assert.False(prepare.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
            release.SetResult();
            await barrier;
            await room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            Assert.Equal(0, room.PendingAdmissionCount);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task StandaloneDoesNotExposeActiveAdmissionPreparation()
    {
        var rig = new RoomFlowTests.Rig();
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"));
            var room = rig.Registry.Find(id)!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => ((IRoomAdmissionDirectory)rig.Flow)
                .PrepareAsync(new(id, room.InstanceId, 2, ""), default).AsTask());
            Assert.Equal(0, room.PendingAdmissionCount);
            await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(2, "Guest"), new(id, ""), default);
            Assert.Equal(2, room.Seats.Count);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public void RequestFormattingDoesNotExposeThePassword() =>
        Assert.DoesNotContain("secret", new RoomAdmissionRequest(1, Guid.NewGuid(), 2, "secret").ToString());

    [Fact]
    public async Task FailedRoomInstallationClosesOnlyItsDestinationConnectionAndReleasesTheOccupiedSeat()
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            rig.Entered.Failure = new IOException("room installation failed");
            var guest = Player(20);
            await Assert.ThrowsAsync<IOException>(() => ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(guest, hold, default).AsTask());
            Assert.True(guest.Connection.IsClosed);
            var host = Assert.Single(room.Seats);
            Assert.False(host.Game.Connection.IsClosed);
            Assert.Null(rig.Registry.TryGet(guest.Id));
            Assert.Equal(0, room.PendingAdmissionCount);
            Assert.Single(rig.SeatVacated.Sent, vacancy => vacancy.Message.Seat.UserNum == 2);
            Assert.False(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(Player(21), hold, default));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task PeerSendFailureDoesNotUndoThePreparedJoinOrCloseTheJoiningConnection()
    {
        var rig = Rig();
        try
        {
            var (room, hold) = await Prepare(rig);
            var host = Assert.Single(room.Seats).Game;
            rig.SeatUpdated.Failure = new IOException("peer announcement failed");
            var guest = Player(20);
            Assert.True(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(guest, hold, default));
            Assert.False(guest.Connection.IsClosed);
            Assert.True(host.Connection.IsClosed);
            Assert.Same(room, rig.Registry.TryGet(guest.Id));
            Assert.Equal(0, room.PendingAdmissionCount);
            Assert.False(await ((IRoomAdmissionDirectory)rig.Flow).JoinAsync(Player(21), hold, default));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(int milliseconds) => _timestamp += milliseconds;
    }
}
