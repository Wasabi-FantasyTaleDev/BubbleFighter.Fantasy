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
using BubbleFighter.Fantasy.Dialect.Messages;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Adapts the legacy builds' main-menu room session, the tutorial select / start path, and the
/// kr-812 plaza create / ready / map / start loop to shared contracts. A separate row from the
/// login adapters so a build whose session layout is not decoded yet can log in without it.
/// Plaza C2S handlers register only for opcodes that are not part of the login burst.
/// </summary>
[Plugin("dialect.rooms", Category = "network", Names = ["en=Room messages", "zh-Hans=房间消息", "zh-Hant=房間訊息", "ko=방 메시지"], Description = "Main-menu room, tutorial select/start, and plaza create/ready/start adapters, with per-build layouts from the packet tables.",
    Requires = [typeof(IOpcodeTableRegistry), typeof(IPacketRouter)],
    Optional = [typeof(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore), typeof(IMessageRouter), typeof(IInventoryRepository), typeof(IGameText), typeof(BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore)],
    OptionalMany = [typeof(IOpcodeTable), typeof(IUdpDatagramEndpoint), typeof(IMatchUdpRelayDirectory)],
    Provides = [], ProvidesMany = [],
    ConditionalProvidesMany = [
        typeof(IInboundMessageBinding<RoundStageResultReported>), typeof(IScriptZombieModes),
        typeof(IInboundMessageBinding<RoundBombStateReported>), typeof(IInboundMessageBinding<RoundBombMissionOverReported>),
        typeof(IMessageSender<RoundBombStateChanged>), typeof(IMessageSender<RoundPlayerObserved>), typeof(IRoundObservationSender),
        typeof(IInboundMessageBinding<RoundPortalMoveRequested>), typeof(IMessageSender<RoundPortalMoved>),
        typeof(IInboundMessageBinding<RoundCharacterFormRequested>), typeof(IMessageSender<RoundCharacterFormChanged>),
        typeof(IInboundMessageBinding<RoundCaptainDropped>), typeof(IMessageSender<RoundCaptainAppointed>),
        typeof(IInboundMessageBinding<RoundScriptZombieConversionReported>), typeof(IMessageSender<RoundScriptZombieConversionReported>),
        typeof(IMessageSender<RoundScriptZombieTeamChanged>),
        typeof(IInboundMessageBinding<RoundExtremeRacingGoalReported>), typeof(IMessageSender<RoundExtremeRacingTimerChanged>),
        typeof(IMessageSender<RoundExtremeRacingRoundChanged>), typeof(IMessageSender<RoundExtremeRacingRecordsUpdated>),
        typeof(IMessageSender<RoundExtremeRacingInitialized>), typeof(IMessageSender<RoundExtremeRacingCannonCommand>),
        typeof(IMessageSender<RoundEquipmentApplied>), typeof(IInboundMessageBinding<RoundWeaponMasterRevivalRequested>),
        typeof(IInboundMessageBinding<RoundBnBPlacementReported>), typeof(IInboundMessageBinding<RoundBnBExplosionReported>),
        typeof(IInboundMessageBinding<RoundBnBPickupReported>), typeof(IMessageSender<RoundBnBPlacementPublished>),
        typeof(IMessageSender<RoundBnBExplosionPublished>), typeof(IMessageSender<RoundBnBPickupPublished>),
        typeof(IInboundMessageBinding<RoundFinalZoneItemPickupRequested>), typeof(IInboundMessageBinding<RoundFinalZoneItemsDropped>),
        typeof(IInboundMessageBinding<RoundFinalZoneBackpackDropped>), typeof(IInboundMessageBinding<RoundFinalZoneBackpackPickupRequested>),
        typeof(IInboundMessageBinding<RoundFinalZoneMapEffectChanged>), typeof(IMessageSender<RoundFinalZoneMapEffectChanged>),
        typeof(IInboundMessageBinding<RoundMapObjectVisibilityChanged>), typeof(IMessageSender<RoundMapObjectVisibilityChanged>),
        typeof(IInboundMessageBinding<RoundBreakableMapObjectCreated>), typeof(IMessageSender<RoundBreakableMapObjectCreated>),
        typeof(IMessageSender<RoundFinalZoneItemsSeeded>), typeof(IMessageSender<RoundFinalZoneItemsSpawned>),
        typeof(IMessageSender<RoundFinalZoneItemPicked>), typeof(IMessageSender<RoundFinalZoneBackpackSpawned>),
        typeof(IMessageSender<RoundFinalZoneBackpackPicked>), typeof(IMessageSender<RoundFinalZonePlayerEliminated>),
        typeof(IMessageSender<RoundFinalZoneWaterTimerChanged>),
        typeof(IInboundMessageBinding<RoundFinalZoneDroneDestroyed>), typeof(IInboundMessageBinding<RoundFinalZoneGoldenSupplyRequested>),
        typeof(IMessageSender<RoundFinalZoneDroneSpawned>), typeof(IMessageSender<RoundFinalZoneDroneBoxSpawned>),
        typeof(IMessageSender<RoundFinalZoneGoldenSupplySpawned>),
        typeof(IInboundMessageBinding<RoundFinalZoneTrapRequested>), typeof(IInboundMessageBinding<RoundFinalZoneTrapLinked>),
        typeof(IMessageSender<RoundFinalZoneTrapSpawned>), typeof(IMessageSender<RoundFinalZoneTrapLinked>),
        typeof(IMatchUdpRelayControl),
        typeof(IInboundMessageBinding<RoomInventoryChangeRequested>), typeof(IMessageSender<RoomInventoryChanged>),
        typeof(IInboundMessageBinding<RoomAiAddRequested>), typeof(IInboundMessageBinding<RoomAiRemoveRequested>),
        typeof(IMessageSender<MainMenuRoomOpened>), typeof(IMessageSender<RoundPvePointsChanged>),
        typeof(IInboundMessageBinding<InitialTutorialNpcReported>), typeof(IMessageSender<InitialTutorialNpcAcknowledged>),
        typeof(IInboundMessageBinding<RoundNpcReported>), typeof(IMessageSender<RoundNpcAcknowledged>),
        typeof(IInboundMessageBinding<TutorialScoreReported>), typeof(IMessageSender<TutorialScoreUpdated>),
        typeof(IMessageSender<TutorialPracticeProgress>),
        typeof(IInboundMessageBinding<InitialTutorialRequested>), typeof(IMessageSender<InitialTutorialEntered>), typeof(IInboundMessageBinding<InitialTutorialExitRequested>),
        typeof(IMessageSender<RoomTeamConverted>), typeof(IInboundMessageBinding<RoomMatchRequested>), typeof(IMessageSender<RoomMatchNotified>), typeof(IMessageSender<RoomAutoStartNotified>),
        typeof(IInboundMessageBinding<RoomListRequested>), typeof(IInboundMessageBinding<RoomCreateRequested>), typeof(IInboundMessageBinding<RoomJoinRequested>), typeof(IInboundMessageBinding<RoomLeaveRequested>),
        typeof(IInboundMessageBinding<RoomReadyRequested>), typeof(IInboundMessageBinding<RoomTeamChangeRequested>), typeof(IInboundMessageBinding<RoomStartRequested>), typeof(IMessageSender<RoomListSnapshot>),
        typeof(IMessageSender<RoomEntered>), typeof(IMessageSender<RoomSeatUpdated>), typeof(IMessageSender<RoomSeatVacated>), typeof(IMessageSender<RoomMasterChanged>),
        typeof(IMessageSender<RoomJoinRefused>), typeof(IMessageSender<RoomLeft>), typeof(IMessageSender<RoomMatchStarting>), typeof(IInboundMessageBinding<RoomSettingsChangeRequested>),
        typeof(IMessageSender<RoomSettingsChanged>), typeof(IInboundMessageBinding<RoomRoundSettingsChangeRequested>), typeof(IInboundMessageBinding<RoomCustomRulesChangeRequested>), typeof(IMessageSender<RoomCustomRulesReplied>), typeof(IInboundMessageBinding<RoomMasterChangeRequested>), typeof(IMessageSender<RoomMasterChangeReplied>),
        typeof(IInboundMessageBinding<RoundLoadReported>), typeof(IInboundMessageBinding<RoundFeedSent>), typeof(IMessageSender<RoundPlayerLoaded>), typeof(IMessageSender<RoundCountdownStarted>),
        typeof(IMessageSender<RoundStarted>), typeof(IMessageSender<RoundFeedReceived>), typeof(IInboundMessageBinding<RoundLotteryRequested>), typeof(IMessageSender<RoundLotteryOpened>),
        typeof(IMessageSender<RoundLotteryClosed>), typeof(IMessageSender<RoundLotterySelected>), typeof(IMessageSender<RoundLotteryRevealed>), typeof(IInboundMessageBinding<RoundEndRequested>),
        typeof(IMessageSender<RoundEnded>), typeof(IMessageSender<RoundWaveChanged>), typeof(IInboundMessageBinding<RoundRespawnRequested>), typeof(IInboundMessageBinding<RoundSpawnAccepted>),
        typeof(IInboundMessageBinding<RoundDeathReported>), typeof(IMessageSender<RoundSpawnOffered>), typeof(IMessageSender<RoundPlayerRespawned>), typeof(IInboundMessageBinding<RoundTauntReported>),
        typeof(IMessageSender<RoundRespawnWaitChanged>), typeof(IInboundMessageBinding<RoundKillCreditReported>), typeof(IInboundMessageBinding<RoundFinishingBlowReported>), typeof(IMessageSender<RoundScoreUpdated>),
        typeof(IMessageSender<RoundScoreboard>), typeof(IMessageSender<ResultStageAdvanced>), typeof(IInboundMessageBinding<RoomChatSent>), typeof(IMessageSender<RoomChatReceived>),
        typeof(IInboundMessageBinding<RoomSessionChatSent>), typeof(IMessageSender<RoomSessionChatReceived>),
        typeof(IInboundMessageBinding<RoomSeatLockRequested>), typeof(IMessageSender<RoomSeatLockChanged>), typeof(IInboundMessageBinding<RoomKickRequested>), typeof(IMessageSender<RoomMemberKicked>),
        typeof(IInboundMessageBinding<PlazaEnterRequested>), typeof(IInboundMessageBinding<PlazaPositionReported>), typeof(IInboundMessageBinding<PlazaMenuReturnRequested>), typeof(IMessageSender<PlazaEntered>),
        typeof(IInboundMessageBinding<PlazaRecruitmentRequested>), typeof(IInboundMessageBinding<PlazaRecruitmentTextRequested>),
        typeof(IMessageSender<PlazaRecruitmentChanged>), typeof(IMessageSender<PlazaRecruitmentTextReceived>),
        typeof(IInboundMessageBinding<PlazaRecruitmentListRequested>), typeof(IMessageSender<PlazaRecruitmentListReceived>),
        typeof(IMessageSender<PlazaRecruitmentRejected>),
        typeof(IMessageSender<PlazaMemberEntered>), typeof(IMessageSender<PlazaMemberLeft>), typeof(IMessageSender<PlazaPositionReceived>), typeof(IMessageSender<PlazaMenuReturnRequested>),
        typeof(IInboundMessageBinding<RoundSuppliesReported>), typeof(IInboundMessageBinding<RoundSupplyPickupRequested>), typeof(IInboundMessageBinding<RoundSupplyDropRequested>), typeof(IMessageSender<RoundSupplyCreated>),
        typeof(IMessageSender<RoundSupplyPicked>), typeof(IInboundMessageBinding<RoundWeaponDropRequested>), typeof(IInboundMessageBinding<RoundWeaponPickupRequested>),
        typeof(IMessageSender<RoundWeaponCreated>), typeof(IMessageSender<RoundWeaponPicked>),
        typeof(IInboundMessageBinding<RoundLucciDropRequested>), typeof(IInboundMessageBinding<RoundHordeAwardRequested>), typeof(IInboundMessageBinding<RoundLucciPickupRequested>),
        typeof(IMessageSender<RoundLucciCreated>), typeof(IMessageSender<RoundLucciPicked>), typeof(IInboundMessageBinding<RoundStarDropRequested>), typeof(IInboundMessageBinding<RoundStarPickupRequested>), typeof(IInboundMessageBinding<RoundStarPointsReported>), typeof(IMessageSender<RoundStarFeverStarted>),
        typeof(IInboundMessageBinding<RoundStarReported>), typeof(IMessageSender<RoundStarCreated>), typeof(IMessageSender<RoundStarPicked>), typeof(IInboundMessageBinding<RoundItemUseRequested>),
        typeof(IMessageSender<RoundItemUsed>), typeof(IInboundMessageBinding<RoundItemSlotSwitchRequested>), typeof(IMessageSender<RoundItemSlotChanged>), typeof(IInboundMessageBinding<RoundInstantRevivalRequested>),
        typeof(IInboundMessageBinding<RoundPveRevivalRequested>), typeof(IInboundMessageBinding<RoundCountedItemUseRequested>), typeof(IMessageSender<RoundCountedItemsPrepared>), typeof(IMessageSender<RoundCountedItemUsed>),
        typeof(IInboundMessageBinding<RoundRescueProgressReported>), typeof(IMessageSender<RoundRescueMissionsPrepared>), typeof(IMessageSender<RoundRescueMissionStarted>), typeof(IMessageSender<RoundRescueMissionFailed>),
        typeof(IMessageSender<RoundRescueMissionSucceeded>), typeof(IInboundMessageBinding<RoundPvePangReported>), typeof(IInboundMessageBinding<RoomStartUnavailableRequested>), typeof(IInboundMessageBinding<RoomUiRefreshRequested>),
        typeof(IMessageSender<RoomUiChanged>), typeof(IMessageSender<RoomProbeAcknowledged>), typeof(IInboundMessageBinding<RoundObjectAddRequested>), typeof(IMessageSender<RoundObjectAddAcknowledged>),
        typeof(IInboundMessageBinding<RoundMovementReported>), typeof(IMessageSender<RoundMovementRelayed>), typeof(IInboundMessageBinding<RoundLifeReported>),
        typeof(IInboundMessageBinding<PartyInviteRequested>), typeof(IInboundMessageBinding<PartyInviteAccepted>), typeof(IInboundMessageBinding<PartyInviteAnswered>),
        typeof(IInboundMessageBinding<PartyInviteTokenRefused>), typeof(IMessageSender<PartyInviteNotified>), typeof(IMessageSender<PartyInviteReplied>), typeof(IMessageSender<PartyUpdated>),
        typeof(IMessageSender<PartyLeaderReplaced>), typeof(IMessageSender<PartyLeft>), typeof(IMessageSender<PartyMemberLeft>), typeof(IMessageSender<PartyMemberJoined>),
    ])]
