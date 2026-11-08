using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class RoomBrowserFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteClickPassesTheFreshIdentityAndPasswordToMigration(bool refused)
    {
        var rig = new RoomFlowTests.Rig();
        try
        {
            var browser = new Browser { Selection = new(false, "remote", 9, 12, Guid.NewGuid(), true) };
            var migration = new Migration { Refusal = refused ? RoomJoinRefusal.WrongPassword : null };
            rig.Flow.Browser = browser;
            rig.Flow.Migration = migration;
            var player = RoomFlowTests.Rig.Player(2, "Guest");
            await rig.Flow.HandleJoinAsync(player, new(99, "key"), default);
            Assert.Same(browser.Selection, migration.Selection);
            Assert.Equal("key", migration.Password);
            Assert.Empty(rig.Entered.To(player));
            Assert.Null(rig.Registry.TryGet(player.Id));
            if (refused) Assert.Equal(RoomJoinRefusal.WrongPassword, Assert.Single(rig.Refused.To(player)).Reason);
            else Assert.Empty(rig.Refused.To(player));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task HomeDepartureRunsOnlyAfterNormalRoomRemovalAndRoundLeaveHook()
    {
        var rig = new RoomFlowTests.Rig(options: new() { ReturnDelayMs = 0 });
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "Host");
            var guest = MigratedPlayer();
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            var room = rig.Registry.Find(id)!;
            await rig.Flow.HandleReadyAsync(guest, new(true), default);
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), default);
            Assert.Equal(BubbleFighter.Fantasy.InGame.Sessions.RoomPhase.Starting, room.Phase);
            var settled = false;
            rig.Flow.SeatLeftDuringRound = (_, _) => { settled = true; return ValueTask.CompletedTask; };
            var migration = new Migration { OnReturn = session =>
            {
                Assert.True(settled);
                Assert.Null(rig.Registry.TryGet(session.Id));
                Assert.Single(rig.Left.To(session));
                Assert.Equal(BubbleFighter.Fantasy.Contracts.Sessions.SessionPhase.Plaza,
                    session.Feature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>().Phase);
            } };
            rig.Flow.Migration = migration;
            await rig.Flow.HandleLeaveAsync(guest, new(), default);
            Assert.Equal(1, migration.Returns);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static RecordingSession MigratedPlayer()
    {
        var session = new RecordingSession(2);
        var player = new PlayerSession(new(2, "guest", "Guest", 1), session.Connection.Id);
        session.RegisterFeature<IPlayerSession>(player);
        player.SetMigrationHome(new("home", 50));
        player.MoveTo(SessionPhase.MainMenu);
        return session;
    }

    [Fact]
    public async Task StayPolicyLeavesTheVisitorOnTheCurrentNode()
    {
        var rig = new RoomFlowTests.Rig(options: new() { ReturnOnLeave = "stay" });
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"));
            var guest = MigratedPlayer();
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            var migration = new Migration();
            rig.Flow.Migration = migration;
            await rig.Flow.HandleLeaveAsync(guest, new(), default);
            Assert.Null(rig.Registry.TryGet(guest.Id));
            Assert.Equal(0, migration.Returns);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SceneChangesDuringTheHomeDelayCancelTheOldReturn(bool leaveAgain)
    {
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = 0;
        var rig = new RoomFlowTests.Rig(autoStartDelay: async (delay, ct) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(1500), delay);
            if (Interlocked.Increment(ref delays) != 1) return;
            delayed.SetResult();
            await resume.Task.WaitAsync(ct);
        });
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"));
            var guest = MigratedPlayer();
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            var migration = new Migration();
            rig.Flow.Migration = migration;
            var leaving = rig.Flow.HandleLeaveAsync(guest, new(), default).AsTask();
            await delayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(rig.Registry.TryGet(guest.Id));
            if (leaveAgain) await rig.Flow.HandleLeaveAsync(guest, new(), default);
            resume.SetResult();
            await leaving;
            Assert.Equal(leaveAgain ? 1 : 0, migration.Returns);
        }
        finally { resume.TrySetResult(); await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData("home", false)]
    [InlineData("stay", false)]
    [InlineData("home", true)]
    public async Task RecoveredMenuUsesReturnPolicyAndCancelsChangedScenes(string policy, bool changeScene)
    {
        var rig = new RoomFlowTests.Rig(options: new() { ReturnOnLeave = policy, ReturnDelayMs = 0,
            GameProtocol = "test/game", RoomAdmissionReservations = true }, migrationHome: true);
        try
        {
            var guest = MigratedPlayer();
            var recovery = (IMigrationRoomRecovery)rig.Flow;
            var migration = new Migration();
            rig.Flow.Migration = migration;
            Assert.True(await recovery.InstallMenuAsync(guest, default));
            Assert.Single(rig.MainMenu.To(guest));
            var generation = guest.Feature<IPlayerSession>().Generation;
            if (changeScene) guest.Feature<IPlayerSession>().MoveTo(SessionPhase.Plaza);
            await recovery.ReturnHomeAsync(guest, generation, default);
            Assert.Equal(policy == "home" && !changeScene ? 1 : 0, migration.Returns);
            Assert.False(guest.Connection.IsClosed);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private sealed class Migration : BubbleFighter.Fantasy.Contracts.Cluster.IRemoteRoomMigration
    {
        public string ClientBuild => "test";
        internal RoomBrowserSelection? Selection;
        internal string? Password;
        internal RoomJoinRefusal? Refusal;
        internal Action<ISession>? OnReturn;
        internal int Returns;
        public ValueTask ReturnHomeAsync(ISession session, CancellationToken cancellationToken)
        {
            Returns++;
            OnReturn?.Invoke(session);
            return ValueTask.CompletedTask;
        }
        public ValueTask<RoomJoinRefusal?> BeginAsync(ISession session, RoomBrowserSelection selection, string password, CancellationToken cancellationToken)
        {
            Selection = selection;
            Password = password;
            return ValueTask.FromResult(Refusal);
        }
    }

    [Fact]
    public async Task NativeListUsesBrowserRowsAndPreservesTheRequestedCursor()
    {
        var rig = new RoomFlowTests.Rig();
        try
        {
            await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"));
            var browser = new Browser();
            rig.Flow.Browser = browser;
            var sink = new RoomFlowTests.Sink<RoomListSnapshot>();
            await rig.Flow.HandleListAsync(RoomFlowTests.Rig.Player(2, "Guest"), new(17), sink, default);
            Assert.Single(browser.LocalRooms!);
            var reply = Assert.Single(sink.Sent).Message;
            Assert.Equal(17, reply.Cursor);
            Assert.Equal(99, Assert.Single(reply.Rooms).RoomId);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryOrSendFailureInvalidatesBrowserVisibility(bool sendFailure)
    {
        var rig = new RoomFlowTests.Rig();
        try
        {
            var browser = new Browser { Failure = sendFailure ? null : new("unavailable", new IOException()) };
            rig.Flow.Browser = browser;
            var sink = new RoomFlowTests.Sink<RoomListSnapshot> { Failure = sendFailure ? new IOException("send failed") : null };
            var operation = rig.Flow.HandleListAsync(RoomFlowTests.Rig.Player(2, "Guest"), new(17), sink, default).AsTask();
            if (sendFailure) await Assert.ThrowsAsync<IOException>(() => operation);
            else { await operation; Assert.Empty(Assert.Single(sink.Sent).Message.Rooms); }
            Assert.Equal(1, browser.Invalidations);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task LocalAliasResolvesToTheCurrentNativeRoomAndUsesOrdinaryAdmission()
    {
        var rig = new RoomFlowTests.Rig();
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"), "key");
            var room = rig.Registry.Find(id)!;
            var browser = new Browser { Selection = new(true, "a", 0, id, room.InstanceId, true) };
            rig.Flow.Browser = browser;
            var guest = RoomFlowTests.Rig.Player(2, "Guest");
            await rig.Flow.HandleJoinAsync(guest, new(99, "wrong"), default);
            Assert.Equal(RoomJoinRefusal.WrongPassword, Assert.Single(rig.Refused.To(guest)).Reason);
            Assert.Null(rig.Registry.TryGet(guest.Id));
            await rig.Flow.HandleJoinAsync(guest, new(99, "key"), default);
            Assert.Equal(99, browser.LastAlias);
            Assert.Same(room, rig.Registry.TryGet(guest.Id));
            Assert.Equal(id, Assert.Single(rig.Entered.To(guest)).Room.RoomId);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("remote")]
    [InlineData("recreated")]
    [InlineData("unavailable")]
    public async Task UnresolvedRemoteOrRecreatedAliasesNeverFallBackToACollidingLocalRoom(string kind)
    {
        var rig = new RoomFlowTests.Rig();
        try
        {
            var id = await rig.CreateAsync(RoomFlowTests.Rig.Player(1, "Host"));
            var room = rig.Registry.Find(id)!;
            var browser = new Browser
            {
                Selection = kind == "missing" ? null : new(kind != "remote", "a", 1, id,
                    kind == "recreated" ? Guid.NewGuid() : room.InstanceId, true),
                Failure = kind == "unavailable" ? new("unavailable", new IOException()) : null,
            };
            rig.Flow.Browser = browser;
            var guest = RoomFlowTests.Rig.Player(2, "Guest");
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            Assert.Null(rig.Registry.TryGet(guest.Id));
            Assert.Empty(rig.Entered.To(guest));
            Assert.Equal(RoomJoinRefusal.NoRoom, Assert.Single(rig.Refused.To(guest)).Reason);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private sealed class Browser : IRoomBrowser
    {
        public string ClientBuild => "test";
        internal IReadOnlyList<PublishedRoomSnapshot>? LocalRooms { get; private set; }
        internal RoomBrowserSelection? Selection { get; set; }
        internal RoomBrowserUnavailableException? Failure { get; set; }
        internal int Invalidations { get; private set; }
        internal int LastAlias { get; private set; }
        public ValueTask<IReadOnlyList<RoomListEntry>> ListAsync(ISession session,
            IReadOnlyList<PublishedRoomSnapshot> localRooms, CancellationToken cancellationToken)
        {
            if (Failure is { } failure) throw failure;
            LocalRooms = localRooms;
            return ValueTask.FromResult<IReadOnlyList<RoomListEntry>>([new(99, "Remote", "Master", 3, 1, 8, false)]);
        }
        public ValueTask<RoomBrowserSelection?> ResolveAsync(ISession session, int alias, CancellationToken cancellationToken)
        {
            if (Failure is { } failure) throw failure;
            LastAlias = alias;
            return ValueTask.FromResult(Selection);
        }
        public void Invalidate(ISession session) => Invalidations++;
    }
}
