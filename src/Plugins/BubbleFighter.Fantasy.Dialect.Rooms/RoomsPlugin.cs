using System.ComponentModel.DataAnnotations;
using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Adapts the legacy builds' main-menu room session, the tutorial select / start path, and the
/// kr-812 plaza create / ready / map / start loop to shared contracts. A separate row from the
/// login adapters so a build whose session layout is not decoded yet can log in without it.
/// Plaza C2S handlers register only for opcodes that are not part of the login burst.
/// </summary>
[Plugin("dialect.rooms", Description = "Main-menu room, tutorial select/start, and plaza create/ready/start adapters, with per-build layouts from the packet tables.",
    Requires = [typeof(IOpcodeTableRegistry), typeof(IPacketRouter)],
    Optional = [typeof(IMessageRouter), typeof(IInventoryRepository), typeof(BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore)], OptionalMany = [typeof(IOpcodeTable)],
    Provides = [], ProvidesMany = [typeof(IMessageSender<MainMenuRoomOpened>)],
    ConditionalProvidesMany = [typeof(IMessageSender<RoomTeamConverted>), typeof(IInboundMessageBinding<RoomMatchRequested>), typeof(IMessageSender<RoomMatchNotified>), typeof(IMessageSender<RoomAutoStartNotified>), typeof(IInboundMessageBinding<RoomListRequested>), typeof(IInboundMessageBinding<RoomCreateRequested>),
        typeof(IInboundMessageBinding<RoomJoinRequested>), typeof(IInboundMessageBinding<RoomLeaveRequested>),
        typeof(IInboundMessageBinding<RoomReadyRequested>), typeof(IInboundMessageBinding<RoomTeamChangeRequested>),
        typeof(IInboundMessageBinding<RoomStartRequested>), typeof(IMessageSender<RoomListSnapshot>), typeof(IMessageSender<RoomEntered>),
        typeof(IMessageSender<RoomSeatUpdated>), typeof(IMessageSender<RoomSeatVacated>), typeof(IMessageSender<RoomMasterChanged>),
        typeof(IMessageSender<RoomJoinRefused>), typeof(IMessageSender<RoomLeft>), typeof(IMessageSender<RoomMatchStarting>),
        typeof(IInboundMessageBinding<RoomSettingsChangeRequested>), typeof(IMessageSender<RoomSettingsChanged>),
        typeof(IInboundMessageBinding<RoomRoundSettingsChangeRequested>),
        typeof(IInboundMessageBinding<RoomMasterChangeRequested>), typeof(IMessageSender<RoomMasterChangeReplied>),
        typeof(IInboundMessageBinding<RoundLoadReported>), typeof(IInboundMessageBinding<RoundFeedSent>),
        typeof(IMessageSender<RoundPlayerLoaded>), typeof(IMessageSender<RoundCountdownStarted>),
        typeof(IMessageSender<RoundStarted>), typeof(IMessageSender<RoundFeedReceived>),
        typeof(IInboundMessageBinding<RoundLotteryRequested>), typeof(IMessageSender<RoundLotteryOpened>), typeof(IMessageSender<RoundLotteryClosed>), typeof(IMessageSender<RoundLotterySelected>), typeof(IMessageSender<RoundLotteryRevealed>), typeof(IInboundMessageBinding<RoundEndRequested>), typeof(IMessageSender<RoundEnded>), typeof(IMessageSender<RoundWaveChanged>),
        typeof(IInboundMessageBinding<RoundRespawnRequested>), typeof(IInboundMessageBinding<RoundSpawnAccepted>), typeof(IInboundMessageBinding<RoundDeathReported>),
        typeof(IMessageSender<RoundSpawnOffered>), typeof(IMessageSender<RoundPlayerRespawned>),
        typeof(IInboundMessageBinding<RoundTauntReported>), typeof(IMessageSender<RoundRespawnWaitChanged>),
        typeof(IInboundMessageBinding<RoundKillCreditReported>), typeof(IInboundMessageBinding<RoundFinishingBlowReported>),
        typeof(IMessageSender<RoundScoreUpdated>), typeof(IMessageSender<RoundScoreboard>), typeof(IMessageSender<ResultStageAdvanced>),
        typeof(IInboundMessageBinding<RoomChatSent>), typeof(IMessageSender<RoomChatReceived>), typeof(IInboundMessageBinding<RoomSeatLockRequested>),
        typeof(IMessageSender<RoomSeatLockChanged>), typeof(IInboundMessageBinding<RoomKickRequested>), typeof(IMessageSender<RoomMemberKicked>),
        typeof(IInboundMessageBinding<PlazaEnterRequested>), typeof(IInboundMessageBinding<PlazaPositionReported>),
        typeof(IInboundMessageBinding<PlazaMenuReturnRequested>),
        typeof(IMessageSender<PlazaEntered>), typeof(IMessageSender<PlazaMemberEntered>), typeof(IMessageSender<PlazaMemberLeft>),
        typeof(IMessageSender<PlazaPositionReceived>), typeof(IMessageSender<PlazaMenuReturnRequested>),
        typeof(IInboundMessageBinding<RoundSuppliesReported>), typeof(IInboundMessageBinding<RoundSupplyPickupRequested>), typeof(IInboundMessageBinding<RoundSupplyDropRequested>),
        typeof(IMessageSender<RoundSupplyCreated>), typeof(IMessageSender<RoundSupplyPicked>),
        typeof(IInboundMessageBinding<RoundLucciDropRequested>), typeof(IInboundMessageBinding<RoundHordeAwardRequested>),
        typeof(IInboundMessageBinding<RoundLucciPickupRequested>), typeof(IMessageSender<RoundLucciCreated>), typeof(IMessageSender<RoundLucciPicked>),
        typeof(IInboundMessageBinding<RoundStarDropRequested>), typeof(IInboundMessageBinding<RoundStarPickupRequested>),
        typeof(IInboundMessageBinding<RoundStarReported>), typeof(IMessageSender<RoundStarCreated>), typeof(IMessageSender<RoundStarPicked>),
        typeof(IInboundMessageBinding<RoundItemUseRequested>), typeof(IMessageSender<RoundItemUsed>),
        typeof(IInboundMessageBinding<RoundItemSlotSwitchRequested>), typeof(IMessageSender<RoundItemSlotChanged>),
        typeof(IInboundMessageBinding<RoundInstantRevivalRequested>), typeof(IInboundMessageBinding<RoundPveRevivalRequested>),
        typeof(IInboundMessageBinding<RoundCountedItemUseRequested>), typeof(IMessageSender<RoundCountedItemsPrepared>),
        typeof(IMessageSender<RoundCountedItemUsed>), typeof(IInboundMessageBinding<RoundRescueProgressReported>),
        typeof(IMessageSender<RoundRescueMissionsPrepared>), typeof(IMessageSender<RoundRescueMissionStarted>),
        typeof(IMessageSender<RoundRescueMissionFailed>), typeof(IMessageSender<RoundRescueMissionSucceeded>),
        typeof(IInboundMessageBinding<RoundPvePangReported>), typeof(IMessageSender<RoundPvePointsChanged>)])]