public sealed class RoomsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<RoomsOptions>();
        if (context.Row.ClientBuild is null)
            throw new ConfigException("dialect.rooms requires an explicit client_build.");
        if (options.CustomRoomRules && (!options.ModernRooms || options.PlazaOnly || options.MenuOnly || options.BrowserOnly))
            throw new ConfigException("custom_room_rules requires the audited modern waiting-room surface.");
        if (options.RoomKicking && (!options.ModernRooms || options.PlazaOnly))
            throw new ConfigException("room_kicking requires modern_rooms with waiting-room bindings, not plaza_only.");
        if (options.RoomInventorySelection && (!options.TypedPlayRooms || options.ModernRooms || options.MenuOnly || options.BrowserOnly))
            throw new ConfigException("room_inventory_selection requires the typed legacy waiting-room surface.");
        if (options.NpcRounds && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly))
            throw new ConfigException("npc_rounds requires modern_rooms with round bindings.");
        if ((options.BombMission || options.CaptainMode) && (options.MenuOnly || options.BrowserOnly
            || options.PlazaOnly || options.WaitingRoomsOnly || !options.ModernRooms && !options.TypedPlayRooms))
            throw new ConfigException("bomb_mission and captain_mode require the complete typed round surface.");
        if (options.ExtremeRacing && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly))
            throw new ConfigException("extreme_racing requires modern_rooms with round bindings.");
        if (options.ExtremeRacingInitialization && !options.ExtremeRacing)
            throw new ConfigException("extreme_racing_initialization requires extreme_racing.");
        if (options.ExtremeRacingCannon && !options.ExtremeRacingInitialization)
            throw new ConfigException("extreme_racing_cannon requires extreme_racing_initialization.");
        if (options.WeaponMaster && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly
            || !options.ResultScoreboardModes.Contains((int)GameModeId.WeaponMaster)))
            throw new ConfigException("weapon_master requires modern round bindings and result_scoreboard_modes including mode 26.");
        if (options.BnB && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly
            || !options.ResultScoreboardModes.Contains((int)GameModeId.BnB)))
            throw new ConfigException("bnb requires modern round bindings and result_scoreboard_modes including mode 41.");
        if (options.RheaCharacterSkill && (!options.ModernRooms || options.MenuOnly || options.BrowserOnly
            || options.PlazaOnly || options.WaitingRoomsOnly))
            throw new ConfigException("rhea_character_skill requires modern_rooms with round bindings.");
        if (options.ExtremeRacing && !options.ResultScoreboardModes.Contains((int)GameModeId.ExtremeRacing))
            throw new ConfigException("extreme_racing requires result_scoreboard_modes to include mode 24.");
        if (options.FinalZone && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly
            || !options.ResultScoreboardModes.Contains((int)GameModeId.FinalZone)))
            throw new ConfigException("final_zone requires modern round bindings and result_scoreboard_modes including mode 30.");
        if (options.ScriptZombies && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly
            || !options.ResultScoreboardModes.Contains(8)))
            throw new ConfigException("script_zombies requires modern round bindings and result_scoreboard_modes including mode 8.");
        if (options.ZombieEscape && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly
            || !options.ResultScoreboardModes.Contains((int)GameModeId.ZombieEscape)))
            throw new ConfigException("zombie_escape requires modern round bindings and result_scoreboard_modes including mode 32.");
        if (options.GhostZombies && (!options.ModernRooms || options.PlazaOnly || options.WaitingRoomsOnly
            || !options.ResultScoreboardModes.Contains(28)))
            throw new ConfigException("ghost_zombies requires modern round bindings and result_scoreboard_modes including mode 28.");
        if (options.StarItems && options.ModernRooms
            && (!options.ResultScoreboardModes.Contains((int)GameModeId.PvpStar)
                || !options.ResultScoreboardModes.Contains((int)GameModeId.PvpStarTeam)))
            throw new ConfigException("star_items on modern rooms requires result_scoreboard_modes including modes 5 and 6.");
        var lineage = context.Row.LineageOrBuild();
        if (options.ModernRooms)
        {
            ActivateModern(context, options, lineage);
            return ValueTask.CompletedTask;
        }
        if (options.PlazaOnly)
            throw new ConfigException("plaza_only requires modern_rooms.");
        if (options.WaitingRoomsOnly)
            throw new ConfigException("waiting_rooms_only requires modern_rooms.");
        if (options.InitialTutorial)
            throw new ConfigException("initial_tutorial requires modern_rooms and its typed room and round bindings.");
        // Every build's structures come from the packet tables; what the tables do not state for a
        // build is its base's, and the summary says how much is stated for each.
        var builds = RoomTables.Load(options.PacketsDirectory);
        foreach (var line in builds.Summary)
            context.Logger.LogInformation("dialect.rooms: {Line}.", line);
        var room = builds.Schemas.Require(lineage);
        if (options.BombMission && !builds.Tables.CapabilityOf(room.Profile, "bomb_mission"))
            throw new ConfigException($"bomb_mission requires the build's audited native capability: {room.Profile}.");
        if (options.CaptainMode && !builds.Tables.CapabilityOf(room.Profile, "captain_mode"))
            throw new ConfigException($"captain_mode requires the build's audited native capability: {room.Profile}.");
        var shape = room.Value;
        context.Logger.LogInformation("dialect.rooms: room layout of {Variant} ({Match}) for lineage [{Lineage}].",
            room.Profile, room.Match, string.Join(", ", lineage));
        var tables = context.Services.Get<IOpcodeTableRegistry>();
        if (UpdatePartyCodec.NeedsIdleParty(shape))
        {
            _ = builds.Tables.Resolve(room.Profile, "SoloPartySnapshot");
            _ = builds.Tables.Resolve(room.Profile, "SoloPartyMember");
        }
        // The move-in notice states its accepted flag, channel word and trailing word exactly as the empty-values
        // writer sizes them; the defaults are the table's (cn-279 sub_6F7DC0 reads all three).
        BubbleFighter.Fantasy.Packets.PacketLayoutContract.RequireFields(builds.Tables, room.Profile, "TutorialMoveInto", "Room move-in",
            ("accepted", "bool"), ("channel", "u32"), ("unknown_12", "u32"));
        _ = builds.Tables.Resolve(room.Profile, "RoomSceneTransition");
        if (options.PlayerChannel <= 0)
            throw new ConfigException($"dialect.rooms player_channel must be a positive channel, not {options.PlayerChannel}: a move reply with channel 0 is refused (cn-324 sub_452520).");
        context.Effect(context.Services.RegisterMany<IMessageSender<MainMenuRoomOpened>>(new RoomBinding(options.Protocol, shape, tables, options.TutorialMoveIn,
            builds.Tables, room.Profile, options.PlayerChannel, options.AutomaticTutorial)));

        if (options.MenuOnly)
            return ValueTask.CompletedTask;

        if (options.BrowserOnly)
        {
            var browserPlay = builds.Play.Resolve(lineage)
                ?? throw new ConfigException("dialect.rooms browser_only requires a waiting-room layout.");
            new PlayRoomCodec(options.Protocol, room.Profile, shape, browserPlay.Value, tables, context.Logger, builds.Tables)
                { PlayerChannel = options.PlayerChannel }.MountBrowser(context);
            return ValueTask.CompletedTask;
        }

        // Tutorial select and start, and the plaza list / create / ready / start requests: only on
        // builds whose packets are proven (PlayValues). None of these opcodes is part of the
        // login burst; they fire after GotoLobby or inside RoomStage.
        if (builds.Play.Resolve(lineage) is { Value: { } play })
        {
            if (options.TutorialUseOldMap && play.Tutorial.OldMapKey.Length == 0)
                throw new ConfigException("dialect.rooms tutorial_use_old_map needs a build with tutorial.old_map_key.");
            var table = tables.ForProtocol(options.Protocol);
            var router = context.Services.Get<IPacketRouter>();
            if (options.PlazaRecruitment)
                new PlazaRecruitmentBinding(options.Protocol, table, builds.Tables, room.Profile, context.Logger).Mount(context);
            var tutorial = new TutorialHandler(builds.Tables, room.Profile, shape, play.Tutorial, play.MatchStart, play.Wire, tables, context.Logger, play.OneSeatWaves, play.Score.OutcomeFirst, shape.ScoreRow, play.Score, play.TutorialSkipStart, options.TutorialUseOldMap, options.MaxRooms,
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
                && table.Find(PacketDirection.ClientToServer, "gcpReqPlayTutorial") is { } playTutorial)
            {
                context.Effect(router.Register(options.Protocol, createTutorial, tutorial.HandleCreateTutorialAsync));
                context.Effect(router.Register(options.Protocol, playTutorial, tutorial.HandlePlayTutorialAsync));
                // The cleared-stage list came later: kr-186 has no Packet_gcpReqSingleStageInfo class.
                if (table.Find(PacketDirection.ClientToServer, "gcpReqSingleStageInfo") is { } stageInfo)
                    context.Effect(router.Register(options.Protocol, stageInfo, tutorial.HandleSingleStageInfoAsync));
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
                // Resolve both wave-table replies before mounting requests, so incomplete opcode data
                // fails at activation rather than disconnecting a host when the first wave loads.
                _ = table.S2C("Unknown908");
                _ = table.S2C("gspRepWaveAllSpawnMobs");
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
                if (builds.Tables.CapabilityOf(room.Profile, "tutorial_practice_progress"))
                    new TutorialPracticeBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context);
                if (options.RoomInventorySelection)
                {
                    if (!builds.Tables.CapabilityOf(room.Profile, "room_inventory_selection"))
                        throw new ConfigException("room_inventory_selection requires an audited native build capability.");
                    new RoomInventoryBinding(options.Protocol, table, builds.Tables, room.Profile, context.Logger).Mount(context);
                }
                // The play row owns the rooms; this row only decodes and encodes for it. Score
                // reports name a client reference, so the codec resolves the reporter's seat
                // through the roster the play row publishes and never guesses one.
                if (!context.Services.TryGet<IMessageRouter>(out var messageRouter) || messageRouter is null)
                    throw new ConfigException("dialect.rooms typed_play_rooms needs the core.message-router row.");
                new PlayRoomCodec(options.Protocol, room.Profile, shape, play, tables, context.Logger, builds.Tables)
                {
                    PlayerChannel = options.PlayerChannel,
                    LoadReportFirst = tutorial.TryHandleMapLoadingCompleteAsync,
                    EndStageFirst = tutorial.TryHandleEndStageAsync,
                    StartRequestFirst = tutorial.TryHandleEnterGameAsync,
                    LeaveRequestFirst = tutorial.TryHandleLeaveAsync,
                    MultiplayerRoomEntering = tutorial.ClearTutorialStateAsync,
                    ReadyRequestFirst = tutorial.TryHandleReadyAsync,
                    SelectMatchRelay = options.SelectMatchRelay,
                    UdpProtocol = options.UdpEnabled ? options.UdpProtocol : string.Empty,
                    UdpKeepaliveMs = options.UdpKeepaliveMs,
                    EchoDirectItemEffects = options.EchoDirectItemEffects
                        && builds.Tables.CapabilityOf(room.Profile, "direct_item_effect_echo"),
                    StarReports = options.StarItems && builds.Tables.CapabilityOf(room.Profile, "star_items"),
                    CaptainKills = options.CaptainMode,
                }.Mount(context);
                if (table.Find(PacketDirection.ServerToClient, "gspRepAutoStart") is not null)
                {
                    _ = builds.Tables.Resolve(room.Profile, "gspRepAutoStart");
                    context.Effect(context.Services.RegisterMany<IMessageSender<RoomAutoStartNotified>>(
                        new AutoStartCodec(options.Protocol, builds.Tables, room.Profile, tables)));
                }
                if (builds.Tables.CapabilityOf(room.Profile, "map_portals"))
                    new PortalMoveBinding(options.Protocol, table, builds.Tables, room.Profile).Mount(context);
                MountObjectEconomy(context, options, table, builds.Tables, room.Profile, tutorial.TryHandleMapSuppliesAsync);
                // The bomb-mission state exchange is a round adapter like the object economy: the round flow owns
                // the decisions, this row only translates the audited native frames.
                if (options.BombMission)
                {
                    new BombModeBinding(options.Protocol, table, builds.Tables, room.Profile, context.Logger).Mount(context);
                }
                if (options.CaptainMode)
                    new CaptainModeBinding(options.Protocol, table, builds.Tables, room.Profile, context.Logger).Mount(context);
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
    /// <summary>
    /// Mounts the modern family's typed room families: the room commands, the plaza scene, the round start,
    /// the result screens, the action feed (game and relay connections) and the movement relay, plus the room
    /// list. Each family binds only what the build's opcode table names, exactly as the modern message row
    /// mounted them before the port; the bytes are pinned by the typed scenario parity suite.
    /// </summary>
    private static void ActivateModern(IPluginContext context, RoomsOptions options, IReadOnlyList<string> lineage)
    {
        if (options.WaitingRoomsOnly && (options.PlazaOnly || options.InitialTutorial))
            throw new ConfigException("waiting_rooms_only cannot be combined with plaza_only or initial_tutorial.");
        var table = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (options.PlazaOnly)
        {
            if (options.InitialTutorial)
                throw new ConfigException("plaza_only cannot mount the initial tutorial without room and round bindings.");
            var plazaPackets = BubbleFighter.Fantasy.Packets.PacketTables.Load(options.PacketsDirectory);
            var plazaBuild = plazaPackets.NearestBuild(lineage)
                ?? throw new ConfigException("plaza_only requires an audited packet-table build.");
            new PlazaSceneBinding(options.Protocol, table, plazaPackets, plazaBuild, context.Logger).Mount(context);
            new RoomChatBinding(options.Protocol, table, plazaPackets, plazaBuild, context.Logger).Mount(context);
            return;
        }
        if (table.Find(PacketDirection.ClientToServer, "gcpReqListSession") is not { } roomListRequest
            || table.Find(PacketDirection.ServerToClient, "gspRepListSession") is not { } roomListReply)
            throw new ConfigException(
                "dialect.rooms modern_rooms requires a table that names the room list (gcpReqListSession/gspRepListSession).");
        var packets = BubbleFighter.Fantasy.Packets.PacketTables.Load(options.PacketsDirectory);
        var tableBuild = packets.NearestBuild(lineage)
            ?? throw new ConfigException($"dialect.rooms modern_rooms: no build of [{string.Join(", ", lineage)}] is named in '{options.PacketsDirectory}'.");
        var customRules = options.CustomRoomRules ? new CustomRulesBinding(options.Protocol, table, packets, tableBuild, context.Logger) : null;
        customRules?.Validate();
        if (options.BombMission && !packets.CapabilityOf(tableBuild, "bomb_mission"))
            throw new ConfigException($"bomb_mission requires the build's audited native capability: {tableBuild}.");
        if (options.CaptainMode && !packets.CapabilityOf(tableBuild, "captain_mode"))
            throw new ConfigException($"captain_mode requires the build's audited native capability: {tableBuild}.");
        if (options.ExtremeRacing && !packets.CapabilityOf(tableBuild, "extreme_racing"))
            throw new ConfigException($"extreme_racing requires the build's audited native capability: {tableBuild}.");
        if (options.ExtremeRacingInitialization && !packets.CapabilityOf(tableBuild, "extreme_racing_initialization"))
            throw new ConfigException($"extreme_racing_initialization requires the audited initialization capability: {tableBuild}.");
        if (options.ExtremeRacingCannon && !packets.CapabilityOf(tableBuild, "extreme_racing_cannon"))
            throw new ConfigException($"extreme_racing_cannon requires the audited cannon capability: {tableBuild}.");
        if (options.WeaponMaster && !packets.CapabilityOf(tableBuild, "weapon_master"))
            throw new ConfigException($"weapon_master requires the build's audited equipment capability: {tableBuild}.");
        if (options.BnB && !packets.CapabilityOf(tableBuild, "bnb"))
            throw new ConfigException($"bnb requires the audited map and relay capability: {tableBuild}.");
        if (options.ScriptZombies && !packets.CapabilityOf(tableBuild, "script_zombies"))
            throw new ConfigException($"script_zombies requires the audited native capability: {tableBuild}.");
        if (options.ZombieEscape && !packets.CapabilityOf(tableBuild, "zombie_escape"))
            throw new ConfigException($"zombie_escape requires the build's audited native capability: {tableBuild}.");
        if (options.GhostZombies && !packets.CapabilityOf(tableBuild, "ghost_zombies"))
            throw new ConfigException($"ghost_zombies requires the audited native capability: {tableBuild}.");
        if (options.FinalZone && !packets.CapabilityOf(tableBuild, "final_zone"))
            throw new ConfigException($"final_zone requires the build's audited native capability: {tableBuild}.");
        if (options.RheaCharacterSkill && !packets.CapabilityOf(tableBuild, "rhea_character_skill"))
            throw new ConfigException($"rhea_character_skill requires the build's audited native capability: {tableBuild}.");
        context.Effect(context.Services.RegisterMany<IMessageSender<MainMenuRoomOpened>>(
            new TableMenuOpenSender(options.Protocol, table.S2C("gspGotoRoom"), packets, tableBuild)));
        new RoomListCodec(options.Protocol, roomListRequest, roomListReply, packets, tableBuild, context.Logger).Mount(context);
        // roundsBound: the room commands binding also resolves the round-return names (gspUdpStop, Unknown620) a
        // result hands the waiting room, which every typed round needs.
        new RoomCommandsBinding(options.Protocol, table, packets, tableBuild, context.Logger,
            roundsBound: !options.WaitingRoomsOnly, roomKicking: options.RoomKicking, scriptZombies: options.ScriptZombies || options.GhostZombies || options.ZombieEscape).Mount(context);
        customRules?.Mount(context);
        if (table.Find(PacketDirection.ClientToServer, "gcpRoomAddNpcBot_") is not null)
            new RoomAiBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        new PlazaSceneBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        if (options.PlazaRecruitment)
            new PlazaRecruitmentBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        if (options.WaitingRoomsOnly)
            return;
        new RoundStartBinding(options.Protocol, table, packets, tableBuild, options.RoundLoadingFlags, context.Logger)
        {
            MapReportBound = options.SupplyItems || options.StarItems,
        }.Mount(context);
        if (options.InitialTutorial)
        {
            new TutorialEntryBinding(options.Protocol, table, packets, tableBuild,
                InitialTutorialRoute.Load(packets, tableBuild, context.Row.ClientBuild
                    ?? throw new ConfigException("Initial tutorial requires an explicit client build."), options.InitialTutorialKind), context.Logger).Mount(context);
            new TutorialNpcBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        }
        if (options.InitialTutorial || options.ZombieEscape)
            new StageResultBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        // The tutorial's waves and spawns are ordinary NPC operations; its round decides who is answered.
        if (options.InitialTutorial || options.NpcRounds)
            new NpcBinding(options.Protocol, table, packets, tableBuild, context.Logger)
            {
                RelayProtocol = options.NpcRounds ? options.RelayProtocol : string.Empty,
                RelayTable = options.NpcRounds
                    ? context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol) : null,
            }.Mount(context);
        if (options.ScriptZombies || options.GhostZombies || options.ZombieEscape)
        {
            var modes = new HashSet<GameModeId>();
            if (options.ScriptZombies) modes.Add(GameModeId.Zombie);
            if (options.GhostZombies) modes.Add(GameModeId.ZombieGhost);
            if (options.ZombieEscape) modes.Add(GameModeId.ZombieEscape);
            new ScriptZombieBinding(options.Protocol, options.RelayProtocol, table,
                context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol), packets, tableBuild, modes).Mount(context);
        }
        new RoundMovementBinding(options.Protocol, options.RelayProtocol, table,
            context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol), packets, tableBuild,
            context.Logger, options.UdpEnabled ? options.UdpProtocol : string.Empty, options.UdpKeepaliveMs).Mount(context);
        new RoundFeedBinding(options.Protocol, options.RelayProtocol, table,
            context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol), packets, tableBuild)
        {
            ExtremeRacingCannon = options.ExtremeRacingCannon,
        }.Mount(context);
        if (packets.CapabilityOf(tableBuild, "map_portals"))
            new PortalMoveBinding(options.Protocol, table, packets, tableBuild).Mount(context);
        if (options.RheaCharacterSkill)
            new CharacterFormBinding(options.Protocol, table, packets, tableBuild).Mount(context);
        new RoundResultBinding(options.Protocol, table, packets, tableBuild, options.ResultScoreboardModes, context.Logger).Mount(context);
        if (options.BombMission)
            new BombModeBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        if (options.CaptainMode)
            new CaptainModeBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        if (options.ExtremeRacing)
        {
            var racing = new ExtremeRacingBinding(options.Protocol, table, packets, tableBuild, context.Logger);
            racing.Mount(context);
            if (options.ExtremeRacingInitialization) racing.MountInitialization(context);
            if (options.ExtremeRacingCannon) racing.MountCannon(context);
        }
        if (options.WeaponMaster)
            new WeaponMasterBinding(options.Protocol, table, packets, tableBuild, context.Logger).Mount(context);
        if (options.BnB)
        {
            var relayTable = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol);
            new BnBBinding(options.Protocol, relayTable, packets, tableBuild, context.Logger).Mount(context);
            new BnBBinding(options.RelayProtocol, relayTable, packets, tableBuild, context.Logger).Mount(context);
        }
        if (options.FinalZone)
        {
            new FinalZoneBinding(options.Protocol, table, packets, tableBuild).Mount(context);
            new FinalZoneTrapBinding(options.Protocol, options.RelayProtocol, table,
                context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol), packets, tableBuild).Mount(context);
        }
        if (options.FinalZone || options.ZombieEscape)
            new FinalZoneMapEffectBinding(options.Protocol, options.RelayProtocol, table,
                context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol), packets, tableBuild).Mount(context);
        if (options.ZombieEscape)
            new MapScriptObjectBinding(options.Protocol, options.RelayProtocol, table,
                context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol), packets, tableBuild).Mount(context);
        if (options.QuickMatching)
        {
            if (!context.Services.TryGet<IMessageRouter>(out var messageRouter) || messageRouter is null)
                throw new ConfigException("dialect.rooms quick_matching needs the core.message-router row.");
            // The request family is table data: the typed serializer request (kr-1256) and the
            // lobby-state request (cn-774 and tw-454) select their own binding, and a build with
            // neither is a configuration error.
            if (table.Find(PacketDirection.ClientToServer, "gcpReqMatch") is not null)
                new TypedMatchingBinding(options.Protocol, table, packets, tableBuild, options.QuickMatchDefaultModeId)
                    .Mount(context, messageRouter);
            else if (table.Find(PacketDirection.ClientToServer, "gcpReqLobbyState_") is not null)
                new LobbyStateMatchingBinding(options.Protocol, table, packets, tableBuild, options.QuickMatchDefaultModeId)
                    .Mount(context, messageRouter);
            else
                throw new ConfigException("dialect.rooms quick_matching requires a named matching request for this build.");
        }
        MountObjectEconomy(context, options, table, packets, tableBuild);
    }

    private static void MountObjectEconomy(IPluginContext context, RoomsOptions options, IOpcodeTable table,
        BubbleFighter.Fantasy.Packets.PacketTables packets, string build,
        Func<ISession, RoundSuppliesReported, CancellationToken, ValueTask<bool>>? mapReportFirst = null)
    {
        if (!(options.SupplyItems || options.WeaponObjects || options.LucciDrops || options.StarItems || options.HordeLottery
            || options.InstantRevival || options.PvePoints || options.RescueMissions || options.CountedPveItems)) return;
        if (!context.Services.TryGet<IMessageRouter>(out var messageRouter) || messageRouter is null)
            throw new ConfigException("dialect.rooms object economy needs the core.message-router row.");
        if (options.SupplyItems)
            new SupplyBinding(options.Protocol, table, packets, build, options.MaxSupplyPoints)
            {
                MapReportFirst = mapReportFirst,
                ReportsStars = options.StarItems,
            }.Mount(context, messageRouter);
        if (options.WeaponObjects)
            new WeaponObjectsBinding(options.Protocol, table, packets, build).Mount(context, messageRouter);
        if (options.LucciDrops)
            new LucciBinding(options.Protocol, table, packets, build).Mount(context, messageRouter);
        if (options.StarItems)
            new StarBinding(options.Protocol, table, packets, build)
            {
                MapReportLimit = options.SupplyItems ? 0 : options.MaxSupplyPoints,
                ReadsStarReports = options.ModernRooms,
            }.Mount(context, messageRouter);
        if (options.HordeLottery)
            new LotteryBinding(options.Protocol, table, packets, build).Mount(context, messageRouter);
        if (options.InstantRevival)
            new RevivalBinding(options.Protocol, table, packets, build).Mount(context, messageRouter);
        if (options.PvePoints)
            new PvePointsBinding(options.Protocol, table, packets, build).Mount(context, messageRouter);
        if (options.RescueMissions)
            new RescueBinding(options.Protocol, table, packets, build).Mount(context, messageRouter);
        if (options.CountedPveItems)
            new CountedItemsBinding(options.Protocol, table, packets, build).Mount(context, messageRouter);
        else if (options.SupplyItems && packets.CapabilityOf(build, "direct_supply_effects"))
            new CountedItemsBinding(options.Protocol, table, packets, build).MountEffect(context);
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
    BubbleFighter.Fantasy.Packets.PacketTables packetTables, string build,
    int playerChannel = RoomsOptions.FreeChannel, bool automaticTutorial = true) : IMessageSender<MainMenuRoomOpened>
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
        if (tutorialMoveIn && table.Find(PacketDirection.ServerToClient, "gspRepMoveInto") is { } moveInto)
        {
            // cn-279 sub_6F7DC0 (dispatcher case 10): bool32 then two i32 stored at session+2244/2248,
            // session+2240 set to 1; dispatcher case 0xB (S2C 11) then sets byte_ABB3DF, and the
            // MainMenuStage tick shows the auto-tutorial notice when both are set.
            var body = WriteMoveInto(packetTables, build, playerChannel);
            await session.SendAsync(moveInto, body, ct).ConfigureAwait(false);
            // cn-324 sub_705B20 sets connection readiness; sub_588190 separately checks the case-11 tutorial flag.
            if (automaticTutorial && message.OfferTutorial != false
                && table.Find(PacketDirection.ServerToClient, "gspNotifyAutoTutorial_") is { } notify)
                await session.SendAsync(notify, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        }
    }

    // The channel word is the player channel, so the tutorial offer leaves the player on the channel every
    // other move uses; the accepted flag and trailing word keep the table's defaults.
    internal static byte[] WriteMoveInto(BubbleFighter.Fantasy.Packets.PacketTables tables, string build,
        int channel = RoomsOptions.FreeChannel) =>
        BubbleFighter.Fantasy.Packets.PacketCodec.Write(tables, build, "TutorialMoveInto",
            new Dictionary<string, object?> { ["channel"] = channel });
}

