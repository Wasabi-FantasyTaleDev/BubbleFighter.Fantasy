using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The typed round flow: the loading barrier, the countdown, the start and the gameplay feed.</summary>
public sealed partial class RoundFlowTests
{
    /// <summary>Rejects duplicate and non-native mode identifiers before a direct-kill policy can load.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    [InlineData(13, false)]
    [InlineData(14, true)]
    public void DirectKillModesValidatePositiveDistinctIdentifiers(int second, bool valid)
    {
        var options = new InGameOptions { DirectKillModes = [13, second] };
        Assert.Equal(!valid, options.Validate().Any(result => result.ErrorMessage?.StartsWith("direct_kill_modes", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task HiddenTutorialSharesLoadBarrierRevivalAndCleanupWithoutOrdinaryResults()
    {
        var rig = new Rig(timeLimitMs: 5);
        var tutorials = new TutorialRoomFlow(rig.Registry);
        try
        {
            await tutorials.OpenAsync(rig.Host, new(103, "rel_prologue_01", 300005,
                [300005, 202001, 203001], 0, 20, 99999000, false), default);
            var room = Assert.IsType<PlayRoom>(rig.Registry.TryGet(rig.Host.Id));
            Assert.Empty(rig.Registry.OpenRooms());
            Assert.Empty(rig.Entered.To(rig.Host));
            Assert.Empty(rig.Starting.To(rig.Host));
            Assert.False(room.AutoStartArmed);
            Assert.Equal(new[] { 300005, 202001, 203001 }, Assert.Single(room.Seats).EquippedItemIds);
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([new(1, 2, 3, 0, 10, 10)]), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            Assert.Single(rig.Loaded.To(rig.Host));
            Assert.True(Assert.Single(rig.Countdown.To(rig.Host)).IsTutorial);
            var start = await rig.WaitStartedAsync(rig.Host);
            Assert.True(start.IsTutorial);
            Assert.Equal(99999000, start.TimeLimitMs);
            await rig.Round.HandleRespawnRequestedAsync(rig.Host, new(0), default);
            Assert.Single(rig.SpawnOffered.To(rig.Host));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, new(0, 0), default);
            Assert.Single(rig.Respawned.To(rig.Host));
            for (var attempt = 0; attempt < 200 && rig.SupplyCreated.To(rig.Host).Length == 0; attempt++)
                await Task.Delay(10);
            Assert.Single(rig.SupplyCreated.To(rig.Host));
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Empty(rig.Ended.To(rig.Host));
            await tutorials.CloseAsync(rig.Host, new(), default);
            Assert.Null(rig.Registry.TryGet(rig.Host.Id));
            Assert.Equal(RoomPhase.Closed, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A starting player's own character add is acknowledged to that player only, once per round.</summary>
    [Fact]
    public async Task ObjectAddIsAcknowledgedOnceToTheRequesterOnly()
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleObjectAddRequestedAsync(rig.Guest, new(1100000), default);
            await rig.Round.HandleObjectAddRequestedAsync(rig.Guest, new(1100000), default);
            Assert.Equal(1100000, Assert.Single(rig.ObjectAdded.To(rig.Guest)).ObjectId);
            Assert.Empty(rig.ObjectAdded.To(rig.Host));
            await rig.Round.HandleObjectAddRequestedAsync(rig.Host, new(1100000), default);
            Assert.Empty(rig.ObjectAdded.To(rig.Host));
            await rig.Round.HandleObjectAddRequestedAsync(rig.Host, new(1000000), default);
            Assert.Single(rig.ObjectAdded.To(rig.Host));
            Assert.Empty(rig.Loaded.To(rig.Host));
            Assert.Empty(rig.Countdown.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A start that carries a rule record plays its ordinary length; one without keeps the row's length.</summary>
    [Theory]
    [InlineData(true, 600000)]
    [InlineData(false, 180000)]
    public async Task RuleRecordSetsTheRoundLength(bool rules, int expected)
    {
        var rig = new Rig();
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("room", "", 4, "map"), default);
            var request = new RoomStartRequested("map") { TimeLimits = rules ? new RoomRoundTimeLimits(600000, 241000) : null };
            await rig.Rooms.HandleStartAsync(rig.Host, request, default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            var started = await rig.WaitStartedAsync(rig.Host);
            Assert.Equal(expected, started.TimeLimitMs);
            Assert.False(started.Unlimited);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A server-drawn start plays a concrete key from the selection slots, draws for a random marker, and refuses a
    /// start whose selection is empty; the drawn map and its stage reach the start message.
    /// </summary>
    [Theory]
    [InlineData("rel_Village01", "", "Random", "rel_Village01")]
    [InlineData("", "", "Random", null)]
    [InlineData("", "", "", "")]
    public async Task ServerMapDrawResolvesTheStartSelection(string key, string name, string selection, string? expected)
    {
        var rig = new Rig(configure: options => options.ServerMapDraw = true);
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("room", "", 4, "map"), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested(key) { MapName = name, MapSelection = selection }, default);
            if (expected == "")
            {
                Assert.Empty(rig.Starting.To(rig.Host));
                return;
            }
            var starting = Assert.Single(rig.Starting.To(rig.Host));
            if (expected is null)
                Assert.Contains(starting.MapKey, BubbleFighter.Fantasy.InGame.Data.KnownMapKeys.All);
            else
                Assert.Equal(expected, starting.MapKey);
            Assert.Equal(starting.MapKey, starting.StageKey);
            Assert.False(starting.IsMission);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Without a server draw the start keeps the master's resolved key and names no stage of its own.</summary>
    [Fact]
    public async Task WithoutAServerDrawTheStartKeepsTheMastersKey()
    {
        var rig = new Rig();
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("room", "", 4, "map"), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("rel_x") { MapSelection = "Random" }, default);
            var starting = Assert.Single(rig.Starting.To(rig.Host));
            Assert.Equal("rel_x", starting.MapKey);
            Assert.Equal("", starting.StageKey);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A cleared tutorial-room stage is stored under its MapMeta stageNum with the time since the round started.</summary>
    [Fact]
    public async Task ClearedTutorialStageIsRecordedByStageNumberWithItsTime()
    {
        var rig = new Rig(timeLimitMs: 5);
        var store = new RecordingStages();
        var stageNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["sin_mission_wall_lv_01"] = 6 };
        var tutorials = new TutorialRoomFlow(rig.Registry, stageNumbers, store);
        try
        {
            await tutorials.OpenAsync(rig.Host, new(15, "sin_mission_wall_lv_01", 300005, [300005], 0, 20, 90000, true), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await tutorials.ClearedAsync(rig.Host, new("sin_mission_wall_lv_01"), default);
            await tutorials.ClearedAsync(rig.Host, new("rel_prologue_01"), default);
            var (user, clear) = Assert.Single(store.Clears);
            Assert.True(user > 0);
            Assert.Equal(6, clear.Stage);
            Assert.Equal(0, clear.BestCount);
            Assert.InRange(clear.BestSeconds, 1, 60);
            await tutorials.CloseAsync(rig.Host, new(), default);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The first clear of a stage grants its MapMeta IsItemAward through the shop; a later clear does not.</summary>
    [Fact]
    public async Task FirstTutorialStageClearGrantsItsGiftOnce()
    {
        var rig = new Rig(timeLimitMs: 5);
        var store = new RecordingStages();
        var gifts = new RecordingGifts();
        var stageNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["sin_mission_wall_lv_03"] = 26 };
        var stageGifts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["sin_mission_wall_lv_03"] = 1000704 };
        var tutorials = new TutorialRoomFlow(rig.Registry, stageNumbers, store, null, stageGifts, gifts);
        try
        {
            await tutorials.OpenAsync(rig.Host, new(15, "sin_mission_wall_lv_03", 300005, [300005], 0, 20, 90000, true), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await tutorials.ClearedAsync(rig.Host, new("sin_mission_wall_lv_03"), default);
            await tutorials.ClearedAsync(rig.Host, new("sin_mission_wall_lv_03"), default);
            Assert.Equal(2, store.Clears.Count);
            var (user, key) = Assert.Single(gifts.Granted);
            Assert.Equal(store.Clears[0].User, user);
            Assert.Equal(1000704, key);
            await tutorials.CloseAsync(rig.Host, new(), default);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private sealed class RecordingGifts : BubbleFighter.Fantasy.Contracts.Shop.IShopGiftService
    {
        public List<(long User, int Key)> Granted { get; } = [];
        public bool CanGrant(int key) => true;
        public ValueTask<bool> GrantAsync(ISession session, long userNum, int key, CancellationToken ct)
        {
            Granted.Add((userNum, key));
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingStages : BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore
    {
        public List<(long User, BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear Clear)> Clears { get; } = [];
        public ValueTask<IReadOnlyList<BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear>> ListAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear>>([]);
        public ValueTask<bool> RecordClearAsync(long userNum, BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear clear, CancellationToken ct)
        {
            Clears.Add((userNum, clear));
            return ValueTask.FromResult(Clears.Count == 1);
        }
    }

    [Fact]
    public async Task NativeSingleStageTutorialUsesSharedRevivalWithoutServerSupplyScheduling()
    {
        var rig = new Rig(timeLimitMs: 5);
        var tutorials = new TutorialRoomFlow(rig.Registry);
        try
        {
            await tutorials.OpenAsync(rig.Host, new(15, "rel_prologue_01", 300005,
                [300005, 202001, 203001], 0, 20, 99999000, true), default);
            var room = Assert.IsType<PlayRoom>(rig.Registry.TryGet(rig.Host.Id));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            Assert.True((await rig.WaitStartedAsync(rig.Host)).IsTutorial);
            await rig.Round.HandleRespawnRequestedAsync(rig.Host, new(0), default);
            Assert.Single(rig.SpawnOffered.To(rig.Host));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, new(0, 0), default);
            Assert.Single(rig.Respawned.To(rig.Host));
            Assert.Null(room.Supplies);
            Assert.Empty(rig.SupplyCreated.To(rig.Host));
            Assert.Empty(rig.Registry.OpenRooms());
            await tutorials.CloseAsync(rig.Host, new(), default);
            Assert.Null(rig.Registry.TryGet(rig.Host.Id));
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingHiddenTutorialCancelsCountdownBeforeItCanStart(bool disconnect)
    {
        var rig = new Rig();
        var tutorials = new TutorialRoomFlow(rig.Registry);
        try
        {
            await tutorials.OpenAsync(rig.Host, new(1, "rel_Tutorial_01", 300001,
                [300001], 2, 60000, 0, true), default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            if (disconnect)
            {
                rig.Rooms.OnConnectionClosed(new BubbleFighter.Fantasy.Abstractions.Net.ConnectionClosedEvent(rig.Host.Connection, "test"));
                for (var attempt = 0; attempt < 200 && rig.Registry.TryGet(rig.Host.Id) is not null; attempt++)
                    await Task.Delay(10);
            }
            else await tutorials.CloseAsync(rig.Host, new(), default);
            await room.CountdownTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(rig.Started.To(rig.Host));
            Assert.Null(rig.Registry.TryGet(rig.Host.Id));
            await tutorials.CloseAsync(rig.Host, new(), default);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private sealed class Sink<T> : IMessageSender<T> where T : IMessage
    {
        private readonly List<(long Session, T Message)> _sent = [];
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            lock (_sent)
                _sent.Add((session.Id, message));
            return ValueTask.CompletedTask;
        }
        public T[] To(ISession session)
        {
            lock (_sent)
                return [.. _sent.Where(row => row.Session == session.Id).Select(row => row.Message)];
        }
    }

    private sealed class ResultGate : IMessageSender<RoundEnded>, IMessageSender<RoundResultBanner>, IRoundResultSequencePolicy
    {
        public string Protocol => "test/game";
        public bool EndBeforeScoreboard => true;
        public bool SingleScoreboard => true;
        public int Ends { get; private set; }
        public int Banners { get; private set; }
        public List<RoundResultBanner> BannerMessages { get; } = [];
        public ValueTask SendAsync(ISession session, RoundEnded message, CancellationToken ct)
        {
            Ends++;
            return ValueTask.CompletedTask;
        }
        public ValueTask SendAsync(ISession session, RoundResultBanner message, CancellationToken ct)
        {
            Banners++;
            BannerMessages.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public RoomRegistry Registry { get; }
        public RoomFlow Rooms { get; }
        public RoundFlow Round { get; }
        public Sink<RoomEntered> Entered { get; } = new();
        public Sink<RoomSeatUpdated> SeatUpdated { get; } = new();
        public Sink<RoomSeatVacated> SeatVacated { get; } = new();
        public Sink<RoomMasterChanged> MasterChanged { get; } = new();
        public Sink<RoomMatchStarting> Starting { get; } = new();
        public Sink<RoundPlayerLoaded> Loaded { get; } = new();
        public Sink<RoundCountdownStarted> Countdown { get; } = new();
        public Sink<RoundStarted> Started { get; } = new();
        public Sink<RoundFeedReceived> Feed { get; } = new();
        public Sink<RoundEnded> Ended { get; } = new();
        public Sink<RoundSpawnOffered> SpawnOffered { get; } = new();
        public Sink<RoundPlayerRespawned> Respawned { get; } = new();
        public Sink<RoundRespawnWaitChanged> WaitChanged { get; } = new();
        public Sink<RoundSupplyCreated> SupplyCreated { get; } = new();
        public Sink<RoundSupplyPicked> SupplyPicked { get; } = new();
        public Sink<RoundLucciCreated> LucciCreated { get; } = new();
        public Sink<RoundLucciPicked> LucciPicked { get; } = new();
        public Sink<RoundStarCreated> StarCreated { get; } = new();
        public Sink<RoundStarPicked> StarPicked { get; } = new();
        public Sink<RoundStarFeverStarted> StarFever { get; } = new();
        public Sink<RoundItemUsed> ItemUsed { get; } = new();
        public Sink<RoundItemSlotChanged> ItemSlotChanged { get; } = new();
        public Sink<RoundCountedItemsPrepared> CountedItemsPrepared { get; } = new();
        public Sink<RoundCountedItemUsed> CountedItemUsed { get; } = new();
        public Sink<RoundPvePointsChanged> PvePoints { get; } = new();
        public Sink<RoundRescueMissionsPrepared> RescuePrepared { get; } = new();
        public Sink<RoundRescueMissionStarted> RescueStarted { get; } = new();
        public Sink<RoundRescueMissionFailed> RescueFailed { get; } = new();
        public Sink<RoundRescueMissionSucceeded> RescueSucceeded { get; } = new();
        public Sink<RoomTeamConverted> TeamConverted { get; } = new();
        public Sink<RoomChatReceived> Chat { get; } = new();
        public Sink<RoundObjectAddAcknowledged> ObjectAdded { get; } = new();
        public RecordingSession Host { get; } = Player(11, "host");
        public RecordingSession Guest { get; } = Player(22, "guest");
        public int RoomId { get; private set; }

        public Rig(int timeLimitMs = 180_000, IPluginContext? resultContext = null, int[]? randomSupplyItemKinds = null,
            int[]? cooperativeStageResultModes = null, int zombieConversionModeId = 0, int[]? countedItemKinds = null, bool rescueMissions = false, int rescueDurationMs = 5000, int rescueCount = 1, int rescueCooldownMs = 25000, int[]? immediateSupplyItemKinds = null, bool pvePoints = false, int rescueStopTimeMs = 0, int[]? cooperativeScoreOutcomeModes = null, int nativeItemSlotCount = 1, int resultRewardDelayMs = 6000, int[]? pvePointUnitValues = null, int[]? countedImmediateItemKinds = null, int[]? directKillModes = null,
            Action<InGameOptions>? configure = null, Func<TimeSpan, CancellationToken, ValueTask>? zombieKingDelay = null)
        {
            var options = new InGameOptions { StartTimerMs = 30, RoundUnknown0 = timeLimitMs, ResultCloseGraceMs = 30, SupplyPollMs = 10,
                RandomSupplyItemKinds = randomSupplyItemKinds ?? [], CooperativeStageResultModes = cooperativeStageResultModes ?? [],
                NativeItemSlotCount = nativeItemSlotCount, ResultRewardDelayMs = resultRewardDelayMs,
                CooperativeScoreOutcomeModes = cooperativeScoreOutcomeModes ?? [],
                ImmediateSupplyItemKinds = immediateSupplyItemKinds ?? [],
                DirectKillModes = directKillModes ?? [],
                CountedImmediateItemKinds = countedImmediateItemKinds ?? [],
                PvePointModeId = pvePoints ? 4 : 0, PvePointUnitValues = pvePoints ? pvePointUnitValues ?? [2, 5] : [],
                ZombieConversionModeId = zombieConversionModeId, CountedItemModeId = countedItemKinds is null ? 0 : 4,
                CountedItemKinds = countedItemKinds ?? [], CountedItemSlotCount = countedItemKinds?.Length ?? 5 };
            if (rescueMissions)
            {
                options.RescueModeId = 4;
                options.RescueStartTimesMs = Enumerable.Range(0, rescueCount).Select(index => index * rescueDurationMs).ToArray();
                options.RescueCooldownMs = rescueCooldownMs;
                options.RescueDurationMs = rescueDurationMs;
                options.RescueStopTimeMs = rescueStopTimeMs;
                options.RescueMissionIds = [1];
                options.RescueCitizenNames = ["ms_citizen"];
                options.RescueMapEnemyCounts = [new() { MapKey = "map", EnemyCounts = Enumerable.Repeat(2, rescueCount).ToArray() }];
            }
            if (resultContext is not null) options.GameProtocol = "test/game";
            configure?.Invoke(options);
            if (cooperativeScoreOutcomeModes is not null)
            {
                options.ResultScoreboardDelayMs = 0;
                options.ResultRewardDelayMs = 0;
                options.ResultCloseGraceMs = 1;
            }
            Registry = new RoomRegistry(options);
            Rooms = new RoomFlow(Registry, new RoomSenders(Entered, SeatUpdated, SeatVacated, MasterChanged, MatchStarting: Starting,
                TeamConverted: TeamConverted, CountedItemsPrepared: CountedItemsPrepared, RescuePrepared: RescuePrepared, Chat: Chat),
                NullLogger.Instance, null, null, 36, options, default, null, null, null,
                static () => Environment.TickCount64,
                static (delay, ct) => new(Task.Delay(delay, ct)),
                zombieKingDelay ?? (static (delay, ct) => new(Task.Delay(delay, ct))));
            Round = new RoundFlow(Registry, options, new RoundSenders(Loaded, Countdown, Started, Feed, Ended, SpawnOffered, Respawned,
                WaitChanged: WaitChanged,
                SupplyCreated: SupplyCreated, SupplyPicked: SupplyPicked, ItemUsed: ItemUsed,
                CountedItemUsed: CountedItemUsed, RescueStarted: RescueStarted, RescueFailed: RescueFailed, RescueSucceeded: RescueSucceeded, RescuePrepared: RescuePrepared, PvePoints: PvePoints, ItemSlotChanged: ItemSlotChanged, LucciCreated: LucciCreated, LucciPicked: LucciPicked,
                StarCreated: StarCreated, StarPicked: StarPicked, StarFever: StarFever, ObjectAdded: ObjectAdded), NullLogger.Instance, CancellationToken.None,
                Rooms.ReturnToRoomAsync, resultContext);
            Rooms.SeatLeftDuringRound = Round.OnSeatLeftAsync;
            Rooms.TeamConvertedDuringRound = Round.OnTeamConvertedAsync;
            Round.RoundPlaying = Rooms.ScheduleZombieKing;
        }

        private static RecordingSession Player(long id, string nickname)
        {
            var session = new RecordingSession(id);
            session.SetLobbyPlayer(id, nickname);
            return session;
        }

        /// <summary>Two seated players (plus any extra ones), every guest ready, the master's start accepted.</summary>
        public async Task StartAsync(params ISession[] extra)
        {
            await Rooms.HandleCreateAsync(Host, new RoomCreateRequested("room", "", 4, "map"), CancellationToken.None);
            RoomId = Entered.To(Host)[0].Room.RoomId;
            foreach (var member in extra.Prepend(Guest))
            {
                await Rooms.HandleJoinAsync(member, new RoomJoinRequested(RoomId, ""), CancellationToken.None);
                await Rooms.HandleReadyAsync(member, new RoomReadyRequested(true), CancellationToken.None);
            }
            await Rooms.HandleStartAsync(Host, new RoomStartRequested("map"), CancellationToken.None);
            Assert.Single(Starting.To(Guest));
        }

        public async Task<RoundStarted> WaitStartedAsync(ISession member)
        {
            for (var i = 0; i < 200 && Started.To(member).Length == 0; i++)
                await Task.Delay(10);
            return Assert.Single(Started.To(member));
        }
    }

    /// <summary>A native-banner time-over grants rewards and sends one final scoreboard instead of 613 plus 614.</summary>
    [Fact]
    public async Task NativeBannerTimeOverUsesOneFinalScoreboard()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "single-result-test", Plugin = "single-result-test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var gate = new ResultGate();
        var boards = new Sink<RoundScoreboard>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(boards));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(gate));
        context.Effect(services.RegisterMany<IMessageSender<RoundResultBanner>>(gate));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 150,
            configure: options => options.StarModeIds = [5]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate)
            {
                room.ModeId = 5;
                room.Scoring.AddStars(room.FindByGame(rig.Host.Id)!.Slot, 7, 1000);
                Assert.Equal(7, room.Scoring.Stars(room.FindByGame(rig.Host.Id)!.Slot));
            }
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.TimeLimit }, default);
            for (var i = 0; i < 100 && boards.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            var board = Assert.Single(boards.To(rig.Host));
            Assert.True(board.WithRewards);
            Assert.True(board.TimeOver);
            Assert.Equal(RoundScoreboardPresentation.StarIndividual, board.Presentation);
            Assert.Equal(7, board.BannerFirstScore);
            Assert.Equal(7, board.BannerSecondScore);
            Assert.Equal(0, gate.Banners);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Single(boards.To(rig.Host));
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            context.Unwind();
        }
    }

    /// <summary>The team star transition uses 613 alone; the individual transition uses 614 alone.</summary>
    [Fact]
    public async Task NativeBannerTeamTimeOverUsesOnlyResultBanner()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "team-banner-test", Plugin = "team-banner-test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var gate = new ResultGate();
        var boards = new Sink<RoundScoreboard>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(boards));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(gate));
        context.Effect(services.RegisterMany<IMessageSender<RoundResultBanner>>(gate));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 150,
            configure: options => options.StarTeamModeIds = [6]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate)
            {
                room.ModeId = 6;
                room.FindByGame(rig.Host.Id)!.Team = 1;
                room.FindByGame(rig.Guest.Id)!.Team = 2;
                room.Scoring.AddStars(room.FindByGame(rig.Host.Id)!.Slot, 3, 1000, team: 1, trackTeamTotal: true);
                room.Scoring.AddStars(room.FindByGame(rig.Guest.Id)!.Slot, 7, 1000, team: 2, trackTeamTotal: true);
            }
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.TimeLimit }, default);
            for (var i = 0; i < 100 && gate.Banners < 2; i++) await Task.Delay(10);
            Assert.Empty(boards.To(rig.Host));
            Assert.Equal(2, gate.Banners);
            Assert.All(gate.BannerMessages, banner =>
            {
                Assert.Equal(3, banner.FirstScore);
                Assert.Equal(7, banner.SecondScore);
            });
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(boards.To(rig.Host));
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            context.Unwind();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedResultDelayAllowsChatAndStopsWhenTheRoomCloses(bool closeRoom)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "result-chat-test", Plugin = "result-chat-test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var boards = new Sink<RoundScoreboard>();
        var stages = new Sink<ResultStageAdvanced>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(boards));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(stages));
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 3000);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.False(room.ResultTask.IsCompleted);
            await rig.Rooms.HandleChatAsync(rig.Host, new(0, "during results"), default).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Single(rig.Chat.To(rig.Host));
            Assert.Single(rig.Chat.To(rig.Guest));
            Assert.False(room.ResultTask.IsCompleted);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            if (closeRoom)
            {
                await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
                await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
                await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Empty(stages.To(rig.Host));
                Assert.Empty(stages.To(rig.Guest));
            }
            else
            {
                await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Single(stages.To(rig.Host));
                Assert.Single(stages.To(rig.Guest));
                Assert.Equal(2, boards.To(rig.Host).Length);
                Assert.Equal(RoomPhase.Waiting, room.Phase);
            }
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            context.Unwind();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CooperativeResultTotalsReflectObjectiveDespiteUnassignedTeamsAndPersonalKills(bool succeeded)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "objective-test", Plugin = "objective-test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var boards = new Sink<RoundScoreboard>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(boards));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        try
        {
            var rig = new Rig(resultContext: context, cooperativeStageResultModes: [4], cooperativeScoreOutcomeModes: [4]);
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            var host = room.FindByGame(rig.Host.Id)!;
            lock (room.Gate)
            {
                foreach (var seat in room.Seats) seat.Team = 0;
                for (var i = 0; i < 7; i++) room.Scoring.AddKill(host.Slot, 0);
            }
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.StageReported, StageSucceeded = succeeded }, default);
            for (var i = 0; i < 200 && !boards.To(rig.Host).Any(); i++) await Task.Delay(10);
            var board = boards.To(rig.Host).Last();
            Assert.Equal(succeeded ? 2 : 1, board.WinnerTeam);
            Assert.True(board.TeamTotals[board.WinnerTeam - 1] > board.TeamTotals[2 - board.WinnerTeam]);
            Assert.Equal(7, board.TeamTotals[1]);
            Assert.All(board.Rows, row => Assert.Equal(2, row.Team));
            Assert.Equal(7, board.Rows.Single(row => row.Slot == host.Slot).Stats.Kills);
            Assert.Equal(succeeded ? RoundOutcome.Won : RoundOutcome.Lost, board.Rows[0].Outcome);
            Assert.Equal(new[] { 0, 0 }, room.Scoring.TeamTotals);
        }
        finally { context.Unwind(); }
    }

