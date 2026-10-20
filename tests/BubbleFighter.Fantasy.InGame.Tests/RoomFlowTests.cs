using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The typed room flow: what each member is sent, asserted as messages rather than one build's bytes.</summary>
public sealed class RoomFlowTests
{
    internal sealed class Sink<T> : IMessageSender<T> where T : IMessage
    {
        public List<(long Session, T Message)> Sent { get; } = [];
        public string Protocol => "test/game";
        internal Exception? Failure { get; set; }
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (Failure is { } failure) throw failure;
            lock (Sent)
                Sent.Add((session.Id, message));
            return ValueTask.CompletedTask;
        }
        public T[] To(ISession session)
        {
            lock (Sent)
                return [.. Sent.Where(row => row.Session == session.Id).Select(row => row.Message)];
        }
    }

    internal sealed class Rig
    {
        public RoomRegistry Registry { get; }
        public RoomFlow Flow { get; }
        public Sink<RoomEntered> Entered { get; } = new();
        public Sink<RoomSeatUpdated> SeatUpdated { get; } = new();
        public Sink<RoomSeatVacated> SeatVacated { get; } = new();
        public Sink<RoomMasterChanged> MasterChanged { get; } = new();
        public Sink<RoomMasterChangeReplied> MasterReplies { get; } = new();
        public Sink<RoomJoinRefused> Refused { get; } = new();
        public Sink<RoomLeft> Left { get; } = new();
        public Sink<RoomMatchStarting> Starting { get; } = new();
        public Sink<RoomSettingsChanged> SettingsChanged { get; } = new();
        public Sink<RoomUiChanged> UiChanged { get; } = new();
        public Sink<RoomProbeAcknowledged> ProbeAcknowledged { get; } = new();
        public Sink<RoomChatReceived> Chat { get; } = new();
        public Sink<RoomSeatLockChanged> SeatLock { get; } = new();
        public Sink<RoomMemberKicked> Kicked { get; } = new();
        public Sink<RoomAutoStartNotified> AutoStart { get; } = new();
        public Sink<ServerNotice> Notices { get; } = new();
        public Sink<RoomMatchNotified> MatchNotifications { get; } = new();
        public Sink<BubbleFighter.Fantasy.Contracts.Match.RoundRescueMissionsPrepared> RescuePrepared { get; } = new();

        public RoomQuickMatcher Matching(InGameOptions options, CancellationToken lifetime = default) =>
            new(Registry, Flow, options, MatchNotifications, NullLogger.Instance, lifetime);

        public Rig(int seats = 16, InGameOptions? options = null, bool stageProbes = false,
            Func<long>? autoStartNow = null, Func<TimeSpan, CancellationToken, ValueTask>? autoStartDelay = null)
        {
            Registry = new RoomRegistry(options ?? new InGameOptions { TypedOnly = true, MaxPlayersPerMatch = seats });
            Flow = new RoomFlow(Registry, new RoomSenders(Entered, SeatUpdated, SeatVacated, MasterChanged, Refused, Left, Starting,
                Chat: Chat, SeatLock: SeatLock, Kicked: Kicked, AutoStart: AutoStart, Notices: Notices,
                RescuePrepared: RescuePrepared, SettingsChanged: SettingsChanged,
                UiChanged: stageProbes ? UiChanged : null, ProbeAcknowledged: stageProbes ? ProbeAcknowledged : null,
                MasterChangeReply: MasterReplies),
                NullLogger.Instance, null, null, 36, options, default, new TestTexts(), null, null,
                autoStartNow ?? (() => Environment.TickCount64),
                autoStartDelay ?? ((delay, ct) => new(Task.Delay(delay, ct))));
        }

        public static RoomPushTests.RecordingSession Player(long id, string nickname)
        {
            var session = new RoomPushTests.RecordingSession(id);
            session.SetLobbyPlayer(id, nickname, level: 3);
            return session;
        }

        public async Task<int> CreateAsync(ISession host, string password = "")
        {
            await Flow.HandleCreateAsync(host, new RoomCreateRequested("room", password, 4, "rel_Village07_Easy02"), CancellationToken.None);
            return Entered.To(host)[^1].Room.RoomId;
        }
    }

    internal sealed class TestTexts : IGameText
    {
        public string Format(string region, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
            key + ":" + (arguments?.GetValueOrDefault("seconds") ?? arguments?.GetValueOrDefault("mode")
                ?? arguments?.GetValueOrDefault("id") ?? "");

        public bool TryFormat(string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null)
        {
            text = key == "mode.8" ? "Zombie Battle" : "";
            return text.Length > 0;
        }
    }

    /// <summary>Reusing a free low seat must not rewrite the insertion order retained by the shared snapshot.</summary>
    [Fact]
    public async Task SnapshotsKeepMembershipOrderSeparateFromSeatOrder()
    {
        var rig = new Rig();
        var host = Rig.Player(921, "host");
        var first = Rig.Player(922, "first");
        var second = Rig.Player(923, "second");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(first, new(id, ""), default);
            await rig.Flow.HandleJoinAsync(second, new(id, ""), default);
            await rig.Flow.HandleLeaveAsync(first, new(), default);
            await rig.Flow.HandleJoinAsync(first, new(id, ""), default);
            var snapshot = rig.Entered.To(first)[^1].Room;
            Assert.Equal(new[] { 0, 1, 2 }, snapshot.Seats.Select(seat => seat.Slot));
            Assert.Equal(new[] { 0, 2, 1 }, snapshot.MemberOrder);
            await rig.Flow.HandleReadyAsync(first, new(true), default);
            Assert.Equal(new[] { 0, 2, 1 }, rig.SeatUpdated.To(host)[^1].Room!.MemberOrder);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task RoomOnlyStartIsRecordedOnceWithoutStartingOrSendingAnything()
    {
        var rig = new Rig();
        var host = Rig.Player(901, "host");
        var guest = Rig.Player(902, "guest");
        var outsider = Rig.Player(903, "outsider");
        var id = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
        var room = rig.Registry.TryGet(host.Id)!;
        var seats = room.Seats.ToArray();
        for (var i = 0; i < 3; i++)
            await rig.Flow.HandleStartUnavailableAsync(i == 0 ? host : guest, new(), default);
        Assert.True(room.StartUnavailableLogged);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
        Assert.Equal(seats, room.Seats);
        Assert.Empty(rig.Starting.Sent);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Flow.HandleStartUnavailableAsync(outsider, new(), default));
    }

    [Fact]
    public async Task RoomOnlyStartMarkerBelongsToTheRoomLifetime()
    {
        var rig = new Rig();
        var host = Rig.Player(911, "host");
        await rig.CreateAsync(host);
        var first = rig.Registry.TryGet(host.Id)!;
        await rig.Flow.HandleStartUnavailableAsync(host, new(), default);
        await rig.Flow.HandleLeaveAsync(host, new(), default);
        await rig.CreateAsync(host);
        var second = rig.Registry.TryGet(host.Id)!;
        Assert.NotSame(first, second);
        Assert.False(second.StartUnavailableLogged);
        await rig.Flow.HandleStartUnavailableAsync(host, new(), default);
        Assert.True(second.StartUnavailableLogged);
        Assert.Empty(rig.Starting.Sent);
    }

    [Fact]
    public async Task QuickMatchingJoinsTheSelectedModeAndIgnoresLockedAndRunningRooms()
    {
        var rig = new Rig();
        var wrongMode = Rig.Player(301, "wrong");
        var locked = Rig.Player(302, "locked");
        var running = Rig.Player(303, "running");
        var available = Rig.Player(304, "available");
        var joiner = Rig.Player(305, "joiner");
        await rig.Flow.HandleCreateAsync(wrongMode, new("wrong", "", 3, "Random"), default);
        await rig.CreateAsync(locked, "secret");
        var runningId = await rig.CreateAsync(running);
        await rig.Flow.HandleStartAsync(running, new() { Force = true }, default);
        var availableId = await rig.CreateAsync(available);
        var matching = rig.Matching(new InGameOptions { QuickMatchWaitMs = 0 });
        await matching.HandleAsync(joiner, new(4, false), default);
        Assert.Equal(availableId, rig.Registry.TryGet(joiner.Id)!.Id);
        Assert.Equal(RoomPhase.Starting, rig.Registry.Find(runningId)!.Phase);
        Assert.True(Assert.Single(rig.MatchNotifications.To(joiner)).Searching);
        Assert.Empty(rig.Refused.To(joiner));
        await matching.StopAsync();
        await rig.Registry.StopAllAsync();
    }

    /// <summary>Existing rooms are joined without waiting, and rooms created during the window are found on the next poll.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickMatchingJoinsBeforeTheSearchDeadline(bool createdDuringSearch)
    {
        var rig = new Rig();
        var host = Rig.Player(306, "available");
        var joiner = Rig.Player(307, "joiner");
        var matching = rig.Matching(new InGameOptions { QuickMatchWaitMs = 30000, QuickMatchPollMs = 10 });
        try
        {
            if (!createdDuringSearch) await rig.CreateAsync(host);
            var search = matching.HandleAsync(joiner, new(4, false), default).AsTask();
            if (createdDuringSearch) await rig.CreateAsync(host);
            await search.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(rig.Registry.TryGet(host.Id)!.Id, rig.Registry.TryGet(joiner.Id)!.Id);
        }
        finally
        {
            await matching.StopAsync();
            await rig.Registry.StopAllAsync();
        }
    }

    [Fact]
    public async Task ConcurrentQuickMatchesCreateOneRoomAndRespectItsCapacity()
    {
        var rig = new Rig(seats: 2);
        var matching = rig.Matching(new InGameOptions { QuickMatchWaitMs = 0 });
        var players = Enumerable.Range(311, 3).Select(id => Rig.Player(id, "player" + id)).ToArray();
        await Task.WhenAll(players.Select(player => matching.HandleAsync(player, new(4, false), default).AsTask()));
        var rooms = rig.Registry.OpenRooms();
        Assert.Equal(2, rooms.Count);
        Assert.Equal(new[] { 1, 2 }, rooms.Select(room => room.Seats.Count).Order().ToArray());
        Assert.All(players, player => Assert.NotNull(rig.Registry.TryGet(player.Id)));
        await matching.StopAsync();
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task QuickMatchUnloadCancelsPendingSearchWithoutCreatingARoom()
    {
        var rig = new Rig();
        var matching = rig.Matching(new InGameOptions { QuickMatchWaitMs = 30000 });
        var player = Rig.Player(321, "waiting");
        var pending = matching.HandleAsync(player, new(4, false), default).AsTask();
        Assert.True(Assert.Single(rig.MatchNotifications.To(player)).Searching);
        await matching.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await pending;
        Assert.Empty(rig.Registry.OpenRooms());
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task QuickMatchingBoundsPendingRequestsAndDoesNotDuplicateOnePlayersSearch()
    {
        var rig = new Rig();
        var matching = rig.Matching(new InGameOptions { QuickMatchWaitMs = 30000, MaxPendingQuickMatches = 1 });
        var player = Rig.Player(331, "waiting");
        var pending = matching.HandleAsync(player, new(4, false), default).AsTask();
        await matching.HandleAsync(player, new(4, false), default);
        Assert.Single(rig.MatchNotifications.To(player));
        await Assert.ThrowsAsync<InvalidOperationException>(() => matching.HandleAsync(Rig.Player(332, "other"), new(4, false), default).AsTask());
        await matching.StopAsync();
        await pending;
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task BossMatchingDoesNotUseOrdinaryRoomMessagesBeforeItsStageIsSupported()
    {
        var rig = new Rig();
        var matching = rig.Matching(new InGameOptions { QuickMatchWaitMs = 0, AutoStartModeIds = [16] });
        var player = Rig.Player(341, "boss");
        await matching.HandleAsync(player, new(16, false), default);
        Assert.False(Assert.Single(rig.MatchNotifications.To(player)).Searching);
        Assert.Empty(rig.Registry.OpenRooms());
        await matching.StopAsync();
        await rig.Registry.StopAllAsync();
    }

    /// <summary>Dedicated PvE and repeat-entry flags cannot be mistaken for ordinary room matching.</summary>
    [Theory]
    [InlineData(101, true, false)]
    [InlineData(13, true, false)]
    [InlineData(1, false, true)]
    [InlineData(16, true, true)]
    public async Task SeparateMatchingStagesAreRefusedWithoutCreatingOrdinaryRooms(int mode, bool dedicated, bool retry)
    {
        var rig = new Rig();
        var matching = rig.Matching(new InGameOptions { QuickMatchWaitMs = 0 });
        var player = Rig.Player(342, "queue");
        await matching.HandleAsync(player, new(mode, dedicated, retry), default);
        Assert.False(Assert.Single(rig.MatchNotifications.To(player)).Searching);
        Assert.Empty(rig.Registry.OpenRooms());
        await matching.StopAsync();
        await rig.Registry.StopAllAsync();
    }

    [Theory]
    [InlineData("!")]
    [InlineData("！")]
    public async Task StartCommandRejectsGuestAndLetsHostBypassReadyMarks(string prefix)
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);
        await rig.Flow.HandleChatAsync(guest, new(0, prefix + "start"), default);
        Assert.Empty(rig.Starting.Sent);
        Assert.Contains("command.start.host_only", Assert.Single(rig.Notices.To(guest)).Text);
        await rig.Flow.HandleStartAsync(host, new(), default);
        Assert.Empty(rig.Starting.Sent);
        await rig.Flow.HandleChatAsync(host, new(0, " " + prefix + "START "), default);
        Assert.Equal(RoomPhase.Starting, rig.Registry.Find(roomId)!.Phase);
        Assert.Single(rig.Starting.To(host));
        Assert.Single(rig.Starting.To(guest));
        Assert.Empty(rig.Chat.Sent);
        await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
        Assert.Single(rig.Starting.To(host));
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task BossStartCommandCancelsWaitingAndArmsEveryMembersNativeCountdownOnce()
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, AutoStartModeIds = [16], AutoStartTimer = true, AutoStartDelayMs = 65000 });
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "rel_ZombiePVE_01"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        Assert.Contains(":60", Assert.Single(rig.Notices.To(host)).Text);
        Assert.Empty(rig.AutoStart.Sent);
        await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
        await rig.Flow.HandleChatAsync(guest, new(0, "!start"), default);
        Assert.Empty(rig.AutoStart.Sent);
        await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
        await room.AutoStartTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(rig.AutoStart.To(host));
        Assert.Single(rig.AutoStart.To(guest));
        Assert.Empty(rig.Starting.Sent);
        Assert.Null(room.AutoStartCancellation);
        await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
        Assert.Single(rig.AutoStart.To(host));
        await rig.Flow.HandleStartAsync(host, new("rel_zombie_01"), default);
        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.Single(rig.Starting.To(guest));
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task BossCountdownWaitsForTheClientsResolvedRandomMapAndRejectsALateDuplicate()
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, AutoStartModeIds = [16], AutoStartTimer = true, AutoStartDelayMs = 65000 });
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "Random"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
        await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
        await Task.Delay(5200);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
        Assert.Empty(rig.Starting.Sent);
        await rig.Flow.HandleStartAsync(host, new("Random"), default);
        Assert.Empty(rig.Starting.Sent);
        await rig.Flow.HandleStartAsync(host, new("rel_zombie05_Tear"), default);
        Assert.Equal("Random", room.MapKey);
        Assert.Equal("rel_zombie05_Tear", room.RoundMapKey);
        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.Single(rig.Starting.To(host));
        Assert.Single(rig.Starting.To(guest));
        await rig.Flow.HandleStartAsync(host, new(), default);
        Assert.Single(rig.Starting.To(host));
        await rig.Registry.StopAllAsync();
    }

    /// <summary>Returning to the same Boss room re-arms the client countdown and accepts a fresh selected-map request.</summary>
    [Fact]
    public async Task BossCanStartTwoRoundsWithoutChangingItsMap()
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, AutoStartModeIds = [16] });
        var host = Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "Random"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            for (var round = 1; round <= 2; round++)
            {
                await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
                Assert.Equal(RoomPhase.Waiting, room.Phase);
                Assert.Equal(round, rig.AutoStart.To(host).Length);
                await rig.Flow.HandleStartAsync(host, new("rel_zombie05_Tear"), default);
                Assert.Equal(round, room.StartGeneration);
                Assert.Equal("rel_zombie05_Tear", rig.Starting.To(host)[round - 1].MapKey);
                room.MarkPlaying();
                room.ResetToWaiting();
                await rig.Flow.ReturnToRoomAsync(room, default);
                Assert.False(room.AutoStartArmed);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Round resolution cannot replace the waiting selection or retain a previous round's map binding.</summary>
    [Theory]
    [InlineData("Random")]
    [InlineData("Random_Hot")]
    [InlineData("npc_Village07_Easy01")]
    public async Task EachRoundKeepsTheRoomSelectionAndUsesItsOwnResolvedMap(string selection)
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var settings = new RoomBuildSettings("kr-812", new byte[] { 1, 2, 3 });
        await rig.Flow.HandleCreateAsync(host, new("room", "", 4, selection, settings), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            foreach (var map in new[] { "rel_Village07_Easy02", "rel_Village01_Byway" })
            {
                await rig.Flow.HandleStartAsync(host, new(map), default);
                var start = rig.Starting.To(host)[^1];
                Assert.Equal(map, start.MapKey);
                Assert.Equal(map, room.RoundMapKey);
                Assert.Equal(selection, start.Room.MapKey);
                Assert.Same(settings, start.Room.BuildSettings);
                room.RoundStageKey = "previous_transport_stage";
                room.MarkPlaying();
                room.ResetToWaiting();
                await rig.Flow.ReturnToRoomAsync(room, default);
                Assert.Empty(room.RoundMapKey);
                Assert.Empty(room.RoundStageKey);
                Assert.Equal(selection, rig.Entered.To(host)[^1].Room.MapKey);
                Assert.Same(settings, rig.Entered.To(host)[^1].Room.BuildSettings);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A changed waiting selection survives round resolution and retains both native settings blocks.</summary>
    [Theory]
    [InlineData("Random")]
    [InlineData("npc_Village07_Easy01")]
    public async Task ChangedSelectionSurvivesRoundResolution(string selection)
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var original = new RoomBuildSettings("kr-812", new byte[] { 1, 2, 3 });
        var change = new RoomBuildSettings("kr-812", default, new byte[] { 4, 5, 6 });
        await rig.Flow.HandleCreateAsync(host, new("room", "", 4, "initial", original), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            await rig.Flow.HandleSettingsChangeAsync(host, new(4, selection, change), default);
            var changed = Assert.Single(rig.SettingsChanged.To(host)).Room;
            Assert.Equal(selection, changed.MapKey);
            Assert.Equal(original.Bytes.ToArray(), changed.BuildSettings!.Bytes.ToArray());
            Assert.Equal(change.Change.ToArray(), changed.BuildSettings.Change.ToArray());
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), default);
            Assert.Equal(selection, Assert.Single(rig.Starting.To(host)).Room.MapKey);
            room.MarkPlaying();
            room.ResetToWaiting();
            await rig.Flow.ReturnToRoomAsync(room, default);
            Assert.Equal(selection, rig.Entered.To(host)[^1].Room.MapKey);
            Assert.Same(changed.BuildSettings, rig.Entered.To(host)[^1].Room.BuildSettings);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Cooperative mission spawners follow the loaded map rather than the waiting room's random marker.</summary>
    [Fact]
    public async Task RescuePreparationUsesTheResolvedRoundMap()
    {
        var rig = new Rig(options: new InGameOptions
        {
            TypedOnly = true, RescueModeId = 16,
            RescueStartTimesMs = [20000], RescueMissionIds = [1], RescueCitizenNames = ["ms_citizen"],
            RescueMapEnemyCounts = [new() { MapKey = "rel_zombie05_Tear", EnemyCounts = [4] }],
        });
        var host = Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "Random"), default);
        try
        {
            await rig.Flow.HandleStartAsync(host, new("rel_zombie05_Tear"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            Assert.Equal("Random", room.MapKey);
            Assert.Equal("rel_zombie05_Tear", room.RoundMapKey);
            Assert.NotNull(room.Rescues);
            Assert.Equal([4], room.Rescues.EnemyCounts);
            Assert.Single(Assert.Single(rig.RescuePrepared.To(host)).Missions);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task DeletingArmedBossRoomNeverEmitsAServerSelectedStart()
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, AutoStartModeIds = [16] });
        var host = Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "rel_ZombiePVE_01"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
        await rig.Flow.HandleLeaveAsync(host, new(), default);
        Assert.Empty(rig.Starting.Sent);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task LeavingBossRoomCancelsPendingAnnouncements()
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, AutoStartModeIds = [16], AutoStartTimer = true });
        var host = Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "rel_ZombiePVE_01"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        await rig.Flow.HandleLeaveAsync(host, new(), default);
        await room.AutoStartTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(rig.Notices.To(host));
        Assert.Empty(rig.AutoStart.Sent);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task NicknameChangeRefreshesEveryRoomMemberAndRoomList()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default);
        await rig.Flow.OnNicknameChangedAsync(new(11, "renamedhost"), default);
        foreach (var observer in new[] { host, guest })
            Assert.Equal("renamedhost", rig.SeatUpdated.To(observer)[^1].Seat.Nickname);
        Assert.Equal("renamedhost", rig.Registry.Find(roomId)!.FindByGame(host.Id)!.Nickname);
        var list = new Sink<RoomListSnapshot>();
        await rig.Flow.HandleListAsync(guest, new(0), list, default);
        Assert.Equal("renamedhost", Assert.Single(Assert.Single(list.To(guest)).Rooms).MasterNickname);
    }

    [Fact]
    public async Task EquipmentChangeRefreshesEveryWaitingRoomMemberAndTheNextStart()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default);
        int[] items = [300002, 203073, 201001, 207001, 100001];

        await rig.Flow.OnEquipmentSelectionCommittedAsync(new(host, 11, 300002, items), default);

        foreach (var observer in new[] { host, guest })
        {
            var changed = rig.SeatUpdated.To(observer)[^1].Seat;
            Assert.Equal(300002, changed.CharacterItemId);
            Assert.Equal(items, changed.EquippedItemIds);
        }
        var stored = rig.Registry.Find(roomId)!.FindByGame(host.Id)!;
        Assert.Equal(300002, stored.CharacterId);
        Assert.Equal(items, stored.EquippedItemIds);
    }

    [Fact]
    public async Task CreateSeatsTheHostSendsTheRoomAndListsIt()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");

        var roomId = await rig.CreateAsync(host, "pw");

        var entered = Assert.Single(rig.Entered.To(host));
        Assert.Equal(0, entered.YourSlot);
        Assert.Equal(("room", 4, "rel_Village07_Easy02", 16, 0, true),
            (entered.Room.Name, entered.Room.ModeId, entered.Room.MapKey, entered.Room.MaxSeats, entered.Room.MasterSlot, entered.Room.HasPassword));
        var seat = Assert.Single(entered.Room.Seats);
        Assert.Equal((0, 11, "host", 3, false), (seat.Slot, seat.UserNum, seat.Nickname, seat.Level, seat.Ready));
        Assert.Equal(SeatCharacter.DaoCharacterItemId, seat.CharacterItemId);
        Assert.Equal(SessionPhase.Room, host.Features.Get<IPlayerSession>().Phase);
        Assert.NotNull(rig.Registry.FindJoinable(roomId));

        var list = new Sink<RoomListSnapshot>();
        await rig.Flow.HandleListAsync(Rig.Player(12, "other"), new RoomListRequested(7), list, CancellationToken.None);
        var snapshot = Assert.Single(list.Sent).Message;
        Assert.Equal(7, snapshot.Cursor);
        Assert.Equal(new RoomListEntry(roomId, "room", "host", 4, 1, 16, true), Assert.Single(snapshot.Rooms));
    }

    [Fact]
    public async Task ARepeatedCreateFromASeatedPlayerSendsTheSameRoomAgain()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var roomId = await rig.CreateAsync(host);

        Assert.Equal(roomId, await rig.CreateAsync(host));

        Assert.Equal(2, rig.Entered.To(host).Length);
        Assert.Single(rig.Registry.All());
    }

    [Fact]
    public async Task JoinSendsTheWholeRoomToTheJoinerAndOnlyTheNewSeatToTheOthers()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);

        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);

        var entered = Assert.Single(rig.Entered.To(guest));
        Assert.Equal(1, entered.YourSlot);
        Assert.Equal([11, 22], entered.Room.Seats.Select(seat => seat.UserNum));
        Assert.Equal(0, entered.Room.MasterSlot);
        var announced = Assert.Single(rig.SeatUpdated.To(host));
        Assert.Equal((roomId, 1, 22, "guest"), (announced.RoomId, announced.Seat.Slot, announced.Seat.UserNum, announced.Seat.Nickname));
        Assert.Empty(rig.SeatUpdated.To(guest));
        Assert.Single(rig.Entered.To(host));
        Assert.Equal(SessionPhase.Room, guest.Features.Get<IPlayerSession>().Phase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("full")]
    [InlineData("password")]
    public async Task ARefusedJoinIsAnsweredWithItsReasonAndSeatsNobody(string scenario)
    {
        var rig = new Rig(seats: 1);
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host, scenario == "password" ? "secret" : "");
        if (scenario == "password")
        {
            // A one-seat room is full before the password is looked at, so this case needs a free seat.
            rig = new Rig(seats: 2);
            roomId = await rig.CreateAsync(host, "secret");
        }

        var asked = scenario == "missing" ? roomId + 100 : roomId;
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(asked, "wrong"), CancellationToken.None);

        var refused = Assert.Single(rig.Refused.To(guest));
        Assert.Equal(asked, refused.RoomId);
        Assert.Equal(scenario switch
        {
            "missing" => RoomJoinRefusal.NoRoom,
            "full" => RoomJoinRefusal.Full,
            _ => RoomJoinRefusal.WrongPassword,
        }, refused.Reason);
        Assert.Empty(rig.Entered.To(guest));
        Assert.Null(rig.Registry.TryGet(guest.Id));
        Assert.Equal(SessionPhase.MainMenu, guest.Features.Get<IPlayerSession>().Phase);
    }

    [Fact]
    public async Task MasterReadyRequestsCannotInstallAnUncancellableReadyMark()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default);
        await rig.Flow.HandleReadyAsync(host, new RoomReadyRequested(true), default);
        Assert.False(rig.Registry.Find(roomId)!.FindByGame(host.Id)!.Ready);
        Assert.False(rig.SeatUpdated.To(host)[^1].Seat.Ready);
        Assert.False(rig.SeatUpdated.To(guest)[^1].Seat.Ready);
    }

    [Fact]
    public async Task ReadyIsShownToEveryMemberIncludingThePlayer()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);
        rig.SeatUpdated.Sent.Clear();

        await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), CancellationToken.None);

        foreach (var member in new[] { host, guest })
        {
            var update = Assert.Single(rig.SeatUpdated.To(member));
            Assert.Equal((1, true), (update.Seat.Slot, update.Seat.Ready));
        }
    }

    /// <summary>Every committed seat change carries all members without later mutations changing earlier snapshots.</summary>
    [Fact]
    public async Task SeatAnnouncementsCarryTheCompleteRoomAtEachCommittedChange()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        try
        {
            var roomId = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);
            var joined = Assert.Single(rig.SeatUpdated.To(host));
            var joinedRoom = Assert.IsType<RoomView>(joined.Room);
            Assert.Equal(new[] { 11, 22 }, joinedRoom.Seats.Select(seat => seat.UserNum));
            Assert.Equal(joined.Seat, joinedRoom.Seats.Single(seat => seat.Slot == joined.Seat.Slot));
            rig.SeatUpdated.Sent.Clear();
            await rig.Flow.HandleReadyAsync(guest, new(false) { InInventory = true }, default);
            var changed = Assert.Single(rig.SeatUpdated.To(host));
            Assert.True(changed.Room!.Seats.Single(seat => seat.UserNum == 22).InInventory);
            Assert.False(joinedRoom.Seats.Single(seat => seat.UserNum == 22).InInventory);
            rig.SeatUpdated.Sent.Clear();
            await rig.Flow.OnEquipmentSelectionCommittedAsync(new(host, 11, 300012, new[] { 300012, 202001, 207001 }), default);
            var equipped = Assert.Single(rig.SeatUpdated.To(guest));
            Assert.Equal(300012, equipped.Room!.Seats.Single(seat => seat.UserNum == 11).CharacterItemId);
            Assert.Equal(new[] { 300012, 202001, 207001 }, equipped.Room.Seats.Single(seat => seat.UserNum == 11).EquippedItemIds);
            Assert.Equal(300001, joinedRoom.Seats.Single(seat => seat.UserNum == 11).CharacterItemId);
            rig.SeatUpdated.Sent.Clear();
            await rig.Flow.HandleTeamChangeAsync(guest, new(), default);
            var team = Assert.Single(rig.SeatUpdated.To(host));
            Assert.Equal(team.Seat, team.Room!.Seats.Single(seat => seat.UserNum == 22));
            Assert.Equal(300012, team.Room.Seats.Single(seat => seat.UserNum == 11).CharacterItemId);
        }
        finally
        {
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Unknown session bytes survive same-lineage updates and are replaced with another lineage's block.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpaqueSessionPrefixFollowsItsSettingsLineage(bool sameLineage)
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var original = new RoomBuildSettings("modern-layout", new byte[] { 1, 2, 3 }) { SessionPrefix = new byte[] { 9, 8, 7 } };
        var change = new RoomBuildSettings(sameLineage ? "modern-layout" : "another-layout", default, new byte[] { 4, 5 })
        {
            SessionPrefix = new byte[] { 6 },
        };
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("room", "", 4, "map", original), default);
            var roomId = Assert.Single(rig.Entered.To(host)).Room.RoomId;
            await rig.Flow.HandleSettingsChangeAsync(host, new(4, "map2", change), default);
            var held = Assert.Single(rig.SettingsChanged.To(host)).Room.BuildSettings!;
            Assert.Equal(sameLineage ? original.SessionPrefix.ToArray() : change.SessionPrefix.ToArray(), held.SessionPrefix.ToArray());
            Assert.Equal(change.Change.ToArray(), held.Change.ToArray());
            await rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);
            var announced = Assert.Single(rig.SeatUpdated.To(host));
            Assert.Same(held, announced.Room!.BuildSettings);
            Assert.Same(held, Assert.Single(rig.Entered.To(guest)).Room.BuildSettings);
        }
        finally
        {
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Existing constructors need neither an opaque prefix nor a complete update snapshot.</summary>
    [Fact]
    public void OptionalRoomMetadataRetainsLegacyDefaults()
    {
        Assert.True(new RoomBuildSettings("kr-812", default).SessionPrefix.IsEmpty);
        Assert.Null(new RoomSeatUpdated(1, new(0, 1, "name", 1, 2, false, 300001, [])).Room);
        Assert.False(new RoomCreateRequested("room", "", 4, "map").UpdateExistingTitle);
        Assert.Equal(RoomEntryReason.Unspecified, new RoomEntered(new(1, "room", 4, "map", 16, 0, false, [], []), 0).Reason);
    }

    /// <summary>Entry and update reasons follow the operation, even when joins and repeated creates look alike.</summary>
    [Fact]
    public async Task RoomEntryAndTeamReasonsDescribeTheCommittedOperation()
    {
        var rig = new Rig(stageProbes: true);
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        try
        {
            var roomId = await rig.CreateAsync(host);
            Assert.Equal(RoomEntryReason.Created, Assert.Single(rig.Entered.To(host)).Reason);
            Assert.Empty(rig.UiChanged.Sent);
            await rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);
            Assert.Equal(RoomEntryReason.Joined, Assert.Single(rig.Entered.To(guest)).Reason);
            Assert.Equal(RoomSeatUpdateReason.Joined, Assert.Single(rig.SeatUpdated.To(host)).Reason);
            Assert.Equal(new[] { 11, 22 }, Assert.Single(rig.UiChanged.To(host)).Room.Seats.Select(seat => seat.UserNum));
            Assert.Equal(2, rig.UiChanged.Sent.Count);
            rig.SeatUpdated.Sent.Clear();
            await rig.Flow.HandleTeamChangeAsync(guest, new(), default);
            Assert.All(rig.SeatUpdated.Sent, row => Assert.Equal(RoomSeatUpdateReason.TeamChanged, row.Message.Reason));
            await rig.Flow.HandleCreateAsync(host, new("renamed", "", 4, "map") { UpdateExistingTitle = true }, default);
            Assert.Equal(RoomEntryReason.Refreshed, rig.Entered.To(host)[^1].Reason);
            Assert.Equal("renamed", rig.Entered.To(host)[^1].Room.Name);
            await rig.Flow.HandleCreateAsync(guest, new("guest title", "", 4, "map") { UpdateExistingTitle = true }, default);
            Assert.Equal("renamed", rig.Entered.To(guest)[^1].Room.Name);
            await rig.Flow.HandleCreateAsync(host, new("legacy repeat", "", 4, "map"), default);
            Assert.Equal("renamed", rig.Entered.To(host)[^1].Room.Name);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Repeated stage probes answer only the seated sender and install the existing room once.</summary>
    [Fact]
    public async Task StageProbesRefreshUiWithoutJoiningOrReinstallingPeers()
    {
        var rig = new Rig(stageProbes: true);
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        try
        {
            var roomId = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);
            rig.Entered.Sent.Clear();
            rig.UiChanged.Sent.Clear();
            var room = rig.Registry.Find(roomId)!;
            var challenge = room.FindByGame(guest.Id)!.Key.Unknown0;
            await rig.Flow.HandleRoomUiRefreshAsync(guest, new(), default);
            Assert.Equal(challenge, Assert.Single(rig.ProbeAcknowledged.To(guest)).Challenge);
            Assert.Equal(1, Assert.Single(rig.ProbeAcknowledged.To(guest)).Slot);
            Assert.Empty(rig.ProbeAcknowledged.To(host));
            Assert.Empty(rig.Entered.To(host));
            Assert.Equal(RoomEntryReason.ProbeRefresh, Assert.Single(rig.Entered.To(guest)).Reason);
            Assert.Equal(2, rig.UiChanged.Sent.Count);
            Assert.True(room.FindByGame(guest.Id)!.JoinAcked);
            await rig.Flow.HandleRoomUiRefreshAsync(guest, new(), default);
            Assert.Equal(2, rig.ProbeAcknowledged.To(guest).Length);
            Assert.Single(rig.Entered.To(guest));
            Assert.Equal(4, rig.UiChanged.Sent.Count);
            Assert.Equal(2, room.Seats.Count);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            rig.UiChanged.Sent.Clear();
            rig.ProbeAcknowledged.Sent.Clear();
            room.MarkStarting();
            await rig.Flow.HandleRoomUiRefreshAsync(guest, new(), default);
            Assert.Empty(rig.UiChanged.Sent);
            Assert.Empty(rig.ProbeAcknowledged.Sent);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// With reinstall_room_on_first_probe off, the first probe is acknowledged and the member list republished, but
    /// the room is not installed again: a second install rebuilds the stage and replays the room-entry animation.
    /// </summary>
    [Fact]
    public async Task FirstProbeNeedNotInstallTheRoomAgain()
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, MaxPlayersPerMatch = 16, ReinstallRoomOnFirstProbe = false },
            stageProbes: true);
        var host = Rig.Player(11, "host");
        try
        {
            var roomId = await rig.CreateAsync(host);
            rig.Entered.Sent.Clear();
            rig.UiChanged.Sent.Clear();
            await rig.Flow.HandleRoomUiRefreshAsync(host, new(), default);
            Assert.Single(rig.ProbeAcknowledged.To(host));
            Assert.Empty(rig.Entered.To(host));
            Assert.Single(rig.UiChanged.Sent);
            Assert.True(rig.Registry.Find(roomId)!.FindByGame(host.Id)!.JoinAcked);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A dialect that mounts no probe surface keeps its old join burst and cannot silently accept a probe.</summary>
    [Fact]
    public async Task UnboundStageProbeFailsBeforeChangingTheSeat()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        try
        {
            var roomId = await rig.CreateAsync(host);
            Assert.Empty(rig.UiChanged.Sent);
            Assert.Empty(rig.ProbeAcknowledged.Sent);
            var room = rig.Registry.Find(roomId)!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Flow.HandleRoomUiRefreshAsync(host, new(), default).AsTask());
            Assert.False(room.FindByGame(host.Id)!.JoinAcked);
            Assert.Single(rig.Entered.To(host));
            // The client probes on a timer, so a probe from a session without a seat is dropped, not a fault.
            var stranger = Rig.Player(33, "stranger");
            await rig.Flow.HandleRoomUiRefreshAsync(stranger, new(), default);
            Assert.Single(room.Seats);
            Assert.Empty(rig.ProbeAcknowledged.Sent);
            Assert.Empty(rig.Entered.To(stranger));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Presence updates replace the previous state and survive subsequent room projections.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowsingStateReplacesReadyAndIsVisibleToEveryMember(bool master)
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default);
        var player = master ? host : guest;
        foreach (var request in new[]
        {
            new RoomReadyRequested(true),
            new RoomReadyRequested(false) { InShop = true },
            new RoomReadyRequested(false) { InInventory = true },
            new RoomReadyRequested(false),
        })
        {
            rig.SeatUpdated.Sent.Clear();
            await rig.Flow.HandleReadyAsync(player, request, default);
            var stored = rig.Registry.Find(roomId)!.FindByGame(player.Id)!;
            Assert.Equal((!master && request.Ready, request.InShop, request.InInventory),
                (stored.Ready, stored.InShop, stored.InInventory));
            foreach (var observer in new[] { host, guest })
            {
                var announced = Assert.Single(rig.SeatUpdated.To(observer)).Seat;
                Assert.Equal((stored.Ready, stored.InShop, stored.InInventory),
                    (announced.Ready, announced.InShop, announced.InInventory));
            }
            await rig.Flow.HandleCreateAsync(player, new("ignored", "", 4, "ignored"), default);
            var snapshot = rig.Entered.To(player)[^1].Room.Seats.Single(seat => seat.UserNum == stored.UserNum);
            Assert.Equal((stored.Ready, stored.InShop, stored.InInventory),
                (snapshot.Ready, snapshot.InShop, snapshot.InInventory));
        }
    }

    /// <summary>Conflicting typed states cannot overwrite a valid ready state or emit an update.</summary>
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task ConflictingBrowsingStatesDoNotChangeTheSeat(bool ready, bool shop, bool inventory)
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);
        await rig.Flow.HandleReadyAsync(guest, new(true), default);
        rig.SeatUpdated.Sent.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Flow.HandleReadyAsync(guest,
            new(ready) { InShop = shop, InInventory = inventory }, default).AsTask());
        var stored = rig.Registry.Find(roomId)!.FindByGame(guest.Id)!;
        Assert.Equal((true, false, false), (stored.Ready, stored.InShop, stored.InInventory));
        Assert.Empty(rig.SeatUpdated.Sent);
    }

    [Fact]
    public async Task AGuestLeavingVacatesTheSeatAndReturnsThemToTheLobby()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);

        await rig.Flow.HandleLeaveAsync(guest, new RoomLeaveRequested(), CancellationToken.None);

        var vacated = Assert.Single(rig.SeatVacated.To(host));
        Assert.Equal((roomId, 1, 22), (vacated.RoomId, vacated.Seat.Slot, vacated.Seat.UserNum));
        Assert.Empty(rig.MasterChanged.Sent);
        var left = Assert.Single(rig.Left.To(guest));
        Assert.Equal((roomId, 1, 22), (left.RoomId, left.Seat.Slot, left.Seat.UserNum));
        Assert.Empty(rig.SeatVacated.To(guest));
        Assert.Equal(SessionPhase.Plaza, guest.Features.Get<IPlayerSession>().Phase);
        Assert.Null(rig.Registry.TryGet(guest.Id));
    }

    [Fact]
    public async Task TheMasterLeavingPromotesTheNextSeatWithReadyCleared()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);
        await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), CancellationToken.None);
        rig.SeatUpdated.Sent.Clear();

        await rig.Flow.HandleLeaveAsync(host, new RoomLeaveRequested(), CancellationToken.None);

        Assert.Equal(0, Assert.Single(rig.SeatVacated.To(guest)).Seat.Slot);
        Assert.Equal(new RoomMasterChanged(roomId, 1), Assert.Single(rig.MasterChanged.To(guest)));
        var record = Assert.Single(rig.SeatUpdated.To(guest));
        Assert.Equal((1, false), (record.Seat.Slot, record.Seat.Ready));
    }

    [Fact]
    public async Task TheLastPlayerLeavingClosesTheRoom()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var roomId = await rig.CreateAsync(host);

        await rig.Flow.HandleLeaveAsync(host, new RoomLeaveRequested(), CancellationToken.None);

        Assert.Null(rig.Registry.Find(roomId));
        Assert.Empty(rig.SeatVacated.Sent);
    }

    [Fact]
    public async Task TheMasterStartsTheRoundOnThePickedMapOnceEveryoneElseIsReady()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);

        // A guest cannot start; the master cannot start over an unready guest.
        await rig.Flow.HandleStartAsync(guest, new RoomStartRequested("rel_Guest"), CancellationToken.None);
        await rig.Flow.HandleStartAsync(host, new RoomStartRequested("rel_Village01_Byway"), CancellationToken.None);
        Assert.Empty(rig.Starting.Sent);

        await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), CancellationToken.None);
        await rig.Flow.HandleStartAsync(host, new RoomStartRequested("rel_Village01_Byway"), CancellationToken.None);

        foreach (var member in new[] { host, guest })
        {
            var starting = Assert.Single(rig.Starting.To(member));
            Assert.Equal((roomId, "rel_Village01_Byway", 2), (starting.Room.RoomId, starting.MapKey, starting.Room.Seats.Count));
        }
    }

    [Fact]
    public async Task ATeamChangeTogglesBetweenTheTwoTeamsAndIsRefusedOnceReady()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);
        rig.SeatUpdated.Sent.Clear();

        await rig.Flow.HandleTeamChangeAsync(guest, new RoomTeamChangeRequested(), CancellationToken.None);
        await rig.Flow.HandleTeamChangeAsync(guest, new RoomTeamChangeRequested(), CancellationToken.None);
        await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), CancellationToken.None);
        await rig.Flow.HandleTeamChangeAsync(guest, new RoomTeamChangeRequested(), CancellationToken.None);

        // Blue, back to red, then the ready mark; the change after ready sends nothing.
        Assert.Equal([(1, false), (2, false), (2, true)], rig.SeatUpdated.To(host).Select(update => (update.Seat.Team, update.Seat.Ready)));
        Assert.Equal(3, rig.SeatUpdated.To(guest).Length);
    }

    [Fact]
    public async Task TheCreatorsSettingsBlockIsHandedBackWithTheRoom()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var settings = new RoomBuildSettings("kr-812", new byte[] { 1, 2, 3 });

        await rig.Flow.HandleCreateAsync(host, new RoomCreateRequested("room", "", 4, "map", settings), CancellationToken.None);

        Assert.Same(settings, Assert.Single(rig.Entered.To(host)).Room.BuildSettings);
    }

    [Fact]
    public async Task AChatLineReachesEveryMemberIncludingTheSpeaker()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);

        await rig.Flow.HandleChatAsync(guest, new RoomChatSent(0, "hello"), CancellationToken.None);

        var expected = new RoomChatReceived(roomId, 1, "guest", 0, "hello");
        Assert.Equal(expected, Assert.Single(rig.Chat.To(host)));
        Assert.Equal(expected, Assert.Single(rig.Chat.To(guest)));
    }

    /// <summary>A pirate room has no start button: it starts natively once every seat is taken or closed.</summary>
    [Fact]
    public async Task AutomaticStartRoomArmsOnceNoSeatIsLeftOpen()
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, MaxPlayersPerMatch = 3, AutoStartModeIds = [101] });
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        await rig.Flow.HandleCreateAsync(host, new("pirates", "", 101, "npc_Village07_Easy01"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            // The timed countdown is optional and off by default.
            Assert.Empty(rig.Notices.To(host));
            Assert.Empty(rig.AutoStart.Sent);
            await rig.Flow.HandleSeatLockAsync(host, new RoomSeatLockRequested(1, true), default);
            Assert.Empty(rig.AutoStart.Sent);
            await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(room.Id, ""), default);
            Assert.True(room.AutoStartArmed);
            Assert.Single(rig.AutoStart.To(host));
            Assert.Single(rig.AutoStart.To(guest));
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Equal("rel_Village07_Easy02", room.RoundMapKey);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// Back from a round, a full automatic-start room holds before it starts again, and then starts only once
    /// every player except the master is ready.
    /// </summary>
    [Fact]
    public async Task AutomaticStartRoomHoldsAfterARoundAndThenWaitsForReadyPlayers()
    {
        var rig = new Rig(options: new InGameOptions
        {
            TypedOnly = true, MaxPlayersPerMatch = 2, AutoStartModeIds = [101], AutoStartRematchHoldMs = 300,
        });
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        await rig.Flow.HandleCreateAsync(host, new("pirates", "", 101, "npc_Village07_Easy01"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(room.Id, ""), default);
            Assert.Single(rig.AutoStart.To(host));
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), default);
            room.MarkPlaying();
            room.ResetToWaiting();
            await rig.Flow.ReturnToRoomAsync(room, default);

            // The return itself arms nothing, and a ready mark inside the hold does not either.
            Assert.False(room.AutoStartArmed);
            await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), default);
            Assert.False(room.AutoStartArmed);
            await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(false), default);

            // After the hold the room waits for the guest's ready mark.
            await Task.Delay(600);
            Assert.False(room.AutoStartArmed);
            await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), default);
            Assert.True(room.AutoStartArmed);
            Assert.Equal(2, rig.AutoStart.To(host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>When the hold ends with every player already ready, the room starts by itself.</summary>
    [Fact]
    public async Task AutomaticStartRoomStartsWhenTheHoldEndsWithEveryoneReady()
    {
        var rig = new Rig(options: new InGameOptions
        {
            TypedOnly = true, MaxPlayersPerMatch = 2, AutoStartModeIds = [101], AutoStartRematchHoldMs = 200,
        });
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        await rig.Flow.HandleCreateAsync(host, new("pirates", "", 101, "npc_Village07_Easy01"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(room.Id, ""), default);
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), default);
            room.MarkPlaying();
            room.ResetToWaiting();
            await rig.Flow.ReturnToRoomAsync(room, default);
            await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), default);
            Assert.False(room.AutoStartArmed);
            for (var i = 0; i < 50 && !room.AutoStartArmed; i++)
                await Task.Delay(50);
            Assert.True(room.AutoStartArmed);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An early rematch timer wake must wait for the deadline before arming the room.</summary>
    [Fact]
    public async Task AutomaticStartRoomRetriesWhenTheHoldTimerWakesEarly()
    {
        var now = 1000L;
        var firstWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEarlyWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        async ValueTask Delay(TimeSpan delay, CancellationToken ct)
        {
            if (first)
            {
                first = false;
                Assert.Equal(TimeSpan.FromMilliseconds(200), delay);
                firstWake.TrySetResult();
                await releaseEarlyWake.Task.WaitAsync(ct);
                return;
            }
            Assert.Equal(TimeSpan.FromMilliseconds(1), delay);
            retryWaiting.TrySetResult();
            await releaseDeadline.Task.WaitAsync(ct);
            Interlocked.Exchange(ref now, 1200);
        }

        var rig = new Rig(options: new InGameOptions
        {
            TypedOnly = true, MaxPlayersPerMatch = 2, AutoStartModeIds = [101], AutoStartRematchHoldMs = 200,
        }, autoStartNow: () => Interlocked.Read(ref now), autoStartDelay: Delay);
        var host = Rig.Player(111, "host");
        var guest = Rig.Player(222, "guest");
        await rig.Flow.HandleCreateAsync(host, new("pirates", "", 101, "npc_Village07_Easy01"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(room.Id, ""), default);
            await rig.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), default);
            room.MarkPlaying();
            room.ResetToWaiting();
            await rig.Flow.ReturnToRoomAsync(room, default);
            await rig.Flow.HandleReadyAsync(guest, new RoomReadyRequested(true), default);
            await firstWake.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Interlocked.Exchange(ref now, 1199);
            releaseEarlyWake.TrySetResult();
            await retryWaiting.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1199, Interlocked.Read(ref now));
            Assert.Single(rig.AutoStart.To(host));
            Assert.False(room.AutoStartArmed);
            releaseDeadline.TrySetResult();
            for (var i = 0; i < 50 && rig.AutoStart.To(host).Length != 2; i++)
                await Task.Delay(50);
            Assert.Equal(2, rig.AutoStart.To(host).Length);
            Assert.True(room.AutoStartArmed);
        }
        finally
        {
            releaseEarlyWake.TrySetResult();
            releaseDeadline.TrySetResult();
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A lone master starts an automatic-start room by closing every other seat; other modes are untouched.</summary>
    [Theory]
    [InlineData(101, true)]
    [InlineData(1, false)]
    public async Task ClosingEveryOtherSeatArmsOnlyAnAutomaticStartMode(int mode, bool arms)
    {
        var rig = new Rig(options: new InGameOptions { TypedOnly = true, MaxPlayersPerMatch = 3, AutoStartModeIds = [101] });
        var host = Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new("room", "", mode, "rel_Village07_Easy02"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        try
        {
            await rig.Flow.HandleSeatLockAsync(host, new RoomSeatLockRequested(1, true), default);
            await rig.Flow.HandleSeatLockAsync(host, new RoomSeatLockRequested(2, true), default);
            Assert.Equal(arms, room.AutoStartArmed);
            Assert.Equal(arms ? 1 : 0, rig.AutoStart.To(host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task OnlyTheMasterClosesAnEmptySeatAndAClosedSeatCannotBeJoined()
    {
        var rig = new Rig(seats: 2);
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);

        // The master's own seat is occupied and cannot be closed.
        await rig.Flow.HandleSeatLockAsync(host, new RoomSeatLockRequested(0, true), CancellationToken.None);
        Assert.Empty(rig.SeatLock.Sent);

        await rig.Flow.HandleSeatLockAsync(host, new RoomSeatLockRequested(1, true), CancellationToken.None);
        Assert.Equal(new RoomSeatLockChanged(roomId, 1, true), Assert.Single(rig.SeatLock.To(host)));
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);
        Assert.Equal(RoomJoinRefusal.Full, Assert.Single(rig.Refused.To(guest)).Reason);

        await rig.Flow.HandleSeatLockAsync(host, new RoomSeatLockRequested(1, false), CancellationToken.None);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);
        Assert.NotNull(rig.Registry.TryGet(guest.Id));

        // A guest cannot lock seats.
        await rig.Flow.HandleSeatLockAsync(guest, new RoomSeatLockRequested(1, true), CancellationToken.None);
        Assert.Equal(2, rig.SeatLock.To(host).Length);
    }

    [Fact]
    public async Task TheMasterRemovesAMemberWhoIsToldAndReturnedToTheLobby()
    {
        var rig = new Rig();
        var host = Rig.Player(11, "host");
        var guest = Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);

        // Neither a guest nor a kick of oneself removes anybody.
        await rig.Flow.HandleKickAsync(guest, new RoomKickRequested("host"), CancellationToken.None);
        await rig.Flow.HandleKickAsync(host, new RoomKickRequested("host"), CancellationToken.None);
        Assert.Empty(rig.Kicked.Sent);

        await rig.Flow.HandleKickAsync(host, new RoomKickRequested("guest"), CancellationToken.None);

        var notice = new RoomMemberKicked(roomId, "host", "guest");
        Assert.Equal(notice, Assert.Single(rig.Kicked.To(host)));
        Assert.Equal(notice, Assert.Single(rig.Kicked.To(guest)));
        Assert.Equal(1, Assert.Single(rig.SeatVacated.To(host)).Seat.Slot);
        Assert.Equal(22, Assert.Single(rig.Left.To(guest)).Seat.UserNum);
        Assert.Equal(SessionPhase.Plaza, guest.Features.Get<IPlayerSession>().Phase);
        Assert.Null(rig.Registry.TryGet(guest.Id));
    }
}