/// <summary>Selects the game connection scope for the legacy room adapter.</summary>
public sealed class RoomsOptions
{
    /// <summary>Gets or sets the audited custom-option exchange within existing game modes; disabled by default.</summary>
    public bool CustomRoomRules { get; set; }

    /// <summary>Gets or sets whether audited native plaza recruitment controls and sign updates are mounted.</summary>
    public bool PlazaRecruitment { get; set; }

    /// <summary>
    /// Gets or sets whether the early temporary room selection request and notification are mounted.
    /// Requires typed legacy rooms, audited build data and this build's client-data service;
    /// YAML key <c>room_inventory_selection</c>. It does not enable saved weapon preset pages.
    /// </summary>
    public bool RoomInventorySelection { get; set; }

    /// <summary>
    /// Gets or sets whether the modern family's typed room families are mounted: the room commands, the plaza
    /// scene, the round start, the result screens, the action feed and the movement relay, each through the
    /// packet tables of the row's build. YAML key <c>modern_rooms</c>.
    /// </summary>
    public bool ModernRooms { get; set; }

    /// <summary>Gets or sets whether modern rooms mount only audited plaza entry, return and movement,
    /// without requiring waiting-room or round layouts. Requires <c>modern_rooms</c>.</summary>
    public bool PlazaOnly { get; set; }