public sealed class RoomsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<RoomsOptions>();
        if (context.Row.ClientBuild is null)
            throw new ConfigException("dialect.rooms requires an explicit client_build.");
        var lineage = context.Row.LineageOrBuild();
        // Every build's structures come from the packet tables; what the tables do not state for a
        // build is its base's, and the summary says how much is stated for each.
        var builds = RoomTables.Load(options.PacketsDirectory);
        foreach (var line in builds.Summary)
            context.Logger.LogInformation("dialect.rooms: {Line}.", line);
        var room = builds.Schemas.Require(lineage);
        var shape = room.Value;
        context.Logger.LogInformation("dialect.rooms: room layout of {Variant} ({Match}) for lineage [{Lineage}].",
            room.Profile, room.Match, string.Join(", ", lineage));
        var tables = context.Services.Get<IOpcodeTableRegistry>();
        if (UpdatePartyCodec.NeedsIdleParty(shape))
        {
            _ = builds.Tables.Resolve(room.Profile, "SoloPartySnapshot");
            _ = builds.Tables.Resolve(room.Profile, "SoloPartyMember");
        }
        _ = builds.Tables.Resolve(room.Profile, "TutorialMoveInto");
        _ = builds.Tables.Resolve(room.Profile, "RoomSceneTransition");
        context.Effect(context.Services.RegisterMany<IMessageSender<MainMenuRoomOpened>>(new RoomBinding(options.Protocol, shape, tables, options.TutorialMoveIn,
            builds.Tables, room.Profile)));

        if (options.MenuOnly)
            return ValueTask.CompletedTask;

        if (options.BrowserOnly)
        {
            var browserPlay = builds.Play.Resolve(lineage)
                ?? throw new ConfigException("dialect.rooms browser_only requires a waiting-room layout.");
            new PlayRoomCodec(options.Protocol, room.Profile, shape, browserPlay.Value, tables, context.Logger, builds.Tables)
                .MountBrowser(context);
            return ValueTask.CompletedTask;
        }

        // Tutorial select and start, and the plaza list / create / ready / start requests: only on
        // builds whose packets are proven (PlayBuild). None of these opcodes is part of the
        // login burst; they fire after GotoLobby or inside RoomStage.
        if (builds.Play.Resolve(lineage) is { Value: { } play })
        {
            if (options.TutorialUseOldMap && play.Tutorial.OldMapKey.Length == 0)
                throw new ConfigException("dialect.rooms tutorial_use_old_map needs a build with tutorial.old_map_key.");
            var table = tables.ForProtocol(options.Protocol);
            var router = context.Services.Get<IPacketRouter>();
            var tutorial = new TutorialHandler(builds.Tables, room.Profile, shape, play.Tutorial, play.MatchStart, play.Wire, tables, context.Logger, play.OneSeatWaves, play.Score?.OutcomeFirst ?? false, shape.ScoreRow, play.Score, play.TutorialSkipStart, options.TutorialUseOldMap, options.MaxRooms,
                context.Services.TryGet<IMessageRouter>(out var messages) ? messages : null,
                context.Services.TryGet<IInventoryRepository>(out var inventory) ? inventory : null,
                context.Events, sharedLifecycle: options.TypedPlayRooms, resultMs: options.TutorialResultMs, scoreMs: options.TutorialScoreMs,
                stages: context.Services.TryGet<BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore>(out var stages) ? stages : null);
            context.Effect(tutorial);
            context.Effect(context.Events.On<ConnectionClosedEvent>(tutorial.OnConnectionClosed));
            if (!options.TypedPlayRooms && play.Tutorial.OldMapKey.Length > 0
                && table.Find(PacketDirection.ClientToServer, "gcpReqEndStage") is { } endStage)
                context.Effect(router.Register(options.Protocol, endStage, tutorial.HandleEndStageAsync));
            // A build whose tutorial requests are not named yet gets its rooms without the tutorial.
            if (table.Find(PacketDirection.ClientToServer, "gcpReqCreateTutorialSession") is { } createTutorial
                && table.Find(PacketDirection.ClientToServer, "gcpReqSingleStageInfo") is { } stageInfo
                && table.Find(PacketDirection.ClientToServer, "gcpReqPlayTutorial") is { } playTutorial)
            {
                context.Effect(router.Register(options.Protocol, createTutorial, tutorial.HandleCreateTutorialAsync));
                context.Effect(router.Register(options.Protocol, stageInfo, tutorial.HandleSingleStageInfoAsync));
                context.Effect(router.Register(options.Protocol, playTutorial, tutorial.HandlePlayTutorialAsync));
            }
            else
                context.Logger.LogInformation("dialect.rooms: {Protocol} names no tutorial requests; the tutorial is not bound.", options.Protocol);
            if (play.OneSeatWaves)
            {
                // One-seat load-wait: the load report after EnterGame -> MapLoading / Everybody /
                // CountDown / StartGame (cn-717 RoomStage cases 0x2BD-0x2C0).
                // With typed rooms the round binds the same report and hands it here first.
                if (!options.TypedPlayRooms)
                    context.Effect(router.Register(options.Protocol, table.C2S("gcpGameReportMapLoadingComplete"), tutorial.HandleMapLoadingCompleteAsync));
            }
            if (play.NpcWaves)
            {
                // Native host spawn requests belong to ordinary NPC rounds as well as tutorials.
                // id-400 sub_65B4E0 and id-1809 sub_693260 emit the one-word spawn-group request.
                context.Effect(router.Register(options.Protocol, table.C2S("gppCreateMobsFromSpawnGroup"), tutorial.HandleCreateMobsFromSpawnGroupAsync));
                // Wave mgr: C2S 903 -> S2C 904; C2S 908 -> empty S2C 908 + S2C 909 (clears +172);
                // then echo gppCreateAIObject / gppCreateSpawnGroupMemberMob so models appear.
                context.Effect(router.Register(options.Protocol, table.C2S("gcpReqStartNewWave"), tutorial.HandleStartNewWaveAsync));
                // C2S 904 gcpReqEndWave -> S2C 905 gspRepEndWave; the client clears its
                // wave-in-progress flag and signals the next tutorial step from that reply.
                if (table.Find(PacketDirection.ClientToServer, "gcpReqEndWave") is { } endWave
                    && table.Find(PacketDirection.ServerToClient, "gspRepEndWave") is not null)
                    context.Effect(router.Register(options.Protocol, endWave, tutorial.HandleEndWaveAsync));
                context.Effect(router.Register(options.Protocol, table.C2S("gcpReqWaveAllSpawnMobs"), tutorial.HandleWaveAllSpawnMobsAsync));
                context.Effect(router.Register(options.Protocol, table.C2S("gppCreateAIObject"), tutorial.HandleCreateAIObjectAsync));
                context.Effect(router.Register(options.Protocol, table.C2S("gppCreateSpawnGroupMemberMob"), tutorial.HandleCreateSpawnGroupMemberMobAsync));
                // A mob's life points ride their own relay (C2S 32920 -> S2C 32920); without it
                // the mob spends none and dies outright at zero hp instead of bubbling.
                if (table.Find(PacketDirection.ClientToServer, "gppLifePoint") is { } lifePoint
                    && table.Find(PacketDirection.ServerToClient, "gppLifePoint") is not null)
                    context.Effect(router.Register(options.Protocol, lifePoint, tutorial.HandleLifePointAsync));
                // The host reports every wave done with an empty gcpReqEndAllWave, which no other
                // path claims; a one-seat tutorial has no typed round for its per-wave and stage
                // reports to reach, so this is where its round ends and the client goes back.
                if (table.Find(PacketDirection.ClientToServer, "gcpReqEndAllWave") is { } endAllWave
                    && table.Find(PacketDirection.ServerToClient, "gspGameEndGame") is not null)
                    context.Effect(router.Register(options.Protocol, endAllWave, tutorial.HandleEndAllWaveAsync));
            }

            if (options.TypedPlayRooms)
            {
                // The play row owns the rooms; this row only decodes and encodes for it. Score
                // reports name a client reference, so the codec resolves the reporter's seat
                // through the roster the play row publishes and never guesses one.
                if (!context.Services.TryGet<IMessageRouter>(out var messageRouter) || messageRouter is null)
                    throw new ConfigException("dialect.rooms typed_play_rooms needs the core.message-router row.");
                new PlayRoomCodec(options.Protocol, room.Profile, shape, play, tables, context.Logger, builds.Tables)
                {
                    LoadReportFirst = tutorial.TryHandleMapLoadingCompleteAsync,
                    EndStageFirst = tutorial.TryHandleEndStageAsync,
                    StartRequestFirst = tutorial.TryHandleEnterGameAsync,
                    LeaveRequestFirst = tutorial.TryHandleLeaveAsync,
                    MultiplayerRoomEntering = tutorial.ClearTutorialStateAsync,
                    ReadyRequestFirst = tutorial.TryHandleReadyAsync,
                    EchoDirectItemEffects = options.EchoDirectItemEffects
                        && builds.Tables.CapabilityOf(room.Profile, "direct_item_effect_echo"),
                    StarReports = options.StarItems && builds.Tables.CapabilityOf(room.Profile, "star_items"),
                }.Mount(context);
                if (table.Find(PacketDirection.ServerToClient, "gspRepAutoStart") is not null)
                {
                    _ = builds.Tables.Resolve(room.Profile, "gspRepAutoStart");
                    context.Effect(context.Services.RegisterMany<IMessageSender<RoomAutoStartNotified>>(
                        new AutoStartCodec(options.Protocol, builds.Tables, room.Profile, tables)));
                }
                if (options.SupplyItems)
                    new SupplyBinding(options.Protocol, table, builds.Tables, room.Profile, options.MaxSupplyPoints)
                    {
                        MapReportFirst = tutorial.TryHandleMapSuppliesAsync,
                    }.Mount(context, messageRouter);
                if (options.LucciDrops)
                    new LucciBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                if (options.StarItems)
                    new StarBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                if (options.HordeLottery)
                    new LotteryBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                if (options.InstantRevival)
                    new RevivalBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                if (options.PvePoints)
                    new PvePointsBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                if (options.RescueMissions)
                    new RescueBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                if (options.CountedPveItems)
                    new CountedItemsBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                else if (options.SupplyItems && builds.Tables.CapabilityOf(room.Profile, "direct_supply_effects"))
                    new CountedItemsBinding(options.Protocol, table, builds.Tables, room.Profile).MountEffect(context);
                if (options.QuickMatching)
                    new MatchingBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context, messageRouter);
                if (options.ZombieConversion)
                {
                    _ = builds.Tables.Resolve(room.Profile, "ZombieTeamChange");
                    context.Effect(context.Services.RegisterMany<IMessageSender<RoomTeamConverted>>(
                        new ZombieConversionCodec(options.Protocol, builds.Tables, room.Profile, tables)));
                }
                return ValueTask.CompletedTask;
            }

            var plaza = new PlazaRoomHandler(shape, builds.Tables, room.Profile, options.MaxRooms, tables, context.Logger);
            context.Effect(context.Events.On<ConnectionClosedEvent>(plaza.OnConnectionClosed));
            context.Effect(router.Register(options.Protocol, table.C2S("gcpReqListSession"), plaza.HandleListSessionAsync));
            context.Effect(router.Register(options.Protocol, table.C2S("gcpReqCreateSession"), plaza.HandleCreateSessionAsync));
            context.Effect(router.Register(options.Protocol, table.C2S("gcpRoomReqReadyStatus"), plaza.HandleReadyStatusAsync));
            context.Effect(router.Register(options.Protocol, table.C2S("gcpRoomReqEnterGame"), plaza.HandleRoomReqEnterGameAsync));
            if (play.HostStart)
                context.Effect(router.Register(options.Protocol, table.C2S("gcpUserHostStart"), plaza.HandleUserHostStartAsync));
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Writes the main menu's room session as <c>gspGotoRoom</c> and moves to the menu with
/// <c>gspGotoMenu</c>, numbered by the session profile's opcode table: kr-812 uses 110 and
/// 108; cn-735 uses 112 and 110. Plaza-capable legacy builds also send a solo
/// <c>gspUpdateParty</c> so MainMenu plaza can leave.
/// </summary>
internal sealed class RoomBinding(string protocol, RoomSchema shape, IOpcodeTableRegistry tables,
    bool tutorialMoveIn,
    BubbleFighter.Fantasy.Packets.PacketTables packetTables, string build) : IMessageSender<MainMenuRoomOpened>
{
    public string Protocol => protocol;

    public async ValueTask SendAsync(ISession session, MainMenuRoomOpened message, CancellationToken ct)
    {
        var table = tables.Get(session.Profile.Id);
        // The session must exist before MainMenuStage loads: its scene creates the actors from
        // the session slots, and its one-shot entry presentation (byte_E682C7) runs only once.
        await session.SendAsync(table.S2C("gspGotoRoom"), RoomCodec.WriteMainMenuEnvelope(message, packetTables, build, shape), ct).ConfigureAwait(false);
        // MainMenu plaza gates (ecx=2) require Party+4 == 2 and a sole self member; without
        // gspUpdateParty the client shows "파티가 이동중입니다." and never sends gcpReqGotoLobby.
        // A party member keeps its party list (leader first) through the menu install.
        if (UpdatePartyCodec.NeedsIdleParty(shape))
            await session.SendAsync(table.S2C("gspUpdateParty"),
                session.TryFeature<IPartyMembership>(out var membership)
                    ? UpdatePartyCodec.WriteParty(packetTables, build, membership.Party)
                    : UpdatePartyCodec.WriteSolo(packetTables, build, message), ct).ConfigureAwait(false);
        await session.SendAsync(table.S2C("gspGotoMenu"), RoomCodec.WriteSceneTransition(packetTables, build), ct).ConfigureAwait(false);
        if (tutorialMoveIn && message.OfferTutorial != false && table.Find(PacketDirection.ServerToClient, "gspRepMoveInto") is { } moveInto)
        {
            // cn-279 sub_6F7DC0 (dispatcher case 10): bool32 then two i32 stored at session+2244/2248,
            // session+2240 set to 1; dispatcher case 0xB (S2C 11) then sets byte_ABB3DF, and the
            // MainMenuStage tick shows the auto-tutorial notice when both are set.
            var body = WriteMoveInto(packetTables, build);
            await session.SendAsync(moveInto, body, ct).ConfigureAwait(false);
            if (table.Find(PacketDirection.ServerToClient, "gspNotifyAutoTutorial_") is { } notify)
                await session.SendAsync(notify, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        }
    }

    internal static byte[] WriteMoveInto(BubbleFighter.Fantasy.Packets.PacketTables tables, string build) =>
        BubbleFighter.Fantasy.Packets.PacketCodec.Write(tables, build, "TutorialMoveInto",
            new Dictionary<string, object?>());
}

/// <summary>Selects the game connection scope for the legacy room adapter.</summary>
public sealed class RoomsOptions
{
    /// <summary>Gets or sets whether only the audited login main-menu sender is mounted.</summary>
    public bool MenuOnly { get; set; }

    /// <summary>Gets or sets whether only plaza entry and waiting-room browsing are mounted, without tutorial or round adapters.</summary>
    public bool BrowserOnly { get; set; }

    /// <summary>Gets or sets whether the verified native cooperative reward-card packets are mounted.</summary>
    public bool HordeLottery { get; set; }

    /// <summary>Gets or sets whether proven consumable and PvE revival requests are mounted.</summary>
    public bool InstantRevival { get; set; }
    /// <summary>Gets or sets whether native direct-item effects require delivery back to the sending client.</summary>
    public bool EchoDirectItemEffects { get; set; }
    /// <summary>Gets or sets whether the native cooperative allegiance-change sender is mounted.</summary>
    public bool ZombieConversion { get; set; }
    /// <summary>Gets or sets whether the confirmed native quick-matching request and notification are mounted.</summary>
    public bool QuickMatching { get; set; }

    /// <summary>Gets or sets the legacy game connection protocol, such as <c>kr-812/game</c>.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/game";

    /// <summary>
    /// Gets or sets whether the plaza room requests are delivered as typed messages to the play row
    /// (<c>ingame</c> with <c>typed_only</c>), which then owns multi-player rooms. Off, this row
    /// keeps its own one-seat rooms. It needs the build's play-room layout, so only builds that
    /// have one may turn it on.
    /// </summary>
    public bool TypedPlayRooms { get; set; } = true;

    /// <summary>Gets or sets whether the row binds proven map supply reports, creation and pickup packets.</summary>
    public bool SupplyItems { get; set; }

    /// <summary>
    /// Gets or sets whether the row binds the NPC-kill Lucci packets (drop requests, item creation,
    /// pickup and its confirmation). Needs the build's <c>lucci_drops</c> capability and opcodes.
    /// </summary>
    public bool LucciDrops { get; set; }

    /// <summary>
    /// Gets or sets whether the row binds the star modes' packets (star drop requests, star creation, the claim
    /// and its confirmation) and reads the clients' star-count reports. Needs the build's <c>star_items</c>
    /// capability and opcodes.
    /// </summary>
    public bool StarItems { get; set; }

    /// <summary>Gets or sets whether the row binds native cooperative counted item slots and use requests.</summary>
    public bool CountedPveItems { get; set; }

    /// <summary>Gets or sets whether native cooperative rescue mission packets are mounted.</summary>
    public bool RescueMissions { get; set; }

    /// <summary>Gets or sets whether native cooperative point and ranking packets are bound.</summary>
    public bool PvePoints { get; set; }

    /// <summary>Gets or sets the maximum entries accepted in each map report vector.</summary>
    [Range(32, 1024)]
    public int MaxSupplyPoints { get; set; } = 128;

    /// <summary>
    /// Live experiment (cn-279): after the login burst send S2C 10 <c>gspRepMoveInto</c>
    /// (bool32 true, two i32) and S2C 11 (empty; the dispatcher case 0xB sets the auto-tutorial
    /// flag <c>byte_ABB3DF</c> from it) so MainMenuStage shows the auto-tutorial notice.
    /// </summary>
    public bool TutorialMoveIn { get; set; }

    /// <summary>
    /// Gets or sets whether the automatic tutorial notice uses the build's old tutorial map.
    /// Requests from the select scene retain the client's map choice. Requires
    /// <c>tutorial.old_map_key</c> in the packet build data.
    /// </summary>
    public bool TutorialUseOldMap { get; set; }

    /// <summary>
    /// Gets or sets the directory of the packet tables: <c>builds.yaml</c> and the structure files
    /// every legacy build's room layouts are read from. YAML key <c>packets_directory</c>.
    /// </summary>
    [Required, StringLength(512, MinimumLength = 1)]
    public string PacketsDirectory { get; set; } = "packets";

    /// <summary>Gets or sets the maximum number of live legacy rooms this row can hold.</summary>
    [Range(1, 10000)]
    public int MaxRooms { get; set; } = 64;

    /// <summary>
    /// Gets or sets how long a finished single-play tutorial stage keeps its clear or fail panel before the player is
    /// sent back to the tutorial room, in milliseconds; 0 returns at once. YAML key <c>tutorial_result_ms</c>.
    /// </summary>
    [Range(0, 60000)]
    public int TutorialResultMs { get; set; }

    /// <summary>
    /// Gets or sets how long a finished single-play tutorial stage shows its time-over cue before its clear or fail
    /// panel, in milliseconds. YAML key <c>tutorial_score_ms</c>.
    /// </summary>
    [Range(0, 60000)]
    public int TutorialScoreMs { get; set; }
}