    /// <summary>Native cooperative awards go to all members, keep equal ranks and reject invalid score units.</summary>
    [Fact]
    public async Task PveScoreUnitsBroadcastIndependentBalancesAndRanks()
    {
        var rig = new Rig(zombieConversionModeId: 4, pvePoints: true);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandlePvePangAsync(rig.Host, new(0), default);
            Assert.Empty(rig.PvePoints.To(rig.Host));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var i = 0; i < 200 && rig.PvePoints.To(rig.Guest).Length == 0; i++) await Task.Delay(10);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var initial = Assert.Single(rig.PvePoints.To(member));
                Assert.Equal(0, initial.Amount);
                Assert.Equal(new[] { 0, 1 }, initial.Rows.Select(row => row.Slot));
                Assert.All(initial.Rows, row => { Assert.Equal(0, row.Points); Assert.Equal(1, row.Rank); });
            }
            await rig.Round.HandlePvePangAsync(rig.Host, new(1), default);
            var first = rig.PvePoints.To(rig.Guest).Last();
            Assert.Equal(5, first.Amount);
            Assert.Equal(new[] { 5, 0 }, first.Rows.Select(row => row.Points));
            Assert.Equal(new[] { 1, 2 }, first.Rows.Select(row => row.Rank));
            await rig.Round.HandlePvePangAsync(rig.Guest, new(1), default);
            Assert.All(rig.PvePoints.To(rig.Host).Last().Rows, row => Assert.Equal(1, row.Rank));
            await rig.Round.HandlePvePangAsync(rig.Host, new(-1), default);
            await rig.Round.HandlePvePangAsync(rig.Host, new(64), default);
            Assert.Equal(3, rig.PvePoints.To(rig.Host).Length);
            Assert.Equal(0, rig.Registry.TryGet(rig.Host.Id)!.Scoring.Stats(first.Slot).Kills);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>All native unit awards accumulate independently and converted players cannot earn human points.</summary>
    [Fact]
    public async Task NativePveUnitsAccumulateAndRejectConvertedPlayers()
    {
        var rig = new Rig(zombieConversionModeId: 4, pvePoints: true,
            pvePointUnitValues: [4, 3, 10, 11, 15, 15, 20, 20, 15, 15, 20, 20, 15, 15, 20, 20]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var unit = 0; unit < 16; unit++)
                await rig.Round.HandlePvePangAsync(rig.Host, new(unit), default);
            await rig.Round.HandlePvePangAsync(rig.Guest, new(3), default);
            var last = rig.PvePoints.To(rig.Host).Last();
            Assert.Equal(new[] { 238, 11 }, last.Rows.Select(row => row.Points));
            Assert.Equal(new[] { 1, 2 }, last.Rows.Select(row => row.Rank));
            Assert.Equal(last.Rows, rig.PvePoints.To(rig.Guest).Last().Rows);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 20, true) { ActorSeat = 1 }, default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            Assert.Equal(15, rig.Registry.Find(rig.RoomId)!.FindByGame(rig.Guest.Id)!.Team);
            var count = rig.PvePoints.To(rig.Host).Length;
            await rig.Round.HandlePvePangAsync(rig.Guest, new(3), default);
            await rig.Round.HandlePvePangAsync(rig.Host, new(-1), default);
            await rig.Round.HandlePvePangAsync(rig.Host, new(16), default);
            Assert.Equal(count, rig.PvePoints.To(rig.Host).Length);
            Assert.Equal(new[] { 238, 11 }, rig.PvePoints.To(rig.Host).Last().Rows.Select(row => row.Points));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Accepts local kills from both human members and ignores stale completed-mission reports.</summary>
    [Fact]
    public async Task RescueCompletesForAllMembersAfterTheConfiguredHostileCount()
    {
        var rig = new Rig(zombieConversionModeId: 4, rescueMissions: true);
        try
        {
            await rig.StartAsync();
            Assert.Equal("killmonster", Assert.Single(Assert.Single(rig.RescuePrepared.To(rig.Guest)).Missions).Arguments[0]);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var i = 0; i < 200 && rig.RescueStarted.To(rig.Guest).Length == 0; i++) await Task.Delay(10);
            Assert.Single(rig.RescueStarted.To(rig.Guest));
            await rig.Round.HandleRescueProgressAsync(rig.Guest, new(1, 101), default);
            await rig.Round.HandleRescueProgressAsync(rig.Guest, new(0, 101), default);
            Assert.Empty(rig.RescueSucceeded.To(rig.Host));
            await rig.Round.HandleRescueProgressAsync(rig.Host, new(0, 102), default);
            Assert.Single(rig.RescueSucceeded.To(rig.Host));
            Assert.Single(rig.RescueSucceeded.To(rig.Guest));
            await rig.Round.HandleRescueProgressAsync(rig.Host, new(0, 102), default);
            Assert.Single(rig.RescueSucceeded.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A completed or failed mission leaves a quiet gap and refreshes the next native UI timer.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RescueCooldownBeginsAfterSuccessOrFailure(bool failed)
    {
        var rig = new Rig(zombieConversionModeId: 4, rescueMissions: true, rescueDurationMs: 5000, rescueCount: 2, rescueCooldownMs: 300);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var i = 0; i < 200 && rig.RescueStarted.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            if (failed) await rig.Round.HandleRescueProgressAsync(rig.Host, new(0, 100), default);
            else
            {
                await rig.Round.HandleRescueProgressAsync(rig.Host, new(0, 101), default);
                await rig.Round.HandleRescueProgressAsync(rig.Guest, new(0, 102), default);
            }
            // Measure the gap instead of sampling it after a fixed sleep: late scheduling can only lengthen it.
            var gap = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 1000 && rig.RescueStarted.To(rig.Host).Length < 2; i++) await Task.Delay(10);
            gap.Stop();
            Assert.Equal(2, rig.RescueStarted.To(rig.Host).Length);
            // The cooldown is measured on Environment.TickCount64, whose resolution can be about 16 ms.
            Assert.True(gap.ElapsedMilliseconds >= 280, $"next mission started {gap.ElapsedMilliseconds} ms after the previous one ended");
            Assert.Equal(1, rig.RescueStarted.To(rig.Guest)[1].MissionIndex);
            var prepared = rig.RescuePrepared.To(rig.Guest)[^1];
            Assert.True(prepared.Missions[1].StartTimeMs >= 330);
            Assert.Equal(5000, prepared.Missions[1].DurationMs);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A second round replaces rescue state and reinitializes zombie allegiance while retaining the chosen map.</summary>
    [Fact]
    public async Task RestartingTheSameMapGetsFreshRescueAndHumanState()
    {
        var rig = new Rig(zombieConversionModeId: 4, rescueMissions: true);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            var previous = room.Rescues;
            Assert.NotNull(previous);
            room.Seats[0].Team = 15;
            room.ResetToWaiting();
            Assert.Null(room.Rescues);
            Assert.Null(room.Supplies);
            await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
            Assert.Equal(2, room.StartGeneration);
            Assert.NotSame(previous, room.Rescues);
            Assert.Equal(2, room.Rescues!.Generation);
            Assert.Equal(-1, room.Rescues.ActiveIndex);
            Assert.All(room.Seats, seat => Assert.Equal(2, seat.Team));
            Assert.Equal(2, rig.RescuePrepared.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Native citizen death and expiry fail a mission exactly once and never emit success.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RescueFailsOnCitizenDeathOrItsDeadline(bool expires)
    {
        var rig = new Rig(zombieConversionModeId: 4, rescueMissions: true, rescueDurationMs: expires ? 200 : 5000);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var i = 0; i < 200 && rig.RescueStarted.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            if (!expires) await rig.Round.HandleRescueProgressAsync(rig.Host, new(0, 100), default);
            for (var i = 0; i < 200 && rig.RescueFailed.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            Assert.Equal(1, Assert.Single(rig.RescueFailed.To(rig.Host)).NextMissionIndex);
            Assert.Single(rig.RescueFailed.To(rig.Guest));
            Assert.Empty(rig.RescueSucceeded.To(rig.Host));
            await rig.Round.HandleRescueProgressAsync(rig.Guest, new(0, 101), default);
            Assert.Single(rig.RescueFailed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExplicitNpcPangCountsAKillWithoutChargingAStalePlayerVictim(bool earlierKnockdown)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "score-test", Plugin = "score-test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var score = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(score));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        try
        {
            var rig = new Rig(resultContext: context);
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            lock (room.Gate) { host.Team = 2; guest.Team = 2; }
            if (earlierKnockdown)
                await rig.Round.HandleKillCreditReportedAsync(rig.Host, new(host.Slot, guest.Slot, 1), default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host,
                new(host.Slot) { HasVictimReference = true, Victim = null }, default);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(0, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(1, room.Scoring.TeamTotals[host.Team - 1]);
            Assert.Single(score.To(rig.Guest).Last().Seats);
            // The NPC report consumes any outstanding pairing; a later compact report cannot reuse it.
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, new(host.Slot), default);
            Assert.Equal(2, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(0, room.Scoring.Stats(guest.Slot).Deaths);
        }
        finally { context.Unwind(); }
    }

    /// <summary>Checks ownership for reference-based ordinary credit and prevents NPC finishing reports from charging a paired player.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceBasedOrdinaryReportsRequireOwnScorer(bool npcFinishing)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "reference-score-test", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var score = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(score));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            lock (room.Gate) room.ModeId = 4;
            var credit = new RoundKillCreditReported(host.Slot, guest.Slot, 0) { RequiresReporterOwnership = true };
            var finishing = new RoundFinishingBlowReported(host.Slot)
                { Victim = npcFinishing ? null : guest.Slot, HasVictimReference = true, RequiresReporterOwnership = true };
            await rig.Round.HandleKillCreditReportedAsync(rig.Guest, credit, default);
            await rig.Round.HandleKillCreditReportedAsync(rig.Host, credit with { Victim = host.Slot }, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, finishing, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, finishing with { HasVictimReference = false }, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, finishing with { Victim = host.Slot }, default);
            Assert.Empty(score.To(rig.Host));
            await rig.Round.HandleKillCreditReportedAsync(rig.Host, credit, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, finishing, default);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Knockdowns);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(npcFinishing ? 0 : 1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(2, score.To(rig.Host).Length);
            Assert.Equal(2, score.To(rig.Guest).Length);
            Assert.Equal(npcFinishing ? 1 : 2, score.To(rig.Guest).Last().Seats.Count);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Checks mode and authenticated ownership admission before a direct kill updates either player's counters.</summary>
    [Theory]
    [InlineData(13, true)]
    [InlineData(14, true)]
    [InlineData(18, true)]
    [InlineData(22, false)]
    [InlineData(4, false)]
    public async Task DirectSurvivalKillsRequireConfiguredModeAndOwnReporter(int mode, bool admitted)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "direct-score-test", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var score = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(score));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, directKillModes: [13, 14, 18]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            lock (room.Gate) room.ModeId = checked((ushort)mode);
            var report = new RoundFinishingBlowReported(host.Slot)
                { HasVictimReference = true, Victim = guest.Slot, RequiresDirectKillMode = true };
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Guest, report, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, report with { Victim = host.Slot }, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, report with { HasVictimReference = false }, default);
            Assert.Empty(score.To(rig.Host));
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, report, default);
            Assert.Equal(admitted ? 1 : 0, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(admitted ? 1 : 0, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Knockdowns);
            Assert.Equal(admitted ? 1 : 0, score.To(rig.Host).Length);
            Assert.Equal(admitted ? 1 : 0, score.To(rig.Guest).Length);
            if (admitted)
            {
                Assert.Equal(2, score.To(rig.Guest)[0].Seats.Count);
                await rig.Round.HandleKillCreditReportedAsync(rig.Host, new(host.Slot, guest.Slot, 1), default);
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, report with { Victim = null }, default);
                Assert.Equal(2, room.Scoring.Stats(host.Slot).Kills);
                Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
                Assert.Single(score.To(rig.Guest).Last().Seats);
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task OnlyTheHostCanCompleteAllWavesAndBothPlayersWinRegardlessOfTeamTotals()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var room = rig.Registry.Find(rig.RoomId)!;
        room.Scoring.TeamTotals[0] = 0;
        room.Scoring.TeamTotals[1] = 50;
        var completion = new RoundEndRequested { Reason = RoundEndReason.WavesCleared };
        await rig.Round.HandleEndRequestedAsync(rig.Guest, completion, default);
        Assert.Equal(RoomPhase.Playing, room.Phase);
        await rig.Round.HandleEndRequestedAsync(rig.Host, completion, default);
        foreach (var member in new[] { rig.Host, rig.Guest })
        {
            var result = Assert.Single(rig.Ended.To(member));
            Assert.Equal(RoundOutcome.Won, result.Outcome);
            Assert.Equal(RoundEndReason.WavesCleared, result.Reason);
        }
    }

    [Theory]
    [InlineData(true, RoundOutcome.Won)]
    [InlineData(false, RoundOutcome.Lost)]
    public async Task CooperativeStageResultOverridesScoresOnlyForTheHost(bool success, RoundOutcome expected)
    {
        var rig = new Rig(cooperativeStageResultModes: [16, 101, 106]);
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var room = rig.Registry.Find(rig.RoomId)!;
        lock (room.Gate)
        {
            room.ModeId = 16;
            room.Scoring.TeamTotals[0] = 100;
            room.Scoring.TeamTotals[1] = 0;
        }
        var request = new RoundEndRequested { StageSucceeded = success };
        await rig.Round.HandleEndRequestedAsync(rig.Guest, request, default);
        Assert.Equal(RoomPhase.Playing, room.Phase);
        await rig.Round.HandleEndRequestedAsync(rig.Host, request, default);
        foreach (var member in new[] { rig.Host, rig.Guest })
            Assert.Equal(expected, Assert.Single(rig.Ended.To(member)).Outcome);
        await rig.Registry.StopAllAsync();
    }

    [Theory]
    [InlineData(RoundEndReason.WavesCleared, 101, true)]
    [InlineData(RoundEndReason.WavesCleared, 102, true)]
    [InlineData(RoundEndReason.WavesCleared, 5, false)]
    [InlineData(RoundEndReason.CooperativeFailed, 101, false)]
    [InlineData(RoundEndReason.TimeLimit, 101, false)]
    [InlineData(RoundEndReason.StageReported, 102, false)]
    public void OnlyAClearedWaveRoundMayOpenTheRewardDialog(RoundEndReason reason, int mode, bool opens)
    {
        var options = new InGameOptions { LotteryModes = [101, 102, 103] };
        Assert.Equal(opens, RoundFlow.LotteryAllowed(options, mode, reason));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllWavesReportInACooperativeModeWaitsForTheStageWord(bool clear)
    {
        var rig = new Rig(cooperativeStageResultModes: [16, 101, 102, 106]);
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var room = rig.Registry.Find(rig.RoomId)!;
        lock (room.Gate) room.ModeId = 101;
        // gcpReqEndAllWave first, then the stage word: the word decides, so a failure after it is still a failure.
        await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.WavesCleared }, default);
        Assert.Equal(RoomPhase.Playing, room.Phase);
        Assert.Empty(rig.Ended.To(rig.Host));
        await rig.Round.HandleEndRequestedAsync(rig.Host, new() { StageSucceeded = clear }, default);
        foreach (var member in new[] { rig.Host, rig.Guest })
        {
            var result = Assert.Single(rig.Ended.To(member));
            Assert.Equal(clear ? RoundOutcome.Won : RoundOutcome.Lost, result.Outcome);
            Assert.Equal(clear ? RoundEndReason.WavesCleared : RoundEndReason.CooperativeFailed, result.Reason);
        }
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task ZombieRoundStartsHumanAndOnlyADeadHumanConvertsOnce()
    {
        var rig = new Rig(zombieConversionModeId: 4);
        await rig.StartAsync();
        var room = rig.Registry.Find(rig.RoomId)!;
        Assert.All(room.Seats, seat => Assert.Equal(2, seat.Team));
        foreach (var member in new[] { rig.Host, rig.Guest })
            Assert.Equal(2, rig.TeamConverted.To(member).Length);
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
        Assert.Equal(2, room.FindByGame(rig.Guest.Id)!.Team);
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 20, true) { ActorSeat = 1 }, default);
        await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
        await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
        Assert.Equal(15, room.FindByGame(rig.Guest.Id)!.Team);
        foreach (var member in new[] { rig.Host, rig.Guest })
        {
            var changes = rig.TeamConverted.To(member);
            Assert.Equal(3, changes.Length);
            Assert.Equal(new RoomTeamConverted(1, 15), changes[^1]);
        }
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 20, true) { ActorSeat = 1 }, default);
        await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
        foreach (var member in new[] { rig.Host, rig.Guest })
            Assert.Equal(4, rig.TeamConverted.To(member).Length);
        await rig.Registry.StopAllAsync();
    }

    /// <summary>
    /// A build whose client times its own respawn (kr-812: gcpGameReqRespawn, then gcpRoomChangeTeam) converts on
    /// its death report alone; a human whose spawn was accepted is alive again and does not convert.
    /// </summary>
    [Fact]
    public async Task ClientTimedDeathConvertsAndARespawnedHumanDoesNot()
    {
        var rig = new Rig(zombieConversionModeId: 4);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandleDeathReportedAsync(rig.Host, new(1000000, 3200) { ActorSeat = 0 }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, new(1000000, 0) { ActorSeat = 0 }, default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new(), default);
            Assert.Equal(2, room.FindByGame(rig.Host.Id)!.Team);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 3200) { ActorSeat = 1 }, default);
            Assert.False(room.FindByGame(rig.Guest.Id)!.AwaitingRespawn);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var seat = room.FindByGame(rig.Guest.Id)!;
            Assert.Equal(15, seat.Team);
            Assert.Null(seat.LastDeathActor);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(new RoomTeamConverted(1, 15), rig.TeamConverted.To(member)[^1]);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Only a converted player can use native zombie revival, with the same allegiance notification.</summary>
    [Fact]
    public async Task NativeZombieRevivalDoesNotReviveAHumanOrConsumeARevivalItem()
    {
        var rig = new Rig(zombieConversionModeId: 4);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            Assert.False(await rig.Rooms.TryPveZombieRevivalAsync(rig.Guest, default));
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 20, true) { ActorSeat = 1 }, default);
            Assert.False(await rig.Rooms.TryPveZombieRevivalAsync(rig.Guest, default));
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 20, true) { ActorSeat = 1 }, default);
            Assert.True(await rig.Rooms.TryPveZombieRevivalAsync(rig.Guest, default));
            var seat = rig.Registry.Find(rig.RoomId)!.FindByGame(rig.Guest.Id)!;
            Assert.Equal(15, seat.Team);
            Assert.False(seat.AwaitingRespawn);
            Assert.Null(seat.LastDeathActor);
            Assert.False(seat.InstantRevivalPending);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                Assert.Equal(new RoomTeamConverted(1, 15), rig.TeamConverted.To(member)[^1]);
                Assert.Empty(rig.Ended.To(member));
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The last human conversion ends the cooperative round once, after broadcasting the conversion.</summary>
    [Fact]
    public async Task LastHumanConversionEndsTheRoundAsFailed()
    {
        var rig = new Rig(zombieConversionModeId: 4);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 20, true) { ActorSeat = 1 }, default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            Assert.Empty(rig.Ended.To(rig.Host));
            await rig.Round.HandleDeathReportedAsync(rig.Host, new(1000000, 20, true) { ActorSeat = 0 }, default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new(), default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new(), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(RoundOutcome.Lost, Assert.Single(rig.Ended.To(member)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private const byte ZombieHuman = 1;
    private const byte ZombieTeam = 14;

    private static Rig ZombiePvpRig(int kingDelayMs = 60_000, int timeLimitMs = 180_000, int ultraKind = 0,
        Func<TimeSpan, CancellationToken, ValueTask>? zombieKingDelay = null) =>
        new(configure: options =>
        {
            options.ZombiePvpModeIds = [4];
            options.ZombieKingDelayMs = kingDelayMs;
            options.ZombiePvpRoundTimeLimitMs = timeLimitMs;
            options.ZombieKingUltraItemKind = ultraKind;
        }, zombieKingDelay: zombieKingDelay);

    /// <summary>
    /// The king's ultra request (no object, the ultra's item kind) is answered with the item effect to the king
    /// alone; the same request from a human, or before there is a king, gets nothing.
    /// </summary>
    [Fact]
    public async Task ZombieKingUltraIsConfirmedToTheKingAlone()
    {
        var rig = ZombiePvpRig(kingDelayMs: 150, ultraKind: 104);
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(-1, 104), default);
            Assert.Empty(rig.CountedItemUsed.To(rig.Host));
            for (var i = 0; i < 300 && room.ZombieKingSlot is null; i++)
                await Task.Delay(10);
            var king = Assert.IsType<int>(room.ZombieKingSlot);
            var (kingSession, human) = room.Seats.Single(seat => seat.Slot == king).Game.Id == rig.Host.Id
                ? (rig.Host, rig.Guest) : (rig.Guest, rig.Host);
            await rig.Round.HandleSupplyPickupAsync(human, new(-1, 104), default);
            await rig.Round.HandleSupplyPickupAsync(kingSession, new(-1, 104), default);
            Assert.Equal(new RoundCountedItemUsed(king, 104), Assert.Single(rig.CountedItemUsed.To(kingSession)));
            Assert.Empty(rig.CountedItemUsed.To(human));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Seats an optional third player, starts the zombie battle and waits until it plays.</summary>
    private static async Task<RecordingSession?> StartZombiePvpAsync(Rig rig, bool third = false)
    {
        RecordingSession? extra = null;
        if (third)
        {
            extra = new RecordingSession(33);
            extra.SetLobbyPlayer(33, "third");
            await rig.StartAsync(extra);
        }
        else
            await rig.StartAsync();
        foreach (var member in new ISession?[] { rig.Host, rig.Guest, extra })
            if (member is not null)
                await rig.Round.HandleLoadReportedAsync(member, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        return extra;
    }

    private static async Task<RoundEnded> WaitEndedAsync(Rig rig, ISession member)
    {
        for (var i = 0; i < 300 && rig.Ended.To(member).Length == 0; i++)
            await Task.Delay(10);
        return Assert.Single(rig.Ended.To(member));
    }

    /// <summary>Every seat starts the zombie battle human, and every member is told so before the round.</summary>
    [Fact]
    public async Task ZombieBattleStartsEverySeatOnTheHumanTeam()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            Assert.All(room.Seats, seat => Assert.Equal(ZombieHuman, seat.Team));
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var changes = rig.TeamConverted.To(member);
                Assert.Equal(room.Seats.Select(seat => new RoomTeamConverted(seat.Slot, ZombieHuman)).OrderBy(change => change.Slot),
                    changes.OrderBy(change => change.Slot));
            }
            Assert.Equal(180_000, (await rig.WaitStartedAsync(rig.Guest)).TimeLimitMs);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>After the opening, the server turns one random human into the king and tells every member.</summary>
    [Fact]
    public async Task ZombieKingIsPickedAfterTheOpeningAndBroadcast()
    {
        var rig = ZombiePvpRig(kingDelayMs: 150);
        var opening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Round.RoundPlaying = (room, generation) =>
        {
            opening.TrySetResult(room.ZombieKingSlot is null);
            rig.Rooms.ScheduleZombieKing(room, generation);
        };
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            // Observe the opening boundary rather than assuming this continuation runs within 150 ms.
            Assert.True(await opening.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            for (var i = 0; i < 300 && room.ZombieKingSlot is null; i++)
                await Task.Delay(10);
            var king = Assert.IsType<int>(room.ZombieKingSlot);
            Assert.Single(room.Seats, seat => seat.Team == ZombieTeam);
            Assert.Equal(king, room.Seats.Single(seat => seat.Team == ZombieTeam).Slot);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                Assert.Equal(new RoomTeamConverted(king, ZombieTeam), rig.TeamConverted.To(member)[^1]);
                Assert.Empty(rig.Ended.To(member));
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A human who dies before the opening ends becomes the king, and the picker that was still waiting for the
    /// opening makes no second one. The picker's wait is the room flow's delay delegate, so the death report
    /// lands while the picker is parked and the retained task reports when the picker has finished.
    /// </summary>
    [Fact]
    public async Task ZombieBattleDeathBeforeTheOpeningMakesTheKing()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = ZombiePvpRig(kingDelayMs: 200, zombieKingDelay: (_, ct) => new(gate.Task.WaitAsync(ct)));
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var guest = room.FindByGame(rig.Guest.Id)!;
            Assert.Equal(guest.Slot, room.ZombieKingSlot);
            Assert.Equal(ZombieTeam, guest.Team);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(new RoomTeamConverted(guest.Slot, ZombieTeam), rig.TeamConverted.To(member)[^1]);
            var sent = rig.TeamConverted.To(rig.Host).Length;
            gate.SetResult(true);
            await room.ZombieKingTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(sent, rig.TeamConverted.To(rig.Host).Length);
            Assert.Equal(ZombieHuman, room.FindByGame(rig.Host.Id)!.Team);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// The picker does nothing while its configured delay is still pending - no seat changes team and the
    /// retained task is still running - and announces exactly one king once the delay completes.
    /// </summary>
    [Fact]
    public async Task ZombieKingPickerActsOnlyWhenTheDelayCompletes()
    {
        var entered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = ZombiePvpRig(kingDelayMs: 200, zombieKingDelay: (delay, ct) =>
        {
            entered.TrySetResult(delay);
            return new(gate.Task.WaitAsync(ct));
        });
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            Assert.Equal(TimeSpan.FromMilliseconds(200), await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(room.ZombieKingTask.IsCompleted);
            Assert.Null(room.ZombieKingSlot);
            var sent = rig.TeamConverted.To(rig.Host).Length;
            Assert.All(room.Seats, seat => Assert.Equal(ZombieHuman, seat.Team));
            gate.SetResult(true);
            await room.ZombieKingTask.WaitAsync(TimeSpan.FromSeconds(3));
            var king = Assert.IsType<int>(room.ZombieKingSlot);
            Assert.Equal(sent + 1, rig.TeamConverted.To(rig.Host).Length);
            Assert.Equal(new RoomTeamConverted(king, ZombieTeam), rig.TeamConverted.To(rig.Host)[^1]);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A picker still waiting for its delay when the room is torn down ends instead of hanging: the retained task
    /// completes, no king is chosen and nothing is announced after the teardown.
    /// </summary>
    [Fact]
    public async Task ZombieKingPickerCompletesWhenTheRoomIsTornDown()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = ZombiePvpRig(kingDelayMs: 200, zombieKingDelay: (_, ct) =>
        {
            entered.TrySetResult(true);
            return new(gate.Task.WaitAsync(ct));
        });
        var stopped = false;
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            var picker = room.ZombieKingTask;
            Assert.True(await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(picker.IsCompleted);
            var sent = rig.TeamConverted.To(rig.Host).Length;
            await rig.Registry.StopAllAsync();
            stopped = true;
            gate.SetResult(true);
            await picker.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(room.ZombieKingSlot);
            Assert.Equal(sent, rig.TeamConverted.To(rig.Host).Length);
        }
        finally
        {
            if (!stopped)
                await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>
    /// A dead human turns into a zombini and a dead zombini comes back without limit, deciding nothing; the
    /// king never comes back, and its fall gives the humans the round.
    /// </summary>
    [Fact]
    public async Task ZombieBattleZombinisReviveAndTheKingsFallWinsForTheHumans()
    {
        var rig = ZombiePvpRig();
        try
        {
            var third = (await StartZombiePvpAsync(rig, third: true))!;
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var thirdSlot = room.FindByGame(third.Id)!.Slot;
            await rig.Rooms.HandleTeamChangeAsync(third, new(), default);
            Assert.Equal(ZombieTeam, room.FindByGame(third.Id)!.Team);
            Assert.NotEqual(thirdSlot, room.ZombieKingSlot);
            await rig.Rooms.HandleTeamChangeAsync(third, new(), default);
            await rig.Rooms.HandleTeamChangeAsync(third, new(), default);
            foreach (var member in new ISession[] { rig.Host, rig.Guest, third })
            {
                Assert.Equal(3, rig.TeamConverted.To(member).Count(change => change == new RoomTeamConverted(thirdSlot, ZombieTeam)));
                Assert.Empty(rig.Ended.To(member));
            }
            var sent = rig.TeamConverted.To(rig.Host).Length;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            Assert.Equal(sent, rig.TeamConverted.To(rig.Host).Length);
            Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, third)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A king who leaves the room gives the humans the round at once.</summary>
    [Fact]
    public async Task ZombieBattleKingLeavingWinsForTheHumans()
    {
        var rig = ZombiePvpRig();
        try
        {
            var third = (await StartZombiePvpAsync(rig, third: true))!;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            Assert.Empty(rig.Ended.To(rig.Host));
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, third)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>When the last human turns, the zombies win at once.</summary>
    [Fact]
    public async Task ZombieBattleLastHumanTurningWinsForTheZombies()
    {
        var rig = ZombiePvpRig();
        try
        {
            var third = (await StartZombiePvpAsync(rig, third: true))!;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            await rig.Rooms.HandleTeamChangeAsync(third, new(), default);
            Assert.Empty(rig.Ended.To(rig.Host));
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            Assert.All(room.Seats, seat => Assert.Equal(ZombieTeam, seat.Team));
            foreach (var member in new ISession[] { rig.Host, rig.Guest, third })
                Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, member)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>When time runs out with a human left, the humans win.</summary>
    [Fact]
    public async Task ZombieBattleTimeOutWithAHumanLeftWinsForTheHumans()
    {
        var rig = ZombiePvpRig(timeLimitMs: 400);
        try
        {
            await StartZombiePvpAsync(rig);
            Assert.Equal(400, (await rig.WaitStartedAsync(rig.Host)).TimeLimitMs);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            Assert.Empty(rig.Ended.To(rig.Host));
            var host = await WaitEndedAsync(rig, rig.Host);
            Assert.Equal(RoundEndReason.TimeLimit, host.Reason);
            Assert.Equal(RoundOutcome.Won, host.Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The server decides the zombie battle, so a client's stage-end report does not end it.</summary>
    [Fact]
    public async Task ZombieBattleIgnoresTheClientStageEndReport()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            await rig.Round.HandleEndRequestedAsync(rig.Guest, new() { Reason = RoundEndReason.StageReported }, default);
            Assert.Equal(RoomPhase.Playing, rig.Registry.Find(rig.RoomId)!.Phase);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData(new[] { 0 }, 0)]
    [InlineData(new[] { 8, 8 }, 0)]
    [InlineData(new[] { 16 }, 16)]
    [InlineData(new[] { 70000 }, 0)]
    public void ZombieBattleOptionsRejectInvalidModes(int[] modes, int conversionMode)
    {
        Assert.Empty(new InGameOptions { ZombiePvpModeIds = [8], ZombieConversionModeId = 16 }.Validate());
        Assert.Contains(new InGameOptions { ZombiePvpModeIds = modes, ZombieConversionModeId = conversionMode }.Validate(),
            result => result.ErrorMessage!.Contains("zombie_pvp_mode_ids"));
        Assert.Contains(new InGameOptions { StarModeIds = [5], StarRoundTimeLimitMs = 100, StarFeverLeadMs = 100 }.Validate(),
            result => result.ErrorMessage!.Contains("star_fever_lead_ms"));
    }

    private static Rig StarRig(bool team = false, int timeLimitMs = 180_000, int feverLeadMs = 45_000,
        string starDropPointsDirectory = "") =>
        new(configure: options =>
        {
            if (team) options.StarTeamModeIds = [4];
            else options.StarModeIds = [4];
            options.StarRoundTimeLimitMs = timeLimitMs;
            options.StarFeverLeadMs = feverLeadMs;
            options.StarDropPointsDirectory = starDropPointsDirectory;
        });

    private static readonly RoundLucciPoint StarPoint = new(1, 2, 3, 0, 1, 0);

    private static async Task ReportStarsAsync(Rig rig, ISession member, RoundStarChange change, int times = 1)
    {
        for (var i = 0; i < times; i++)
            await rig.Round.HandleStarReportedAsync(member, new RoundStarReported(0, change), default);
    }

    /// <summary>A dropped star goes to every member, the dropper too, and only the first claim removes it.</summary>
    [Fact]
    public async Task StarDropReachesEveryMemberAndOnlyTheFirstClaimRemovesIt()
    {
        var rig = StarRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Round.HandleStarDropAsync(rig.Host, new RoundStarDropRequested(StarPoint, 2, 0), default);
            var created = Assert.Single(rig.StarCreated.To(rig.Host));
            Assert.Equal(created, Assert.Single(rig.StarCreated.To(rig.Guest)));
            Assert.Equal(2, created.Kind);
            Assert.Equal(StarPoint, created.Point);
            Assert.Equal(0, created.Tag);
            await rig.Round.HandleStarPickupAsync(rig.Guest, new RoundStarPickupRequested(1_100_000, created.ObjectId), default);
            await rig.Round.HandleStarPickupAsync(rig.Host, new RoundStarPickupRequested(1_000_000, created.ObjectId), default);
            Assert.Equal(new RoundStarPicked(1_100_000, created.ObjectId), Assert.Single(rig.StarPicked.To(rig.Host)));
            Assert.Single(rig.StarPicked.To(rig.Guest));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rig.Round.HandleStarDropAsync(rig.Host, new RoundStarDropRequested(StarPoint, 3, 0), default).AsTask());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The T-45 Fever packet precedes one appended second wave with fresh ids and the same seeds.</summary>
    [Fact]
    public async Task StarFeverAppendsSecondWaveWithFreshObjectIds()
    {
        var rig = StarRig(timeLimitMs: 400, feverLeadMs: 300);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var secondSeedPoint = new RoundLucciPoint(4, 5, 6, 0, 0, 2);
            await rig.Round.HandleStarDropAsync(rig.Host, new RoundStarDropRequested(StarPoint, 1, 3), default);
            await rig.Round.HandleStarDropAsync(rig.Guest, new RoundStarDropRequested(secondSeedPoint, 2, 4), default);

            for (var i = 0; i < 100 && rig.StarCreated.To(rig.Host).Length < 4; i++)
                await Task.Delay(10);
            Assert.Single(rig.StarFever.To(rig.Host));
            Assert.Single(rig.StarFever.To(rig.Guest));
            var sent = rig.StarCreated.To(rig.Host);
            Assert.Equal(4, sent.Length);
            var expectedSeeds = new[] { (1, StarPoint, 3), (2, secondSeedPoint, 4) };
            Assert.Equal(expectedSeeds, sent.Take(2).Select(created => (created.Kind, created.Point, created.Tag)));
            Assert.Equal(expectedSeeds, sent.Skip(2).Select(created => (created.Kind, created.Point, created.Tag)));
            Assert.True(sent[2].ObjectId > sent[1].ObjectId);
            Assert.True(sent[3].ObjectId > sent[2].ObjectId);
            var room = rig.Registry.Find(rig.RoomId)!;
            Assert.Equal(4, room.Stars!.Items.Count);
            Assert.Contains(sent[2].ObjectId, room.Stars.Items.Keys);
            Assert.Contains(sent[3].ObjectId, room.Stars.Items.Keys);

            await Task.Delay(150);
            Assert.Single(rig.StarFever.To(rig.Host));
            Assert.Equal(4, rig.StarCreated.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A captured map first wave is created once and the T-45 second wave copies exactly those seeds.</summary>
    [Fact]
    public async Task StarDropPointsCreateFirstWaveAndFeverCopiesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-star-points-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "map.json"), """
            {"small":[{"x":10,"y":20,"z":30},{"x":40,"y":50,"z":60}],"big":[{"x":70,"y":80,"z":90}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "stale-selection.json"), """
            {"small":[{"x":1000,"y":2000,"z":3000}],"big":[]}
            """);
        var rig = StarRig(timeLimitMs: 1000, feverLeadMs: 800, starDropPointsDirectory: directory);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.MapKey = "stale-selection";
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var i = 0; i < 100 && rig.StarCreated.To(rig.Host).Length < 3; i++) await Task.Delay(10);
            var first = rig.StarCreated.To(rig.Host);
            Assert.Equal(new[] { 1, 1, 2 }, first.Select(star => star.Kind));
            Assert.Equal(new[] { 3, 3, 4 }, first.Select(star => star.Tag));

            for (var i = 0; i < 200 && rig.StarCreated.To(rig.Host).Length < 6; i++) await Task.Delay(10);
            Assert.Single(rig.StarFever.To(rig.Host));
            var sent = rig.StarCreated.To(rig.Host);
            Assert.Equal(6, sent.Length);
            Assert.Equal(first.Select(star => (star.Kind, star.Point, star.Tag)),
                sent.Skip(3).Select(star => (star.Kind, star.Point, star.Tag)));
            Assert.True(sent[3].ObjectId > sent[2].ObjectId);
            Assert.Equal(6, room.Stars!.Items.Count);
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Outside the star modes the star packets and reports change nothing.</summary>
    [Fact]
    public async Task StarPacketsAreIgnoredOutsideTheStarModes()
    {
        var rig = new Rig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Round.HandleStarDropAsync(rig.Host, new RoundStarDropRequested(StarPoint, 1, 0), default);
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallGained);
            Assert.Empty(rig.StarCreated.To(rig.Host));
            var room = rig.Registry.Find(rig.RoomId)!;
            Assert.False(room.Scoring.CountsStars);
            Assert.Null(room.Scoring.Stats(room.FindByGame(rig.Host.Id)!.Slot).Stars);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// Each report moves the reporter's own count: a big star is worth ten, a count never drops below zero, and
    /// the starting stars reported while the map loads count too.
    /// </summary>
    [Fact]
    public async Task StarReportsMoveTheReportersCount()
    {
        var rig = StarRig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallGained, 10);
            Assert.Equal(10, room.Scoring.Stars(room.FindByGame(rig.Host.Id)!.Slot));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.BigGained);
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallLost, 3);
            Assert.Equal(17, room.Scoring.Stars(room.FindByGame(rig.Host.Id)!.Slot));
            await ReportStarsAsync(rig, rig.Guest, RoundStarChange.BigLost);
            var guest = room.Scoring.Stats(room.FindByGame(rig.Guest.Id)!.Slot);
            Assert.Equal(0, guest.Stars);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>In the individual mode the seat holding the most stars wins when time runs out.</summary>
    [Fact]
    public async Task IndividualStarRoundGoesToTheSeatWithTheMostStars()
    {
        var rig = StarRig(timeLimitMs: 400);
        try
        {
            await StartZombiePvpAsync(rig);
            Assert.Equal(400, (await rig.WaitStartedAsync(rig.Host)).TimeLimitMs);
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallGained, 3);
            await ReportStarsAsync(rig, rig.Guest, RoundStarChange.BigGained);
            Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Host)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Equal star counts in the individual mode are a draw for everyone.</summary>
    [Fact]
    public async Task IndividualStarRoundWithEqualCountsIsADraw()
    {
        var rig = StarRig(timeLimitMs: 300);
        try
        {
            await StartZombiePvpAsync(rig);
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallGained, 2);
            await ReportStarsAsync(rig, rig.Guest, RoundStarChange.SmallGained, 2);
            Assert.Equal(RoundOutcome.Draw, (await WaitEndedAsync(rig, rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Draw, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>In the team mode the team whose seats hold more stars wins, whatever the kill totals say.</summary>
    [Fact]
    public async Task TeamStarRoundGoesToTheTeamWithMoreStars()
    {
        var rig = StarRig(team: true, timeLimitMs: 400);
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate)
            {
                room.FindByGame(rig.Host.Id)!.Team = 1;
                room.FindByGame(rig.Guest.Id)!.Team = 2;
                room.Scoring.AddKill(room.FindByGame(rig.Host.Id)!.Slot, 1);
            }
            await ReportStarsAsync(rig, rig.Guest, RoundStarChange.SmallGained, 4);
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallGained, 1);
            Assert.Equal(new[] { 1, 4 }, room.Scoring.StarTeamTotals);
            Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Host)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData(new[] { 5 }, new[] { 5 })]
    [InlineData(new[] { 8 }, new int[0])]
    [InlineData(new[] { 0 }, new int[0])]
    public void StarOptionsRejectInvalidModes(int[] modes, int[] teamModes)
    {
        Assert.Empty(new InGameOptions { StarModeIds = [5], StarTeamModeIds = [6], ZombiePvpModeIds = [8] }.Validate());
        Assert.Contains(new InGameOptions { StarModeIds = modes, StarTeamModeIds = teamModes, ZombiePvpModeIds = [8] }.Validate(),
            result => result.ErrorMessage!.Contains("star_mode_ids"));
    }

    /// <summary>A mission whose duration would overlap Boss spawning is never announced or spawned.</summary>
    [Fact]
    public async Task RescueSchedulingStopsBeforeTheBossPhase()
    {
        var rig = new Rig(zombieConversionModeId: 4, rescueMissions: true, rescueDurationMs: 1000, rescueCount: 2,
            rescueCooldownMs: 5000, rescueStopTimeMs: 2000);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var i = 0; i < 200 && rig.RescueStarted.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            await rig.Round.HandleRescueProgressAsync(rig.Host, new(0, 101), default);
            await rig.Round.HandleRescueProgressAsync(rig.Guest, new(0, 102), default);
            var state = rig.Registry.Find(rig.RoomId)!.Rescues!;
            await state.Worker!.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Single(rig.RescueStarted.To(rig.Host));
            Assert.Single(rig.RescueStarted.To(rig.Guest));
            Assert.Single(rig.RescueSucceeded.To(rig.Host));
            Assert.Empty(rig.RescueFailed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task OrdinaryModeDoesNotUseTheCooperativeSuccessFlag()
    {
        var rig = new Rig(cooperativeStageResultModes: [16, 101, 106]);
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        await rig.Round.HandleEndRequestedAsync(rig.Host, new() { StageSucceeded = true }, default);
        foreach (var member in new[] { rig.Host, rig.Guest })
            Assert.Equal(RoundOutcome.Draw, Assert.Single(rig.Ended.To(member)).Outcome);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task DisabledZombieConversionNeverConvertsADefaultModePlayer()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var room = rig.Registry.Find(rig.RoomId)!;
        lock (room.Gate) room.ModeId = 0;
        var previousTeam = room.FindByGame(rig.Guest.Id)!.Team;
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1000001, 20, true) { ActorSeat = 1 }, default);
        await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
        Assert.Equal(previousTeam, room.FindByGame(rig.Guest.Id)!.Team);
        Assert.Empty(rig.TeamConverted.To(rig.Guest));
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task DroppedItemsFromEitherPlayerReachBothAndCannotBePickedTwice()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, 0, 0, 0), 10000), default);
        var box = Assert.Single(rig.SupplyCreated.To(rig.Host));
        Assert.Equal(box, Assert.Single(rig.SupplyCreated.To(rig.Guest)));
        Assert.Equal(0, box.Point.Kind);
        await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, 1), default);
        Assert.Empty(rig.SupplyPicked.To(rig.Host));
        await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, 0), default);
        await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(box.ObjectId, 0), default);
        Assert.Single(rig.SupplyPicked.To(rig.Host));
        Assert.Single(rig.SupplyPicked.To(rig.Guest));
    }

    private static async Task<PlayRoom> StartedRoundAsync(Rig rig)
    {
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        return rig.Registry.Find(rig.RoomId)!;
    }

    [Fact]
    public async Task NpcLucciDropsReachBothPlayersAndOnlyTheFirstPickupWins()
    {
        var rig = new Rig();
        var room = await StartedRoundAsync(rig);
        var point = new RoundLucciPoint(1, 2, 3, 4, 5, 6);
        await rig.Round.HandleLucciDropAsync(rig.Host, new(point, 2, 9), default);
        var item = Assert.Single(rig.LucciCreated.To(rig.Host));
        Assert.Equal(item, Assert.Single(rig.LucciCreated.To(rig.Guest)));
        Assert.Equal((2, 9, point), (item.Kind, item.Tag, item.Point));
        Assert.True(item.ObjectId > new InGameOptions().LucciObjectIdBase);
        await rig.Round.HandleLucciPickupAsync(rig.Guest, new(1000100, item.ObjectId), default);
        await rig.Round.HandleLucciPickupAsync(rig.Host, new(1000000, item.ObjectId), default);
        var picked = new RoundLucciPicked(1000100, item.ObjectId);
        Assert.Equal(picked, Assert.Single(rig.LucciPicked.To(rig.Host)));
        Assert.Equal(picked, Assert.Single(rig.LucciPicked.To(rig.Guest)));
        Assert.Equal(100, room.Scoring.Lucci(room.FindByGame(rig.Guest.Id)!.Slot));
        Assert.Equal(0, room.Scoring.Lucci(room.FindByGame(rig.Host.Id)!.Slot));
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task HordeAwardRequestsCreateTheConfiguredAwardAndCreditItsValue()
    {
        var rig = new Rig(configure: options => options.HordeAwardKind = 5);
        var room = await StartedRoundAsync(rig);
        await rig.Round.HandleHordeAwardAsync(rig.Guest, new(new(1, 2, 3, 0, 0, 0), 4), default);
        var award = Assert.Single(rig.LucciCreated.To(rig.Host));
        Assert.Equal((5, 4), (award.Kind, award.Tag));
        await rig.Round.HandleLucciPickupAsync(rig.Host, new(1, award.ObjectId), default);
        Assert.Equal(10, room.Scoring.Lucci(room.FindByGame(rig.Host.Id)!.Slot));
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task LucciRequestsOutsideARunningRoundOrWithAnUnknownItemAreIgnored()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLucciDropAsync(rig.Host, new(new(0, 0, 0, 0, 0, 0), 1, 0), default);
        Assert.Empty(rig.LucciCreated.To(rig.Host));
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var room = rig.Registry.Find(rig.RoomId)!;
        await rig.Round.HandleLucciPickupAsync(rig.Host, new(1, 0x63000001), default);
        Assert.Empty(rig.LucciPicked.To(rig.Host));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Round.HandleLucciDropAsync(rig.Host, new(new(0, 0, 0, 0, 0, 0), 3, 0), default));
        Assert.Empty(rig.LucciCreated.To(rig.Host));
        room.ResetToWaiting();
        Assert.Null(room.Lucci);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task UnconsumedLucciDropsAreBoundedAndPickupsFreeRoom()
    {
        var rig = new Rig(configure: options => options.MaxDroppedLucci = 2);
        await StartedRoundAsync(rig);
        for (var i = 0; i < 3; i++)
            await rig.Round.HandleLucciDropAsync(rig.Host, new(new(i, 0, 0, 0, 0, 0), 1, i), default);
        var created = rig.LucciCreated.To(rig.Host);
        Assert.Equal(2, created.Length);
        Assert.Equal(2, created.Select(item => item.ObjectId).Distinct().Count());
        await rig.Round.HandleLucciPickupAsync(rig.Host, new(1, created[0].ObjectId), default);
        await rig.Round.HandleLucciDropAsync(rig.Host, new(new(9, 0, 0, 0, 0, 0), 1, 9), default);
        Assert.Equal(3, rig.LucciCreated.To(rig.Host).Length);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task PickedLucciIsCappedPerSeatButEveryItemIsStillConsumed()
    {
        var rig = new Rig(configure: options => options.MaxRoundLucci = 150);
        var room = await StartedRoundAsync(rig);
        for (var i = 0; i < 2; i++)
        {
            await rig.Round.HandleLucciDropAsync(rig.Guest, new(new(0, 0, 0, 0, 0, 0), 2, 0), default);
            await rig.Round.HandleLucciPickupAsync(rig.Host, new(1, rig.LucciCreated.To(rig.Host).Last().ObjectId), default);
        }
        Assert.Equal(2, rig.LucciPicked.To(rig.Host).Length);
        Assert.Equal(150, room.Scoring.Lucci(room.FindByGame(rig.Host.Id)!.Slot));
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task PickedLucciIsAddedToTheSeatsRoundReward()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "lucci-reward-test", Plugin = "lucci-reward-test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options => { options.LevelExpThresholds = [0, 100, 500]; options.MaxLevel = 3; });
        try
        {
            var room = await StartedRoundAsync(rig);
            var slot = room.FindByGame(rig.Host.Id)!.Slot;
            var before = rig.Round.ComputeRewards(room, room.StartGeneration, RoundEndReason.StageReported)[slot];
            await rig.Round.HandleLucciDropAsync(rig.Guest, new(new(0, 0, 0, 0, 0, 0), 2, 0), default);
            await rig.Round.HandleLucciPickupAsync(rig.Host, new(1, rig.LucciCreated.To(rig.Host).Single().ObjectId), default);
            var after = rig.Round.ComputeRewards(room, room.StartGeneration, RoundEndReason.StageReported)[slot];
            Assert.Equal(before.LucciEarned + 100, after.LucciEarned);
            Assert.Equal(before.ExpEarned, after.ExpEarned);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task RandomBoxAcceptsItsResolvedKindGrantsTheSlotAndUsesItOnlyOnce()
    {
        var rig = new Rig(randomSupplyItemKinds: [101, 109]);
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
        Assert.Empty(rig.ItemUsed.To(rig.Host));
        await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, 0, 0, 0), 10000), default);
        var box = Assert.Single(rig.SupplyCreated.To(rig.Host));
        await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, 999999), default);
        Assert.Empty(rig.SupplyPicked.To(rig.Host));
        await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, 101), default);
        await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(box.ObjectId, 109), default);
        foreach (var player in new[] { rig.Host, rig.Guest })
            Assert.Equal(new RoundSupplyPicked(0, box.ObjectId) { ItemKind = 101 }, Assert.Single(rig.SupplyPicked.To(player)));
        await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
        await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
        await rig.Round.HandleItemUseAsync(rig.Guest, new(), default);
        foreach (var player in new[] { rig.Host, rig.Guest })
            Assert.Equal(new RoundItemUsed(0) { Kind = 101 }, Assert.Single(rig.ItemUsed.To(player)));
        await rig.Registry.StopAllAsync();
    }

    /// <summary>Preserves two held kinds across a full inventory, selection, use and death instead of overwriting the displayed item.</summary>
    [Fact]
    public async Task TwoItemSlotsPreserveKindsSwitchSelectionAndClearOnDeath()
    {
        var rig = new Rig(randomSupplyItemKinds: [101, 109, 112], nativeItemSlotCount: 2);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            async Task<int> Drop(int kind)
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, kind, 0, 0), 10000), default);
                return rig.SupplyCreated.To(rig.Host).Last().ObjectId;
            }
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(await Drop(101), 101), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(await Drop(109), 109), default);
            Assert.Equal(new[] { 0, 1 }, rig.SupplyPicked.To(rig.Host).Select(item => item.ItemSlot));
            var waitingBox = await Drop(112);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(waitingBox, 112), default);
            Assert.Equal(2, rig.SupplyPicked.To(rig.Host).Length);
            await rig.Round.HandleItemSlotSwitchAsync(rig.Host, new(), default);
            Assert.Equal(new RoundItemSlotChanged(0, 1, 109), Assert.Single(rig.ItemSlotChanged.To(rig.Guest)));
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Equal(new RoundItemUsed(0) { Kind = 109, ItemSlot = 1 }, Assert.Single(rig.ItemUsed.To(rig.Guest)));
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Single(rig.ItemUsed.To(rig.Guest));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(waitingBox, 112), default);
            Assert.Equal(1, rig.SupplyPicked.To(rig.Host).Last().ItemSlot);
            await rig.Round.HandleItemSlotSwitchAsync(rig.Host, new(), default);
            Assert.Equal(new RoundItemSlotChanged(0, 0, 101), rig.ItemSlotChanged.To(rig.Guest).Last());
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Equal(101, rig.ItemUsed.To(rig.Guest).Last().Kind);
            await rig.Round.HandleDeathReportedAsync(rig.Host, new(1000000, 0) { ActorSeat = 0 }, default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Equal(2, rig.ItemUsed.To(rig.Guest).Length);
            await rig.Round.HandleItemSlotSwitchAsync(rig.Host, new(), default);
            Assert.True(rig.ItemSlotChanged.To(rig.Host).Last().Rejected);
            Assert.Equal(2, rig.ItemSlotChanged.To(rig.Guest).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Counts repeated pickups by kind, resolves native use indexes, and isolates each player's stock.</summary>
    [Fact]
    public async Task CooperativeItemsAccumulateAndConsumeOnlyTheirOwnersCount()
    {
        var rig = new Rig(randomSupplyItemKinds: [120, 102], countedItemKinds: [120, 102]);
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        foreach (var player in new[] { rig.Host, rig.Guest })
            Assert.Equal(new[] { 120, 102 }, Assert.Single(rig.CountedItemsPrepared.To(player)).Kinds);
        for (var i = 0; i < 2; i++)
        {
            await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, 0, 0, 0), 10000), default);
            var box = rig.SupplyCreated.To(rig.Host).Last();
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, 120), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(box.ObjectId, 120), default);
        }
        await rig.Round.HandleCountedItemUseAsync(rig.Guest, new(1), default);
        await rig.Round.HandleCountedItemUseAsync(rig.Host, new(-1), default);
        await rig.Round.HandleCountedItemUseAsync(rig.Host, new(120), default);
        await rig.Round.HandleCountedItemUseAsync(rig.Host, new(0), default);
        await rig.Round.HandleCountedItemUseAsync(rig.Host, new(2), default);
        Assert.Empty(rig.CountedItemUsed.To(rig.Host));
        await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
        Assert.Empty(rig.ItemUsed.To(rig.Host));
        await rig.Round.HandleCountedItemUseAsync(rig.Host, new(1), default);
        await rig.Round.HandleCountedItemUseAsync(rig.Host, new(1), default);
        await rig.Round.HandleCountedItemUseAsync(rig.Host, new(1), default);
        Assert.Equal(2, rig.CountedItemUsed.To(rig.Host).Length);
        Assert.All(rig.CountedItemUsed.To(rig.Host), used => Assert.Equal(new RoundCountedItemUsed(0, 120), used));
        Assert.Empty(rig.CountedItemUsed.To(rig.Guest));
        await rig.Registry.StopAllAsync();
    }

    /// <summary>Keeps three cooperative counts separate from immediate pickups and ordinary held slots.</summary>
    [Fact]
    public async Task ThreeCooperativeSlotsDoNotHoldImmediateBoostOrGrenadePickups()
    {
        var rig = new Rig(randomSupplyItemKinds: [229, 146, 136, 101],
            countedItemKinds: [229, 146, 136], countedImmediateItemKinds: [101, 17]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            Assert.Equal(new[] { 229, 146, 136 }, Assert.Single(rig.CountedItemsPrepared.To(rig.Host)).Kinds);
            foreach (var kind in new[] { 229, 146, 136, 101, 17 })
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, kind == 17 ? 0 : kind, 0, 0), 10000), default);
                var id = rig.SupplyCreated.To(rig.Host).Last().ObjectId;
                await rig.Round.HandleSupplyPickupAsync(rig.Host, new(id, kind), default);
                await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(id, kind), default);
            }
            Assert.Equal(new[] { 229, 146, 136, 0, 0 }, rig.SupplyPicked.To(rig.Host).Select(pick => pick.ItemKind));
            Assert.Equal(new[] { 101, 17 }, rig.CountedItemUsed.To(rig.Host).Select(use => use.Kind));
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Empty(rig.ItemUsed.To(rig.Host));
            foreach (var slot in new[] { 1, 2, 3, 1, 2, 3, 4 })
                await rig.Round.HandleCountedItemUseAsync(rig.Host, new(slot), default);
            Assert.Equal(new[] { 101, 17, 229, 146, 136 }, rig.CountedItemUsed.To(rig.Host).Select(use => use.Kind));
            Assert.Empty(rig.CountedItemUsed.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static Rig ItemModeRig(int[]? modeIds = null) => new(randomSupplyItemKinds: [101, 109, 138], nativeItemSlotCount: 2,
        configure: options => { options.ItemModeIds = modeIds ?? [4]; options.ItemModeKinds = [101, 138]; });

    private static async Task<RoundSupplyCreated[]> WaitSuppliesAsync(Rig rig, ISession member, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.SupplyCreated.To(member).Length < count && DateTime.UtcNow < deadline) await Task.Delay(10);
        return rig.SupplyCreated.To(member);
    }

    /// <summary>Item mode keeps the points, thrown boxes and pickups of the kinds the build's item sheet offers and drops the rest.</summary>
    [Fact]
    public async Task ItemModeAcceptsOnlyTheBuildsKinds()
    {
        var rig = ItemModeRig();
        try
        {
            await rig.StartAsync();
            // Kind 112 is not in this build's item sheet, 138 is, and 0 is a random box.
            await rig.Round.HandleSuppliesReportedAsync(rig.Host,
                new([new(1, 2, 3, 112, 10, 10), new(4, 5, 6, 138, 10, 10), new(7, 8, 9, 0, 10, 10)]), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            Assert.Equal(new[] { 138, 0 }, room.Supplies!.Points.Select(point => point.Point.Kind));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var scheduled = await WaitSuppliesAsync(rig, rig.Guest, 2);
            Assert.Equal(new[] { 138, 0 }, scheduled.Take(2).Select(supply => supply.Point.Kind));
            var before = rig.SupplyCreated.To(rig.Host).Length;
            await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, 112, 0, 0), 10000), default);
            Assert.Equal(before, rig.SupplyCreated.To(rig.Host).Length);
            await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, 101, 0, 0), 10000), default);
            var thrown = rig.SupplyCreated.To(rig.Host).Single(supply => supply.Point.Kind == 101);
            // Kind 109 is a slot item of the build but not of its item mode.
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(thrown.ObjectId, 109), default);
            Assert.Empty(rig.SupplyPicked.To(rig.Host));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(thrown.ObjectId, 101), default);
            Assert.Equal(new RoundSupplyPicked(0, thrown.ObjectId) { ItemKind = 101 }, Assert.Single(rig.SupplyPicked.To(rig.Host)));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A box a team owns is eaten only by that team in an item mode; the other team's claim changes nothing.</summary>
    [Fact]
    public async Task ItemModeTeamBoxIsEatenOnlyByItsTeam()
    {
        var rig = ItemModeRig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate)
            {
                room.FindByGame(rig.Host.Id)!.Team = 1;
                room.FindByGame(rig.Guest.Id)!.Team = 2;
            }
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([new(1, 2, 3, 0, 10, 10, 2)]), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var box = (await WaitSuppliesAsync(rig, rig.Host, 1))[0];
            Assert.Equal(2, box.Point.Team);
            Assert.Equal(box, Assert.Single(rig.SupplyCreated.To(rig.Guest)));
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, 101), default);
            Assert.Empty(rig.SupplyPicked.To(rig.Host));
            await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(box.ObjectId, 138), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(new RoundSupplyPicked(1, box.ObjectId) { ItemKind = 138 }, Assert.Single(rig.SupplyPicked.To(member)));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Outside an item mode the server leaves team boxes to the clients, which refuse the other team themselves.</summary>
    [Fact]
    public async Task TeamBoxesAreNotEnforcedOutsideItemMode()
    {
        var rig = ItemModeRig(modeIds: [9]);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.FindByGame(rig.Host.Id)!.Team = 1;
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([new(1, 2, 3, 112, 10, 10, 2)]), default);
            Assert.Equal(112, Assert.Single(room.Supplies!.Points).Point.Kind);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var box = (await WaitSuppliesAsync(rig, rig.Host, 1))[0];
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(box.ObjectId, 112), default);
            Assert.Single(rig.SupplyPicked.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A build whose report states no interval (cn-279) gets the configured default for every point.</summary>
    [Fact]
    public async Task UnstatedIntervalsTakeTheConfiguredDefault()
    {
        var rig = new Rig(configure: options => { options.SupplyDefaultMinMs = 10; options.SupplyDefaultMaxMs = 20; });
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([new(1, 2, 3, 0, 0, 0)]) { IntervalsStated = false }, default);
            var point = Assert.Single(rig.Registry.Find(rig.RoomId)!.Supplies!.Points).Point;
            Assert.Equal((10, 20), (point.MinimumMs, point.MaximumMs));
            // A build that states intervals still has a zero bound rejected.
            var other = new Rig();
            try
            {
                await other.StartAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    other.Round.HandleSuppliesReportedAsync(other.Host, new([new(1, 2, 3, 0, 0, 0)]), default).AsTask());
            }
            finally { await other.Registry.StopAllAsync(); }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Item mode ids need kinds, and the kinds must be ones the build's slots can hold.</summary>
    [Theory]
    [InlineData(new int[] { 3 }, new int[] { }, "item_mode_ids")]
    [InlineData(new int[] { }, new int[] { 101 }, "item_mode_ids")]
    [InlineData(new int[] { 3 }, new int[] { 999 }, "item_mode_kinds")]
    [InlineData(new int[] { 3 }, new int[] { 101, 101 }, "item_mode_kinds")]
    [InlineData(new int[] { 8 }, new int[] { 101 }, "item_mode_ids")]
    public void ItemModeOptionsRejectInconsistentConfiguration(int[] modes, int[] kinds, string message)
    {
        Assert.Empty(new InGameOptions { ItemModeIds = [3], ItemModeKinds = [101], RandomSupplyItemKinds = [101] }.Validate());
        Assert.Contains(new InGameOptions { ItemModeIds = modes, ItemModeKinds = kinds, RandomSupplyItemKinds = [101], ZombiePvpModeIds = [8] }.Validate(),
            result => result.ErrorMessage!.Contains(message));
    }

    [Fact]
    public async Task RandomItemMapReportDoesNotDisconnectHostOrBlockRoundStart()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleSuppliesReportedAsync(rig.Host,
            new([new(3200, 120, 1700, 0, 15000, 30000)]), default);
        var room = rig.Registry.Find(rig.RoomId)!;
        Assert.Equal(0, Assert.Single(room.Supplies!.Points).Point.Kind);
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        Assert.Equal(RoomPhase.Playing, room.Phase);
        Assert.Equal(2, room.Seats.Count);
        await room.Executor.ExecuteAsync(_ => { lock (room.Gate) room.MarkResult(); return ValueTask.CompletedTask; }, default);
        await room.Supplies.Worker;
    }

    [Fact]
    public async Task SuppliesReachBothPlayersAndABoxCanBeConsumedOnlyOnce()
    {
        var rig = new Rig(immediateSupplyItemKinds: [10]);
        await rig.StartAsync();
        var report = new RoundSuppliesReported([new(1, 2, 3, 10, 150, 150)]);
        await rig.Round.HandleSuppliesReportedAsync(rig.Guest, report, default);
        Assert.Null(rig.Registry.Find(rig.RoomId)!.Supplies);
        await rig.Round.HandleSuppliesReportedAsync(rig.Host, report, default);
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.SupplyCreated.To(rig.Guest).Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var supply = Assert.Single(rig.SupplyCreated.To(rig.Guest));
        Assert.Equal(supply, Assert.Single(rig.SupplyCreated.To(rig.Host)));
        await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(supply.ObjectId, 99), default);
        Assert.Empty(rig.SupplyPicked.To(rig.Host));
        await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(supply.ObjectId, 10), default);
        await rig.Round.HandleSupplyPickupAsync(rig.Host, new(supply.ObjectId, 10), default);
        foreach (var member in new[] { rig.Host, rig.Guest })
            Assert.Equal(new RoundSupplyPicked(1, supply.ObjectId), Assert.Single(rig.SupplyPicked.To(member)));
        Assert.Equal(new RoundCountedItemUsed(1, 10), Assert.Single(rig.CountedItemUsed.To(rig.Guest)));
        Assert.Empty(rig.CountedItemUsed.To(rig.Host));
        while (rig.SupplyCreated.To(rig.Guest).Length < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(2, rig.SupplyCreated.To(rig.Guest).Length);
        Assert.NotEqual(supply.ObjectId, rig.SupplyCreated.To(rig.Guest)[1].ObjectId);
        var room = rig.Registry.Find(rig.RoomId)!;
        await room.Executor.ExecuteAsync(_ => { lock (room.Gate) room.MarkResult(); return ValueTask.CompletedTask; }, default);
        await room.Supplies!.Worker;
        await Task.Delay(180);
        Assert.Equal(2, rig.SupplyCreated.To(rig.Guest).Length);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(9)]
    public async Task HostWaveTransitionsReachEveryPlayerAndGuestCannotAdvanceTheWave(ushort mode)
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        var room = rig.Registry.Find(rig.RoomId)!;
        lock (room.Gate) room.ModeId = mode;
        var waves = new Sink<RoundWaveChanged>();
        await rig.Round.HandleWaveReportedAsync(rig.Host, new(1, true), waves, default);
        await rig.Round.HandleWaveReportedAsync(rig.Guest, new(2, true), waves, default);
        await rig.Round.HandleWaveReportedAsync(rig.Host, new(1, false), waves, default);
        foreach (var member in new[] { rig.Host, rig.Guest })
            Assert.Equal(new[] { new RoundWaveChanged(1, true), new RoundWaveChanged(1, false) }, waves.To(member));
    }

    [Fact]
    public async Task TheCountdownWaitsForEveryPlayerAndTheRoundStartsWhenItRunsOut()
    {
        var rig = new Rig();
        await rig.StartAsync();

        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        // A second report from the same player changes nothing.
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        Assert.Equal(new RoundPlayerLoaded(rig.RoomId, 1), Assert.Single(rig.Loaded.To(rig.Host)));
        Assert.Empty(rig.Countdown.To(rig.Host));

        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);

        foreach (var member in new ISession[] { rig.Host, rig.Guest })
        {
            Assert.Equal([1, 0], rig.Loaded.To(member).Select(loaded => loaded.Slot));
            Assert.Equal(new RoundCountdownStarted(rig.RoomId, 30), Assert.Single(rig.Countdown.To(member)));
            Assert.Equal(new RoundStarted(rig.RoomId, 180_000), await rig.WaitStartedAsync(member));
        }
        Assert.Equal(RoomPhase.Playing, rig.Registry.Find(rig.RoomId)!.Phase);
    }

    [Fact]
    public async Task AStartedRoomRefusesJoins()
    {
        var rig = new Rig();
        await rig.StartAsync();
        var late = new RecordingSession(33);
        late.SetLobbyPlayer(33, "late");
        var refused = new Sink<RoomJoinRefused>();
        var rooms = new RoomFlow(rig.Registry, new RoomSenders(rig.Entered, rig.SeatUpdated, rig.SeatVacated, rig.MasterChanged, refused),
            NullLogger.Instance);

        await rooms.HandleJoinAsync(late, new RoomJoinRequested(rig.RoomId, ""), CancellationToken.None);

        Assert.Equal(RoomJoinRefusal.Started, Assert.Single(refused.To(late)).Reason);
    }

    [Fact]
    public async Task APlayerLeavingDuringLoadingNoLongerHoldsTheBarrier()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        Assert.Empty(rig.Countdown.To(rig.Host));

        await rig.Rooms.HandleLeaveAsync(rig.Guest, new RoomLeaveRequested(), CancellationToken.None);

        Assert.Single(rig.Countdown.To(rig.Host));
        Assert.Empty(rig.Countdown.To(rig.Guest));
        await rig.WaitStartedAsync(rig.Host);
    }

    [Fact]
    public async Task FeedPacketsReachTheOtherPlayersOfARoundOnly()
    {
        var rig = new Rig();
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("room", "", 4, "map"), CancellationToken.None);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);
        byte[] body = [1, 2, 3];

        // Nothing is passed on while the room is waiting.
        await rig.Round.HandleFeedAsync(rig.Host, new RoundFeedSent(0x8075, body), CancellationToken.None);
        Assert.Empty(rig.Feed.To(rig.Guest));

        await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), CancellationToken.None);
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), CancellationToken.None);
        await rig.Round.HandleFeedAsync(rig.Host, new RoundFeedSent(0x8075, body), CancellationToken.None);

        var received = Assert.Single(rig.Feed.To(rig.Guest));
        Assert.Equal(0x8075, received.Code);
        Assert.Equal(body, received.Payload.ToArray());
        Assert.Empty(rig.Feed.To(rig.Host));
        await rig.Round.HandleFeedAsync(rig.Host, new RoundFeedSent(0x8075, body) { IncludeSender = true }, CancellationToken.None);
        Assert.Equal(body, Assert.Single(rig.Feed.To(rig.Host)).Payload.ToArray());
        Assert.Equal(2, rig.Feed.To(rig.Guest).Length);
    }

    /// <summary>
    /// A feed packet that came on the relay goes to the other players' relay connections of this round, never to
    /// their game connections; a seat whose relay belongs to an earlier round gets nothing.
    /// </summary>
    [Fact]
    public async Task RelayFeedPacketsReachTheOtherPlayersRelayConnections()
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var hostRelay = new RecordingSession(91);
            var guestRelay = new RecordingSession(92);
            lock (room.Gate)
                foreach (var seat in room.Seats)
                {
                    seat.Relay = seat.Game.Id == rig.Host.Id ? hostRelay : guestRelay;
                    seat.RelayGeneration = room.StartGeneration;
                }
            byte[] body = [4, 5, 6];
            await rig.Round.HandleFeedAsync(rig.Host, new RoundFeedSent(0x8075, body) { OnRelay = true }, CancellationToken.None);
            Assert.Equal(body, Assert.Single(rig.Feed.To(guestRelay)).Payload.ToArray());
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Empty(rig.Feed.To(hostRelay));
            lock (room.Gate)
                room.Seats.Single(seat => seat.Game.Id == rig.Guest.Id).RelayGeneration = room.StartGeneration - 1;
            await rig.Round.HandleFeedAsync(rig.Host, new RoundFeedSent(0x8075, body) { OnRelay = true }, CancellationToken.None);
            Assert.Single(rig.Feed.To(guestRelay));
            Assert.Empty(rig.Feed.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A feed packet whose layout names actors passes only when one of them is the sender's character: another
    /// seat's character is a protocol violation, and a character of no seat is dropped outside the horde modes.
    /// </summary>
    [Fact]
    public async Task FeedPacketsThatNameActorsMustNameTheSendersOwnCharacter()
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var guestRelay = new RecordingSession(92);
            int hostActor, guestActor;
            lock (room.Gate)
            {
                var guest = room.FindByGame(rig.Guest.Id)!;
                guest.Relay = guestRelay;
                guest.RelayGeneration = room.StartGeneration;
                hostActor = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
                guestActor = RoomRegistry.SceneEntity(guest.Slot);
            }
            byte[] body = [1, 2, 3, 4];
            RoundFeedSent Feed(params int[] actors) => new(0x8076, body) { OnRelay = true, ReportedActors = actors };

            await rig.Round.HandleFeedAsync(rig.Host, Feed(guestActor + 7, hostActor), CancellationToken.None);
            Assert.Single(rig.Feed.To(guestRelay));
            await rig.Round.HandleFeedAsync(rig.Host, Feed(hostActor + 7), CancellationToken.None);
            Assert.Single(rig.Feed.To(guestRelay));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => rig.Round.HandleFeedAsync(rig.Host, Feed(guestActor), CancellationToken.None).AsTask());
            Assert.Single(rig.Feed.To(guestRelay));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// In a native-life round the clients' reports move each seat through bubble, pop and reset: a pop of an enemy's
    /// bubble kills it once (a second report of the same pop does nothing), and the dead player's reset is granted
    /// only after its death presentation and completed only by a confirmation on its relay.
    /// </summary>
    [Fact]
    public async Task NativeLifeReportsBubblePopAndResetASeat()
    {
        var rig = new Rig(configure: options => options.NativeLifeModeIds = [4]);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var hostRelay = new RecordingSession(91);
            var guestRelay = new RecordingSession(92);
            int hostActor, guestActor;
            RoomSeat guest;
            lock (room.Gate)
            {
                room.MarkPlaying();
                foreach (var seat in room.Seats)
                    seat.Team = seat.Game.Id == rig.Host.Id ? (byte)1 : (byte)2;
                guest = room.FindByGame(rig.Guest.Id)!;
                hostActor = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
                guestActor = RoomRegistry.SceneEntity(guest.Slot);
            }
            rig.Registry.BindRelay(hostRelay, room.FindByGame(rig.Host.Id)!.Key);
            rig.Registry.BindRelay(guestRelay, guest.Key);
            var granted = new Sink<RoundResetGranted>();
            byte[] request = [.. BitConverter.GetBytes(guestActor), 0xBA, 0xBA, 0, 0, 0, 0];

            // A bubble the guest reports for someone else is ignored; its own bubble counts.
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, hostActor), default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, guestActor), default);
            Assert.Equal(SeatLife.Bubble, guest.Life);
            // The pop is reported by both clients and counts once.
            await rig.Round.HandleLifeReportedAsync(hostRelay, new(RoundLifeEvent.Finished, guestActor) { Initiator = hostActor }, default);
            await rig.Round.HandleLifeReportedAsync(guestRelay, new(RoundLifeEvent.Finished, guestActor) { Initiator = hostActor }, default);
            Assert.Equal(SeatLife.Dead, guest.Life);
            Assert.Equal(1, room.Scoring.TeamTotals[0]);

            // Too early: the death presentation has not finished.
            await rig.Round.HandleResetRequestedAsync(rig.Guest, new(guestActor, request), granted, default);
            Assert.Empty(granted.To(rig.Guest));
            await rig.Round.HandleLifeReportedAsync(guestRelay, new(RoundLifeEvent.DeathCleaned, guestActor), default);
            await rig.Round.HandleResetRequestedAsync(rig.Guest, new(guestActor, request), granted, default);
            Assert.Equal(request, Assert.Single(granted.To(rig.Guest)).Request.ToArray());
            Assert.Equal(SeatLife.ResetPending, guest.Life);
            // Only the relay the reset was granted on completes it.
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.ResetConfirmed, guestActor), default);
            Assert.Equal(SeatLife.ResetPending, guest.Life);
            await rig.Round.HandleLifeReportedAsync(guestRelay, new(RoundLifeEvent.ResetConfirmed, guestActor), default);
            Assert.Equal(SeatLife.Alive, guest.Life);
            // The ignored reports are only counted: the foreign bubble, the second report of the pop and the
            // confirmation on the game connection.
            lock (room.Gate)
                Assert.Equal("OwnEventForeignActor=1, ResetConfirmedOnOtherLink=1, PopOfUnbubbled=1", room.LifeChecks.Summary());
            // A pop between teammates or of oneself, and a release of a free character, change nothing.
            lock (room.Gate)
                foreach (var seat in room.Seats)
                    seat.Team = 1;
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, guestActor), default);
            await rig.Round.HandleLifeReportedAsync(hostRelay, new(RoundLifeEvent.Finished, guestActor) { Initiator = hostActor }, default);
            await rig.Round.HandleLifeReportedAsync(guestRelay, new(RoundLifeEvent.Finished, guestActor) { Initiator = guestActor }, default);
            await rig.Round.HandleLifeReportedAsync(hostRelay, new(RoundLifeEvent.Released, hostActor), default);
            Assert.Equal(SeatLife.Bubble, guest.Life);
            lock (room.Gate)
            {
                Assert.Equal(1, room.LifeChecks.Count(LifeCheck.PopWithoutOpposingTeams));
                Assert.Equal(1, room.LifeChecks.Count(LifeCheck.PopOfSelf));
                Assert.Equal(1, room.LifeChecks.Count(LifeCheck.ReleasedNotBubbled));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rig.Round.HandleResetRequestedAsync(rig.Guest, new(hostActor, request), granted, default).AsTask());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A movement report is merged into the sender's record and the complete record goes to the other players'
    /// relay connections only: a partial before any full record, another seat's actor and the sender itself get
    /// nothing, and a later partial keeps the components it does not select.
    /// </summary>
    [Fact]
    public async Task MovementReportsReachTheOtherPlayersRelaysAsMergedRecords()
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var hostRelay = new RecordingSession(91);
            var guestRelay = new RecordingSession(92);
            int hostActor;
            lock (room.Gate)
            {
                foreach (var seat in room.Seats)
                {
                    seat.Relay = seat.Game.Id == rig.Host.Id ? hostRelay : guestRelay;
                    seat.RelayGeneration = room.StartGeneration;
                }
                hostActor = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
            }
            var relayed = new Sink<RoundMovementRelayed>();
            await rig.Round.HandleMovementReportedAsync(rig.Host,
                new RoundMovementReported(hostActor, 0x01, 9f, 0, 0, 0, 0, 0, 1), relayed, CancellationToken.None);
            await rig.Round.HandleMovementReportedAsync(rig.Host,
                new RoundMovementReported(hostActor + 1, 0x3F, 1f, 2f, 3f, 4, 5, 6, 2), relayed, CancellationToken.None);
            Assert.Empty(relayed.To(guestRelay));

            await rig.Round.HandleMovementReportedAsync(rig.Host,
                new RoundMovementReported(hostActor, 0x3F, 1f, 2f, 3f, 4, 5, 6, 3), relayed, CancellationToken.None);
            await rig.Round.HandleMovementReportedAsync(rig.Host,
                new RoundMovementReported(hostActor, 0x01 | 0x20, 7f, 0, 0, 0, 0, 8, 4), relayed, CancellationToken.None);
            var frames = relayed.To(guestRelay);
            Assert.Equal(2, frames.Length);
            Assert.Equal(new RoundMovementRelayed(hostActor, new(1f, 2f, 3f, 4, 5, 6), 3), frames[0]);
            Assert.Equal(new RoundMovementRelayed(hostActor, new(7f, 2f, 3f, 4, 5, 8), 4), frames[1]);
            Assert.Empty(relayed.To(hostRelay));
            Assert.Empty(relayed.To(rig.Guest));
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleMovementReportedAsync(rig.Host,
                new RoundMovementReported(hostActor, 0x40, 0, 0, 0, 0, 0, 0, 5), relayed, CancellationToken.None).AsTask());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task TheFirstEndReportEndsTheRoundAndEveryoneReturnsToTheWaitingRoom()
    {
        var rig = new Rig();
        await rig.StartAsync();
        // An end report before the round runs is ignored.
        await rig.Round.HandleEndRequestedAsync(rig.Host, new RoundEndRequested(), CancellationToken.None);
        Assert.Empty(rig.Ended.To(rig.Host));
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Guest);

        await rig.Round.HandleEndRequestedAsync(rig.Guest, new RoundEndRequested(), CancellationToken.None);
        await rig.Round.HandleEndRequestedAsync(rig.Host, new RoundEndRequested(), CancellationToken.None);

        foreach (var member in new ISession[] { rig.Host, rig.Guest })
            Assert.Equal(new RoundEnded(rig.RoomId, RoundOutcome.Draw, RoundEndReason.StageReported) { ModeId = 4 }, Assert.Single(rig.Ended.To(member)));
        for (var i = 0; i < 200 && rig.Entered.To(rig.Guest).Length < 2; i++)
            await Task.Delay(10);
        // The room is sent again to both, with both still seated and nobody ready.
        var back = rig.Entered.To(rig.Guest)[^1];
        Assert.Equal((1, 2), (back.YourSlot, back.Room.Seats.Count));
        Assert.All(back.Room.Seats, seat => Assert.False(seat.Ready));
        Assert.Equal(2, rig.Entered.To(rig.Host).Length);
        Assert.Equal(RoomPhase.Waiting, rig.Registry.Find(rig.RoomId)!.Phase);
    }

    [Fact]
    public async Task TheRoundEndsByItselfWhenItsTimeLimitRunsOut()
    {
        var rig = new Rig(timeLimitMs: 60);
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Host);

        for (var i = 0; i < 300 && rig.Entered.To(rig.Host).Length < 2; i++)
            await Task.Delay(10);

        Assert.Single(rig.Ended.To(rig.Host));
        Assert.Single(rig.Ended.To(rig.Guest));
        Assert.Equal(RoomPhase.Waiting, rig.Registry.Find(rig.RoomId)!.Phase);
    }

    [Fact]
    public async Task ADeadPlayerIsOfferedItsStartPositionAndComesBackForEveryone()
    {
        var rig = new Rig();
        await rig.StartAsync();
        // Outside a running round a respawn request is ignored.
        await rig.Round.HandleRespawnRequestedAsync(rig.Guest, new RoundRespawnRequested(1100000), CancellationToken.None);
        Assert.Empty(rig.SpawnOffered.To(rig.Guest));
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Guest);

        await rig.Round.HandleRespawnRequestedAsync(rig.Guest, new RoundRespawnRequested(1100000), CancellationToken.None);

        // An explicit index request must receive an index reply, rather than another revival prompt.
        Assert.Equal(new RoundSpawnOffered(1100000, 1) { IndexRequested = true }, Assert.Single(rig.SpawnOffered.To(rig.Guest)));
        Assert.Empty(rig.SpawnOffered.To(rig.Host));
        Assert.Empty(rig.Respawned.To(rig.Guest));

        await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new RoundSpawnAccepted(1100000, 1), CancellationToken.None);

        foreach (var member in new ISession[] { rig.Host, rig.Guest })
            Assert.Equal(new RoundPlayerRespawned(rig.RoomId, 1, 1100000, 1), Assert.Single(rig.Respawned.To(member)));
    }

    [Fact]
    public async Task APlayerWhoseClientWaitsIsBroughtBackWhenItsDeathTimeIsOver()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Guest);

        // A client that times itself gets nothing.
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 20), CancellationToken.None);
        // The waiting client repeats its report; the round keeps one timer.
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 20, ServerRevives: true), CancellationToken.None);
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 20, ServerRevives: true), CancellationToken.None);
        Assert.Empty(rig.Respawned.To(rig.Guest));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.SpawnOffered.To(rig.Guest).Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(100);

        // The build has an offer, so the round prompts the client once and the client's answer revives it.
        Assert.Equal(new RoundSpawnOffered(1100000, 1), Assert.Single(rig.SpawnOffered.To(rig.Guest)));
        Assert.Empty(rig.Respawned.To(rig.Guest));
        await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new RoundSpawnAccepted(1100000, 1), CancellationToken.None);

        foreach (var member in new ISession[] { rig.Host, rig.Guest })
            Assert.Equal(new RoundPlayerRespawned(rig.RoomId, 1, 1100000, 1), Assert.Single(rig.Respawned.To(member)));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(1_000_015)]
    [InlineData(1_000_024)]
    public async Task NpcDeathDoesNotOccupyTheHostsRevivalTimerOrReviveTheNpc(int npcActor)
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Guest);

        await rig.Round.HandleDeathReportedAsync(rig.Host,
            new RoundDeathReported(npcActor, 20_000, ServerRevives: true) { ActorSeat = -1 }, CancellationToken.None);
        await rig.Round.HandleRespawnRequestedAsync(rig.Host,
            new RoundRespawnRequested(npcActor) { ActorSeat = -1 }, CancellationToken.None);
        await rig.Round.HandleSpawnAcceptedAsync(rig.Host,
            new RoundSpawnAccepted(npcActor, 0) { ActorSeat = -1 }, CancellationToken.None);
        await rig.Round.HandleSpawnAcceptedAsync(rig.Guest,
            new RoundSpawnAccepted(1_000_000, 0) { ActorSeat = 0 }, CancellationToken.None);
        Assert.Empty(rig.SpawnOffered.To(rig.Host));
        Assert.Empty(rig.Respawned.To(rig.Host));

        await rig.Round.HandleDeathReportedAsync(rig.Host,
            new RoundDeathReported(1_000_000, 20, ServerRevives: true) { ActorSeat = 0 }, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.SpawnOffered.To(rig.Host).Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(new RoundSpawnOffered(1_000_000, 0), Assert.Single(rig.SpawnOffered.To(rig.Host)));
        await rig.Round.HandleSpawnAcceptedAsync(rig.Host,
            new RoundSpawnAccepted(1_000_000, 0) { ActorSeat = 0 }, CancellationToken.None);
        foreach (var member in new ISession[] { rig.Host, rig.Guest })
            Assert.Equal(new RoundPlayerRespawned(rig.RoomId, 0, 1_000_000, 0), Assert.Single(rig.Respawned.To(member)));
    }

    [Fact]
    public async Task ATauntTakesTimeOffTheDeadPlayersWait()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Guest);

        // A taunt at a player nobody is timing changes nothing.
        await rig.Round.HandleTauntReportedAsync(rig.Host, new RoundTauntReported(1100000), CancellationToken.None);
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new RoundDeathReported(1100000, 20_000, ServerRevives: true), CancellationToken.None);
        await Task.Delay(100);
        Assert.Empty(rig.SpawnOffered.To(rig.Guest));

        // Ten taunts of 2000 ms use the whole wait up.
        for (var i = 0; i < 10; i++)
            await rig.Round.HandleTauntReportedAsync(rig.Host, new RoundTauntReported(1100000), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.SpawnOffered.To(rig.Guest).Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Single(rig.SpawnOffered.To(rig.Guest));
    }
}