    /// <summary>Gets or sets whether modern rooms stop at the audited waiting-room surface while round adapters are unavailable.</summary>
    public bool WaitingRoomsOnly { get; set; }

    /// <summary>
    /// Gets or sets whether modern waiting rooms bind the audited nickname-based kick request and notification
    /// to shared room-master authority. Disabled until native-client acceptance; YAML key <c>room_kicking</c>.
    /// </summary>
    public bool RoomKicking { get; set; }

    /// <summary>
    /// Gets or sets whether a host's native wave manager and NPC spawners are answered in ordinary modern rounds,
    /// as the pirate battle needs. YAML key <c>npc_rounds</c>.
    /// </summary>
    public bool NpcRounds { get; set; }

    /// <summary>
    /// Gets or sets whether the bomb-mission round's state exchange is mounted for the typed legacy round
    /// surface: the acting client's <c>gcpBombState</c> report is relayed to the room as <c>gspBombState</c> and
    /// the master's <c>gcpBombMissionOver</c> report ends the round through the shared win-condition path. Needs the
    /// build's <c>bomb_mission</c> capability. Also supplies the generic observation sender; the gameplay row's
    /// explicit respawn exclusions decide whether to use it instead of revival. YAML key <c>bomb_mission</c>.
    /// </summary>
    public bool BombMission { get; set; }

    /// <summary>
    /// Gets or sets whether the Captain round's exchange is mounted for the typed legacy round surface: the dying
    /// client's <c>gcpDropCaptain</c> report, the <c>gspCreateCaptain</c> announcement and the Captain-kill score
    /// report kind. Needs the build's <c>captain_mode</c> capability. YAML key <c>captain_mode</c>.
    /// </summary>
    public bool CaptainMode { get; set; }

    /// <summary>
    /// Gets or sets whether the audited Rhea transformation request and reply are mounted for modern rounds.
    /// Disabled pending native-client acceptance; YAML key <c>rhea_character_skill</c>.
    /// </summary>
    public bool RheaCharacterSkill { get; set; }

    /// <summary>
    /// Gets or sets whether the ExtremeRacing round's exchange (round mode 24) is mounted for modern rooms: the goal
    /// touch request and the race clock, round and record senders. Needs the build's <c>extreme_racing</c>
    /// capability; the checkpoint touch is a round-feed route of the build's data. Disabled until native-client
    /// acceptance; YAML key <c>extreme_racing</c>.
    /// </summary>
    public bool ExtremeRacing { get; set; }

    /// <summary>Gets or sets the native racing object initialization sender; requires an explicit map policy in the in-game row.</summary>
    public bool ExtremeRacingInitialization { get; set; }

    /// <summary>
    /// Gets or sets whether the racing cannon exchange is mounted: the S2C 718 command sender and the relay 32902 shot
    /// route. It schedules nothing and requires <c>extreme_racing_initialization</c> and the build's
    /// <c>extreme_racing_cannon</c> capability; the in-game row must enable it too. Disabled by default; YAML key
    /// <c>extreme_racing_cannon</c>.
    /// </summary>
    public bool ExtremeRacingCannon { get; set; }

    /// <summary>
    /// Gets or sets whether the WeaponMaster equipment frame (S2C 1001, round mode 26) is mounted for modern rooms. Needs
    /// the build's <c>weapon_master</c> capability and <c>result_scoreboard_modes</c> including 26; the in-game row's
    /// <c>weapon_master_*</c> settings choose the weapons. Disabled by default; YAML key <c>weapon_master</c>.
    /// </summary>
    public bool WeaponMaster { get; set; }

    /// <summary>Gets or sets the native BnB exchange on both match transports; disabled by default.</summary>
    public bool BnB { get; set; }

    /// <summary>Gets or sets the audited FinalZone item, elimination and map-script exchange; disabled by default.</summary>
    public bool FinalZone { get; set; }

    /// <summary>Gets or sets the audited native script zombie conversion exchange for modern mode 8.</summary>
    public bool ScriptZombies { get; set; }
    /// <summary>Gets or sets the audited Lua-driven Zombie Escape conversion and result exchange; disabled by default.</summary>
    public bool ZombieEscape { get; set; }

    /// <summary>Gets or sets the audited ghost-mode script and single-king conversion exchange for mode 28.</summary>
    public bool GhostZombies { get; set; }

    /// <summary>Gets or sets whether the audited first-run tutorial entry is mounted with modern rooms.</summary>
    public bool InitialTutorial { get; set; }

    /// <summary>Gets or sets the audited initial tutorial generation: classic, pirate_prologue or ai_battle.
    /// Only a generation explicitly supported by this build's packet data can be activated.</summary>
    public string InitialTutorialKind { get; set; } = "pirate_prologue";

    /// <summary>Gets or sets the relay connection's protocol scope the feed and movement bindings register on. YAML key <c>relay_protocol</c>.</summary>
    public string RelayProtocol { get; set; } = "kr-1256/relay";

    /// <summary>Gets or sets whether configured UDP movement routes are mounted; disabling them preserves TCP routes.</summary>
    public bool UdpEnabled { get; set; } = true;

    /// <summary>Gets or sets the UDP listener protocol used by the authenticated modern relay path; empty disables it.</summary>
    public string UdpProtocol { get; set; } = string.Empty;

    /// <summary>Gets or sets the interval between native UDP relay liveness probes, in milliseconds.</summary>
    [System.ComponentModel.DataAnnotations.Range(100, 60000)]
    public int UdpKeepaliveMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets whether a legacy round's loading gate selects the match relay for every seat that did not fall
    /// back to its game connection (first word of gspGameEverybodyLoadingComplete; cn-324 sub_6F0B30, kr-812
    /// sub_802E60). Turn it on together with the ingame row's native match relay. YAML key <c>select_match_relay</c>.
    /// </summary>
    public bool SelectMatchRelay { get; set; }

    /// <summary>Gets or sets the S2C 585 loading words the round start sends (kr-1256 start words 1, 0, 1). YAML key <c>round_loading_flags</c>.</summary>
    public int[] RoundLoadingFlags { get; set; } = [1, 0, 1];

    /// <summary>Gets or sets the mode ids whose results carry the full scoreboard. YAML key <c>result_scoreboard_modes</c>.</summary>
    public int[] ResultScoreboardModes { get; set; } = [1];

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

    /// <summary>
    /// Gets or sets the mode id the modern quick-match requests serve, because the modern request
    /// family (kr-1256 sub_114E2E0) carries no mode word. Zero refuses every request with the
    /// native generic failure. YAML key <c>quick_match_default_mode_id</c>.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(0, 65535)]
    public int QuickMatchDefaultModeId { get; set; }

    /// <summary>Gets or sets the legacy game connection protocol, such as <c>kr-812/game</c>.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/game";

    /// <summary>
    /// Gets or sets whether the plaza room requests are delivered as typed messages to the play row
    /// (<c>ingame</c>, which serves typed messages only), which then owns multi-player rooms. Off, this
    /// row keeps its own one-seat rooms. It needs the build's play-room layout, so only builds that
    /// have one may turn it on.
    /// </summary>
    public bool TypedPlayRooms { get; set; } = true;

    /// <summary>Gets or sets whether the row binds proven map supply reports, creation and pickup packets.</summary>
    public bool SupplyItems { get; set; }

    /// <summary>Gets or sets whether the row binds audited native dropped-weapon creation and pickup messages.</summary>
    public bool WeaponObjects { get; set; }

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
    /// Gets or sets whether the login burst sends the native menu connection-ready reply.
    /// cn-279 sub_6F7DC0 consumes the move-in and tutorial notification separately;
    /// <see cref="AutomaticTutorial"/> controls only the latter. YAML key <c>tutorial_move_in</c>.
    /// </summary>
    public bool TutorialMoveIn { get; set; }

    /// <summary>
    /// Gets or sets whether the login room sends the automatic tutorial notification after its move-in reply.
    /// Disabling it retains the connection-ready reply required by native room requests. YAML key <c>automatic_tutorial</c>.
    /// </summary>
    public bool AutomaticTutorial { get; set; } = true;

    /// <summary>Channel 60, "Free": a column of the FighterConfigChannel sheet in every client checked.</summary>
    public const int FreeChannel = 60;

    /// <summary>
    /// Gets or sets the channel the room adapter's move replies place the player in: the plaza entry's scene
    /// move and the tutorial offer's move-in. YAML key <c>player_channel</c>; positive, default 60 ("Free").
    /// <para>
    /// Both are <c>gspRepMoveInto</c>, whose channel word the client keeps at <c>ServerConnection+2432</c>
    /// (kr-812 <c>sub_81C6F0</c>). At round start the client reads that channel's column of the
    /// <c>FighterConfigChannel</c> sheet in <c>Config/FighterConfig.xml</c> (kr-812 <c>sub_587690</c>,
    /// <c>sub_584320</c>), so it decides the round's movement and bubble values, and a channel without a column
    /// crashes the round: channel 50, which the move reply admits, has none in CN2011, KR2015 or kr-1256, while
    /// 60 is "Free" in all three. The server keeps one shared lobby, so every client is placed on 60.
    /// </para>
    /// <para>
    /// The bundles set it from <c>${BFF_PLAYER_CHANNEL:-60}</c>, the variable that also feeds the
    /// dialect.messages <c>login_channel</c> and the cluster move, so all paths agree. Zero is refused: the
    /// move reply with channel 0 is rejected (cn-324 <c>sub_452520</c>).
    /// </para>
    /// </summary>
    public int PlayerChannel { get; set; } = FreeChannel;

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

/// <summary>
/// The table-menu family's <see cref="IMessageSender{MainMenuRoomOpened}"/>: the main-menu
/// <c>gspGotoRoom</c> install the login path itself writes, published as the row's menu-open sender so
/// every menu-open caller (and the row graph) resolves it from this row.
/// </summary>
internal sealed class TableMenuOpenSender(string protocol, ushort opcode, BubbleFighter.Fantasy.Packets.PacketTables packets, string build)
    : IMessageSender<MainMenuRoomOpened>
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, MainMenuRoomOpened message, CancellationToken ct)
    {
        var character = message.CharacterItemId is >= 300001 and <= 301999
            ? message.CharacterItemId
            : throw new ArgumentOutOfRangeException(nameof(message), "The modern menu needs a 300001..301999 character item.");
        return session.SendAsync(opcode, MainMenuInstall.Write(packets, build, message.UserNumber,
            message.Nickname, message.Level, character, message.EquippedItemIds, ItemAttributeWire.Values(message.ItemAttributes)), ct);
    }
}
