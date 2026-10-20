using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using Microsoft.Extensions.Logging;
namespace BubbleFighter.Fantasy.InGame;

/// <summary>
/// In-session play after login: plaza enter, room create / join / ready, and the round's start
/// and result on the game and relay connections. In-round traffic between the players belongs
/// to the match-relay row. Dialect 1256 is selected by <c>version_major</c>.
/// This row does not emit packets on the lobby login 3 to 163 chain.
///
/// </summary>
[Plugin("ingame",
    Description = "Plaza enter, room create/join/ready, and combat. Dialect selected by version_major.", 
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter)],
    OptionalMany = [typeof(IMatchCapabilityOfferSender), typeof(IInboundMessageBinding<MatchConnectionReported>), typeof(IMessageSender<RoomTeamConverted>), typeof(IInboundMessageBinding<RoomMatchRequested>), typeof(IMessageSender<RoomMatchNotified>), typeof(IMessageSender<RoomAutoStartNotified>), typeof(IMessageSender<ServerNotice>), typeof(IMessageSender<MatchSessionSignature>),
        typeof(IInboundMessageBinding<RoomListRequested>), typeof(IMessageSender<RoomListSnapshot>), typeof(IMessageSender<PartyUpdated>),
        typeof(IInboundMessageBinding<RoomCreateRequested>), typeof(IInboundMessageBinding<RoomJoinRequested>),
        typeof(IInboundMessageBinding<RoomLeaveRequested>), typeof(IInboundMessageBinding<RoomReadyRequested>),
        typeof(IInboundMessageBinding<RoomUiRefreshRequested>), typeof(IMessageSender<RoomUiChanged>), typeof(IMessageSender<RoomProbeAcknowledged>),
        typeof(IMessageSender<RoomEntered>), typeof(IMessageSender<RoomSeatUpdated>), typeof(IMessageSender<RoomSeatVacated>),
        typeof(IMessageSender<RoomMasterChanged>), typeof(IMessageSender<RoomJoinRefused>), typeof(IMessageSender<RoomLeft>),
        typeof(IInboundMessageBinding<RoomMasterChangeRequested>), typeof(IMessageSender<RoomMasterChangeReplied>),
        typeof(IInboundMessageBinding<RoomTeamChangeRequested>), typeof(IInboundMessageBinding<RoomStartRequested>),
        typeof(IInboundMessageBinding<RoomStartUnavailableRequested>),
        typeof(IMessageSender<RoomMatchStarting>), typeof(IInboundMessageBinding<RoomSettingsChangeRequested>),
        typeof(IInboundMessageBinding<RoomRoundSettingsChangeRequested>),
        typeof(IMessageSender<RoomSettingsChanged>), typeof(IMessageSender<MainMenuRoomOpened>),
        typeof(IInboundMessageBinding<RoundLoadReported>), typeof(IInboundMessageBinding<RoundFeedSent>),
        typeof(IMessageSender<RoundPlayerLoaded>), typeof(IMessageSender<RoundCountdownStarted>),
        typeof(IMessageSender<RoundStarted>), typeof(IMessageSender<RoundFeedReceived>),
        typeof(IInboundMessageBinding<RoundObjectAddRequested>), typeof(IMessageSender<RoundObjectAddAcknowledged>),
        typeof(IInboundMessageBinding<RoundMovementReported>), typeof(IMessageSender<RoundMovementRelayed>),
        typeof(IInboundMessageBinding<RoundLifeReported>), typeof(IInboundMessageBinding<RoundResetRequested>),
        typeof(IMessageSender<RoundResetGranted>),
        typeof(IInboundMessageBinding<RoundLotteryRequested>), typeof(IMessageSender<RoundLotteryOpened>), typeof(IMessageSender<RoundLotteryClosed>), typeof(IMessageSender<RoundLotterySelected>), typeof(IMessageSender<RoundLotteryRevealed>), typeof(IInboundMessageBinding<RoundEndRequested>), typeof(IMessageSender<RoundEnded>),
        typeof(IInboundMessageBinding<RoundRespawnRequested>), typeof(IInboundMessageBinding<RoundSpawnAccepted>), typeof(IInboundMessageBinding<RoundDeathReported>),
        typeof(IMessageSender<RoundSpawnOffered>),
        typeof(IMessageSender<RoundPlayerRespawned>),
        typeof(IInboundMessageBinding<RoundTauntReported>), typeof(IMessageSender<RoundRespawnWaitChanged>),
        typeof(IInboundMessageBinding<RoomChatSent>), typeof(IMessageSender<RoomChatReceived>), typeof(IInboundMessageBinding<RoomSeatLockRequested>),
        typeof(IMessageSender<RoomSeatLockChanged>), typeof(IInboundMessageBinding<RoomKickRequested>), typeof(IMessageSender<RoomMemberKicked>),
        typeof(IInboundMessageBinding<PlazaEnterRequested>), typeof(IInboundMessageBinding<PlazaPositionReported>),
        typeof(IInboundMessageBinding<PlazaMenuReturnRequested>),
        typeof(IMessageSender<PlazaEntered>), typeof(IMessageSender<PlazaMemberEntered>), typeof(IMessageSender<PlazaMemberLeft>),
        typeof(IMessageSender<PlazaPositionReceived>), typeof(IMessageSender<PlazaMenuReturnRequested>),
        typeof(IInboundMessageBinding<RoundKillCreditReported>), typeof(IInboundMessageBinding<RoundFinishingBlowReported>),
        typeof(IMessageSender<RoundScoreUpdated>), typeof(IMessageSender<RoundScoreboard>), typeof(IMessageSender<ResultStageAdvanced>),
        typeof(IMessageSender<RoundPlayerWalletChanged>), typeof(IMessageSender<RoundWaveChanged>),
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
        typeof(IInboundMessageBinding<RoundPvePangReported>), typeof(IMessageSender<RoundPvePointsChanged>)],
    Optional = [typeof(IPartyDirectory), typeof(IOnlinePlayers), typeof(BubbleFighter.Fantasy.Contracts.Shop.IShopPurchaseStore), typeof(IInventoryRepository), typeof(IInventoryPushService), typeof(IGameText), typeof(ISeatCharacterResolver), typeof(IEquippedViewPush), typeof(IEquipmentSelection), typeof(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore), typeof(BubbleFighter.Fantasy.Contracts.Progress.IPlayerProgressRepository), typeof(BubbleFighter.Fantasy.Contracts.Wallet.IPlayerWalletRepository), typeof(BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore), typeof(BubbleFighter.Fantasy.Contracts.Shop.IShopGiftService)],
    ProvidesMany = [typeof(IMatchCapabilityDirectory)],
    ConditionalProvides = [typeof(IMatchConnectionTracker), typeof(IMatchRoomDirectory), typeof(IRoomStartService), typeof(IRoomRoster), typeof(IRoundScoreboard)])]
public sealed class InGamePlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<InGameOptions>();
        LevelTable.LoadConfiguredFile(options);
        var modes = ModeCatalog.Load(options.ModeTablePath);
        var router = context.Services.Get<IPacketRouter>();
        var registry = new RoomRegistry(options, context.Logger);
        // Registered first so it runs last on unload: by then every handler and event subscription that
        // enqueues room work is gone, and room state is released only after the running operations return.
        var roomStopTimeout = TimeSpan.FromMilliseconds(options.RoomStopTimeoutMs);
        context.Effect(() =>
        {
            if (!registry.StopAllAsync().Wait(roomStopTimeout))
                throw new TimeoutException($"InGame room executors did not stop within {options.RoomStopTimeoutMs} ms.");
        });
        context.Effect(context.Services.RegisterMany<IMatchCapabilityDirectory>(registry));
        var chatRateLimit = new ChatRateLimit(options, context.Logger);
        context.Effect(chatRateLimit);
        context.Effect(context.Events.On<ConnectionClosedEvent>(chatRateLimit.OnConnectionClosed));
        if (options.TypedOnly)
        {
            // Protocol-scoped capabilities above can coexist; the singleton services below belong to the wire row.
            ActivateTypedOnly(context, options, registry, chatRateLimit);
            return ValueTask.CompletedTask;
        }

        // Publish the relay-bind classification so the lobby login handler can
        // leave a match connection's reconnect-shaped C2S 3 to the in-game seam
        // (the lobby router owns opcode 3; the routing boundary asks its gameplay owner and
        // avoids a reverse service dependency on a later activation).
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Match.IMatchConnectionTracker>(registry));
        context.Effect(context.Services.Register<IMatchRoomDirectory>(registry));
        context.Effect(context.Events.Intercept<LoginConnectionRouting, bool>((request, next, ct) =>
            request.Protocol == options.GameProtocol && registry.IsMatchBound(request.Session)
                ? ValueTask.FromResult(true) : next(ct)));
        // In-round traffic belongs to the match-relay row: it reads the seats through the roster and
        // adds team points through the scoreboard, which stay on the room for the result and rewards.
        var roster = new RoomRoster(registry, context.Logger, context.Lifetime);
        context.Effect(context.Services.Register<IRoomRoster>(roster));
        context.Effect(context.Services.Register<IRoundScoreboard>(roster));
        // Scene maps and S2C 806 must resolve the same stored selection (0x81D060 / 0x81CCE0).
        context.Services.TryGet<IEquipmentSelection>(out var selection);
        if (selection is not null)
            context.Logger.LogInformation("InGame scene character ids resolve through the authoritative IEquipmentSelection.");
        var characters = context.Services.TryGet<ISeatCharacterResolver>(out var resolver) && resolver is not null
            ? resolver
            : null;
        if (characters is not null)
            context.Logger.LogInformation("InGame seat character ids resolve through a published ISeatCharacterResolver.");
        // The equipped view (S2C 806) is the write that puts a player's equipped
        // character and weapons into the profile map the in-room spawner and the
        // appearance rebuilder read. A working reference session sends it as part
        // of its room-entry sequence, right after the room install. The service
        // is optional: without the row the room path simply does not send it.
        var equippedView = context.Services.TryGet<IEquippedViewPush>(out var push) && push is not null
            ? push
            : null;
        if (equippedView is not null)
            context.Logger.LogInformation("InGame room entry publishes the equipped set through a published IEquippedViewPush.");
        var signatureSenders = context.Services.GetAll<IMessageSender<MatchSessionSignature>>()
            .Where(sender => sender.Protocol == options.GameProtocol).ToArray();
        if (signatureSenders.Length != 1)
            throw new ConfigException($"InGame requires exactly one session signature sender for '{options.GameProtocol}'.");
        // The plaza population lives beside the room registry, not inside it: a
        // plaza player has no room id, no phase and no relay key, and folding
        // them into RoomRegistry would list them as rooms and expose them to the
        // start seam. See PlazaRoster.
        var plaza = new PlazaRoster(options.RoomQueueCapacity);
        // Stopped after the handlers and subscriptions that enqueue plaza work are gone.
        context.Effect(() =>
        {
            if (!plaza.Executor.StopAsync().Wait(roomStopTimeout))
                throw new TimeoutException($"InGame plaza executor did not stop within {options.RoomStopTimeoutMs} ms.");
        });
        var partySender = PlaySenders.Optional<PartyUpdated>(context, options.GameProtocol);
        var rooms = new RoomLoop(
            registry, modes, options, options.RelayAdvertiseIpv4, context.Logger, characters, plaza, equippedView, selection, partySender)
        {
            RoomListSender = PlaySenders.Optional<RoomListSnapshot>(context, options.GameProtocol),
        };
        // Random map selections draw from the client's own pools when the client data is loaded.
        Data.MapCatalog? maps = null;
        if (context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var clientData) && clientData is not null)
        {
            maps = Data.MapCatalog.Load(clientData);
            context.Logger.LogInformation("InGame map pools loaded from client data: {Hot} mode(s) with a random pool, {Difficulty} with a difficulty list.",
                maps.HotModeCount, maps.DifficultyModeCount);
        }
        else
        {
            context.Logger.LogWarning("InGame has no client data (clientdata row): random map selections fall back to the shipped keys, whatever the mode.");
        }
        context.Services.TryGet<BubbleFighter.Fantasy.Contracts.Progress.IPlayerProgressRepository>(out var progress);
        if (progress is not null && options.LevelExpThresholds.Count == 0)
            context.Logger.LogWarning("Round rewards are off: level_exp_thresholds is empty.");
        var flows = new InGameFlows(options, registry, context.Logger, characters, equippedView, context.Lifetime, signatureSenders[0], selection, maps, progress, partySender, context.Events);
        var modeNotices = new RoomNotices(PlaySenders.Optional<ServerNotice>(context, options.GameProtocol),
            context.Services.TryGet<IGameText>(out var commandTexts) ? commandTexts : null);
        rooms.ModeNotices = modeNotices;
        rooms.Commands = new GameCommandHandler(registry, async (session, ct) =>
        {
            if (registry.TryGet(session.Id) is { } room)
                await flows.Start.StartMatchAsync(room, ct, session.Id, force: true).ConfigureAwait(false);
        }, modeNotices, (room, mode, ct) => rooms.ApplyModeAsync(room, mode, null, ct));
        context.Effect(context.Events.OnAsync<EquipmentSelectionCommitted>(async (change, ct) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Lifetime);
            await rooms.OnEquipmentSelectionCommittedAsync(change, linked.Token).ConfigureAwait(false);
        }));
        var game = options.GameProtocol;
        var relay = options.RelayProtocol;
        // Where each game-connection request is expected, from the recorded plaza, room and match
        // sessions and a live client (C2S 106, 401, 502 and 511 also arrive outside their first
        // recorded phase). Observation only until the sets are confirmed; see PhaseGate.
        var gate = new PhaseGate(context.Logger);
        const SessionPhase Menu = SessionPhase.MainMenu, Plaza = SessionPhase.Plaza, Room = SessionPhase.Room, Match = SessionPhase.Match;
        PacketHandler In(ushort opcode, PacketHandler handler, params SessionPhase[] phases) => gate.Expect(opcode, handler, phases);

        context.Effect(router.Register(game, InGameOpcodes.C2SUnknown106, In(InGameOpcodes.C2SUnknown106, rooms.Plaza.HandleEnterPlazaAsync, Menu, Plaza)));
        context.Effect(router.Register(game, InGameOpcodes.C2SReturnToMenu, In(InGameOpcodes.C2SReturnToMenu, rooms.Plaza.HandleReturnToMenuAsync, Plaza)));
        // C2S 108 is the single-player training-ground create: the 56-byte rule record only, with mode 40 and
        // the training map synthesized server-side because neither is on the wire.
        context.Effect(router.Register(game, InGameOpcodes.C2STrainingRoom, flows.Setup.HandleTrainingRoomAsync));
        var messages = context.Services.Get<IMessageRouter>().For(context);
        if (rooms.RoomListSender is not null)
            context.Effect(messages.On<RoomListRequested>(game, gate.Expect<RoomListRequested>(rooms.HandleRoomListAsync, Plaza, Room)));
        context.Effect(router.Register(game, InGameOpcodes.C2SUnknown402, In(InGameOpcodes.C2SUnknown402, rooms.HandleUnknown402Async, Plaza)));
        context.Effect(router.Register(game, InGameOpcodes.C2SUnknown403, In(InGameOpcodes.C2SUnknown403, rooms.HandleUnknown403Async, Plaza)));
        context.Effect(router.Register(game, InGameOpcodes.C2SUnknown404, In(InGameOpcodes.C2SUnknown404, rooms.HandleUnknown404Async, Room)));
        context.Effect(router.Register(game, InGameOpcodes.C2SCreateRoom, In(InGameOpcodes.C2SCreateRoom, flows.Setup.HandleCreateRoomAsync, Room)));
        context.Effect(router.Register(game, InGameOpcodes.C2SQuickCreateRoom, flows.Setup.HandleQuickCreateRoomAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SJoinRoom, rooms.HandleJoinRoomAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SUnknown505, In(InGameOpcodes.C2SUnknown505, flows.Setup.HandleRoomStatusAsync, Room)));
        context.Effect(router.Register(game, InGameOpcodes.C2SRoomChat, chatRateLimit.Wrap(In(InGameOpcodes.C2SRoomChat, rooms.HandleRoomChatAsync, Menu, Plaza, Room))));
        context.Effect(router.Register(game, InGameOpcodes.C2SRoomChatTyped, chatRateLimit.Wrap(In(InGameOpcodes.C2SRoomChatTyped, rooms.HandleRoomChatTypedAsync, Room))));
        // C2S 515 is the team-change send the room-stage click dispatcher
        // builds (sub_969730 -> sub_971140 -> sub_4B3AC0). The
        // opcode is shared with two other builders, so the body is logged
        // rather than parsed; registering it keeps the click out of the
        // unhandled log once a client can press the button.
        context.Effect(router.Register(game, InGameOpcodes.C2SUnknown509, In(InGameOpcodes.C2SUnknown509, rooms.HandleSeatCloseAsync, Room)));
        context.Effect(router.Register(game, InGameOpcodes.C2SChangeTeam, In(InGameOpcodes.C2SChangeTeam, rooms.HandleChangeTeamAsync, Room)));
        // C2S 511 is the in-room character position report, not a separate state command, so only the position
        // handler is registered for it; the plaza twin C2S 301 shares the same body.
        context.Effect(router.Register(game, InGameOpcodes.C2SCharacterPositionPlaza, In(InGameOpcodes.C2SCharacterPositionPlaza, rooms.HandleCharacterPositionAsync, Plaza)));
        context.Effect(router.Register(game, InGameOpcodes.C2SCharacterPositionRoom, In(InGameOpcodes.C2SCharacterPositionRoom, rooms.HandleCharacterPositionAsync, Plaza, Room)));
        context.Effect(router.Register(game, InGameOpcodes.C2SGameStartReady, flows.Start.HandleGameStartReadyAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SRelayEndpoint, flows.Start.HandleRelayEndpointAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SRelayConnectResult, flows.Start.HandleRelayConnectResultAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SRelayAck, flows.Start.HandleRelayAckAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SSceneObjectReady, In(InGameOpcodes.C2SSceneObjectReady, flows.Start.HandleSceneObjectReadyAsync, Match)));
        // C2S 579 is the client's "I consumed the stage move" report, sent in the
        // same millisecond as C2S 607 under one gate (sub_755330). Neither
        // was registered, so a chain that waited for a position report or for C2S
        // 571 could stall and read as "the round never starts".
        context.Effect(router.Register(game, InGameOpcodes.C2SStageMoveConsumed, In(InGameOpcodes.C2SStageMoveConsumed, flows.Start.HandleStageMoveConsumedAsync, Match)));
        // C2S 513 is the map-list report. Its body is two prefixed wstrings and
        // an i32 per sub_9D8C00, so it is parsed and logged rather than left in
        // the unhandled log.
        context.Effect(router.Register(game, InGameOpcodes.C2SMapListReport, flows.Setup.HandleMapListReportAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SLeaveGame, In(InGameOpcodes.C2SLeaveGame, flows.Setup.HandleLeaveAsync, Match)));
        context.Effect(router.Register(game, InGameOpcodes.C2SGameOver, In(InGameOpcodes.C2SGameOver, flows.Result.HandleGameOverAsync, Match)));
        context.Effect(router.Register(game, InGameOpcodes.C2SGameResultStats, In(InGameOpcodes.C2SGameResultStats, flows.Result.HandleResultStatsAsync, Match)));
        // The horde (pirate) round: the host's client runs the waves and the server answers or
        // repeats its reports to every seat (HordeWave1256).
        var horde = new HordeWaveFlow(registry, context.Logger);
        context.Effect(router.Register(game, HordeWave1256.WaveStartRequest, horde.HandleWaveStartAsync));
        context.Effect(router.Register(game, HordeWave1256.WaveTableReport, horde.HandleWaveTableAsync));
        context.Effect(router.Register(game, HordeWave1256.WaveEndRequest, horde.HandleWaveEndAsync));
        context.Effect(router.Register(game, HordeWave1256.AllWavesEndRequest, horde.HandleAllWavesEndAsync));
        context.Effect(router.Register(game, HordeWave1256.DropReport, horde.HandleDropAsync));
        context.Effect(router.Register(game, HordeWave1256.PickupReport, horde.HandlePickupAsync));

        // C2S 17 gspGameStartReady rides the RELAY connection, not the game one,
        // once per round. The client builds it with sub_755330 alongside C2S 579 and
        // sends the pair in the same millisecond once the stage move is consumed.
        // Registering it on the game listener only meant the report never arrived,
        // so MarkPlaying never ran and the room sat in Starting - which is exactly
        // the "the round never starts" symptom. The handler resolves the room
        // through the relay seat rather than the game session id.
        context.Effect(router.Register(relay, InGameOpcodes.C2SGameStartReady, flows.Start.HandleGameStartReadyAsync));
        // Publish the room start service so administration requests use the same phase validation and start
        // sequence as the client-driven path.
        context.Effect(context.Services.Register<IRoomStartService>(
            new RoomStartService(registry, flows.Start)));

        context.Effect(context.Events.On<ConnectionClosedEvent>(flows.Setup.OnConnectionClosed));
        // The match connection opens on the GAME listener - the reference's own
        // match connections do, and the emulator's does - with opcode 1 and a relay
        // authentication rather than the 96-byte patch handshake. That opcode is
        // already taken on the game protocol by the handshake, so the pair cannot
        // be registered twice; the router's packet-received event is the seam, and
        // the protocol row deliberately leaves a non-handshake opcode 1 unanswered
        // for exactly this reason.
        // The plaza has no leave packet, so the connection close is the only
        // signal that a player left the plaza; the room detach above and this
        // subscription are independent.
        context.Effect(context.Events.On<ConnectionClosedEvent>(rooms.Plaza.OnConnectionClosed));
        context.Logger.LogInformation(
            "InGame dialect {Version} on {Game} and {Relay}; mode table {Modes}; advertise {Ip}:{Port}.",
            options.VersionMajor, game, relay, modes.HasRows ? modes.Ids.Count : 0,
            options.RelayAdvertiseIpv4, options.RelayAdvertisePort);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The typed-only form of the row: the waiting-room requests the build's dialect row delivers
    /// as typed messages, and nothing else. A request family is served when the dialect publishes
    /// its binding, so a build without one has no such feature.
    ///
    /// </summary>
    private static void ActivateTypedOnly(IPluginContext context, InGameOptions options, RoomRegistry registry, ChatRateLimit chatRateLimit)
    {
        var game = options.GameProtocol;
        // The profile's inventoryview realm supplies this build's stored appearance to every scene.
        context.Services.TryGet<IEquipmentSelection>(out var selection);
        ISeatCharacterResolver? characters = null;
        bool Bound<T>() where T : IMessage =>
            context.Services.GetAll<IInboundMessageBinding<T>>().Any(binding => binding.Protocol == game);
        var messages = context.Services.Get<IMessageRouter>().For(context);
        var gate = new PhaseGate(context.Logger);
        const SessionPhase Menu = SessionPhase.MainMenu, Plaza = SessionPhase.Plaza, Room = SessionPhase.Room;
        if (!Bound<RoomCreateRequested>())
            throw new ConfigException($"InGame typed_only needs a room create binding on '{game}'; the build's dialect row publishes none.");
        if (options.NativeMatchRelay && !Bound<RoundLoadReported>())
            throw new ConfigException("Native match relay requires a map-load binding and shared round handlers.");
        var rooms = new RoomFlow(registry, RoomSenders.Resolve(context, game), context.Logger, selection, characters,
            options.PlazaCapacity, options, context.Lifetime,
            context.Services.TryGet<IGameText>(out var texts) ? texts : null,
            context.Services.TryGet<IPartyDirectory>(out var parties) ? parties : null,
            context.Services.TryGet<IOnlinePlayers>(out var online) ? online : null);
        context.Effect(() =>
        {
            if (!rooms.StopScenesAsync().Wait(TimeSpan.FromMilliseconds(options.RoomStopTimeoutMs)))
                throw new TimeoutException("Pending scene transitions did not stop before unload.");
        });
        if (options.ServerMapDraw)
        {
            // The same pools the packet-wire start draws from, with the same fallback to the shipped keys.
            if (context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var mapData) && mapData is not null)
                rooms.Maps = Data.MapCatalog.Load(mapData);
            else
                context.Logger.LogWarning("InGame server_map_draw has no client data (clientdata row): random selections draw from the shipped keys, whatever the mode.");
        }
        context.Effect(context.Events.OnAsync<BubbleFighter.Fantasy.Contracts.Lobby.PlayerNicknameChanged>(async (change, ct) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Lifetime);
            await rooms.OnNicknameChangedAsync(change, linked.Token).ConfigureAwait(false);
        }));
        context.Effect(context.Events.OnAsync<BubbleFighter.Fantasy.Contracts.Lobby.PlayerColorChanged>(async (change, ct) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Lifetime);
            await rooms.OnColorChangedAsync(change, linked.Token).ConfigureAwait(false);
        }));
        context.Effect(context.Events.OnAsync<EquipmentSelectionCommitted>(async (change, ct) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Lifetime);
            await rooms.OnEquipmentSelectionCommittedAsync(change, linked.Token).ConfigureAwait(false);
        }));
        context.Effect(messages.On<RoomCreateRequested>(game, gate.Expect<RoomCreateRequested>(rooms.HandleCreateAsync, Menu, Plaza)));
        if (Bound<RoomMatchRequested>())
        {
            var matching = new RoomQuickMatcher(registry, rooms, options, PlaySenders.Required<RoomMatchNotified>(context, game),
                context.Logger, context.Lifetime);
            context.Effect(() =>
            {
                if (!matching.StopAsync().Wait(TimeSpan.FromMilliseconds(options.RoomStopTimeoutMs)))
                    throw new TimeoutException("Pending room matching requests did not stop before unload.");
            });
            context.Effect(context.Events.On<ConnectionClosedEvent>(matching.OnConnectionClosed));
            context.Effect(messages.On<RoomMatchRequested>(game, gate.Expect<RoomMatchRequested>(matching.HandleAsync, Menu, Plaza)));
        }
        if (Bound<RoomJoinRequested>())
            context.Effect(messages.On<RoomJoinRequested>(game, gate.Expect<RoomJoinRequested>(rooms.HandleJoinAsync, Menu, Plaza)));
        if (Bound<RoomLeaveRequested>())
            context.Effect(messages.On<RoomLeaveRequested>(game, gate.Expect<RoomLeaveRequested>(rooms.HandleLeaveAsync, Room)));
        if (Bound<PlazaEnterRequested>())
            context.Effect(messages.On<PlazaEnterRequested>(game, gate.Expect<PlazaEnterRequested>(rooms.HandlePlazaEnterAsync, Menu, Plaza)));
        if (Bound<PlazaPositionReported>())
            context.Effect(messages.On<PlazaPositionReported>(game, gate.Expect<PlazaPositionReported>(rooms.HandlePlazaPositionAsync, Plaza)));
        if (Bound<PlazaMenuReturnRequested>())
            context.Effect(messages.On<PlazaMenuReturnRequested>(game, gate.Expect<PlazaMenuReturnRequested>(rooms.HandlePlazaMenuReturnAsync, Plaza, Room)));
        if (Bound<RoomReadyRequested>())
            context.Effect(messages.On<RoomReadyRequested>(game, gate.Expect<RoomReadyRequested>(rooms.HandleReadyAsync, Room)));
        if (Bound<RoomUiRefreshRequested>())
        {
            _ = PlaySenders.Required<RoomUiChanged>(context, game);
            _ = PlaySenders.Required<RoomProbeAcknowledged>(context, game);
            context.Effect(messages.On<RoomUiRefreshRequested>(game, gate.Expect<RoomUiRefreshRequested>(rooms.HandleRoomUiRefreshAsync, Room)));
        }
        if (Bound<RoomTeamChangeRequested>())
            context.Effect(messages.On<RoomTeamChangeRequested>(game, gate.Expect<RoomTeamChangeRequested>(rooms.HandleTeamChangeAsync, Room)));
        if (Bound<RoomSettingsChangeRequested>())
            context.Effect(messages.On<RoomSettingsChangeRequested>(game, gate.Expect<RoomSettingsChangeRequested>(rooms.HandleSettingsChangeAsync, Room)));
        if (Bound<RoomRoundSettingsChangeRequested>())
        {
            _ = PlaySenders.Required<RoomSettingsChanged>(context, game);
            context.Effect(messages.On<RoomRoundSettingsChangeRequested>(game,
                gate.Expect<RoomRoundSettingsChangeRequested>(rooms.HandleRoundSettingsChangeAsync, Room)));
        }
        if (Bound<RoomStartRequested>())
            context.Effect(messages.On<RoomStartRequested>(game, gate.Expect<RoomStartRequested>(rooms.HandleStartAsync, Room)));
        if (Bound<RoomStartUnavailableRequested>())
            context.Effect(messages.On<RoomStartUnavailableRequested>(game, gate.Expect<RoomStartUnavailableRequested>(rooms.HandleStartUnavailableAsync, Room)));
        if (Bound<RoomChatSent>())
        {
            context.Effect(messages.On<RoomChatSent>(game, rooms.HandleChatAsync));
            context.Effect(messages.Use<RoomChatSent>(game, chatRateLimit.Filter<RoomChatSent>));
        }
        if (Bound<RoomSeatLockRequested>())
            context.Effect(messages.On<RoomSeatLockRequested>(game, gate.Expect<RoomSeatLockRequested>(rooms.HandleSeatLockAsync, Room)));
        if (Bound<RoomKickRequested>())
            context.Effect(messages.On<RoomKickRequested>(game, gate.Expect<RoomKickRequested>(rooms.HandleKickAsync, Room)));
        if (Bound<RoomMasterChangeRequested>())
        {
            _ = PlaySenders.Required<RoomMasterChangeReplied>(context, game);
            context.Effect(messages.On<RoomMasterChangeRequested>(game,
                gate.Expect<RoomMasterChangeRequested>(rooms.HandleMasterChangeAsync, Room)));
        }
        if (Bound<RoundLoadReported>())
        {
            if (options.NativeMatchRelay && !Bound<MatchConnectionReported>())
                throw new ConfigException("Native match relay requires a game-side connection-report binding.");
            // The round follows the start: loading barrier, countdown, start, and the gameplay feed.
            // The result surface resolves inside the flow from the same dialect's encoders, so a
            // build without one keeps the plain end path.
            var round = new RoundFlow(registry, options, RoundSenders.Resolve(context, game), context.Logger, context.Lifetime,
                rooms.ReturnToRoomAsync, context);
            context.Services.TryGet<BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore>(out var tutorialStages);
            // Tutorial stage clears are listed by MapMeta stageNum; without client data no clear is recorded.
            var stageTable = context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var tutorialData) && tutorialData is not null
                ? Data.TutorialStageTable.Load(tutorialData) : null;
            Dictionary<string, int>? tutorialGifts = null;
            BubbleFighter.Fantasy.Contracts.Shop.IShopGiftService? giftService = null;
            if (options.TutorialGifts)
            {
                if (stageTable is null || tutorialStages is null
                    || !context.Services.TryGet(out giftService) || giftService is null)
                    throw new ConfigException("tutorial_gifts requires client data, tutorial stage storage and the shop row's gift service.");
                tutorialGifts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (map, gift) in stageTable.Gifts)
                {
                    // The id-400 and id-1809 MapMeta keep kr-812's bubble_bomb gift 1001520, which their workbooks lack.
                    if (giftService.CanGrant(gift))
                        tutorialGifts[map] = gift;
                    else
                        context.Logger.LogWarning("Tutorial stage {Map} names gift {Gift}, which this build's shop workbook cannot grant; its first clear gives nothing.", map, gift);
                }
            }
            var tutorials = new TutorialRoomFlow(registry, stageTable?.Numbers, tutorialStages, context.Logger, tutorialGifts, giftService);
            context.Effect(messages.On<TutorialRoundOpened>(game, tutorials.OpenAsync));
            context.Effect(messages.On<TutorialRoundClosed>(game, tutorials.CloseAsync));
            context.Effect(messages.On<TutorialStageCleared>(game, tutorials.ClearedAsync));
            if (options.CountedItemModeId > 0 && !Bound<RoundCountedItemUseRequested>())
                throw new ConfigException("Counted cooperative items require a native use binding.");
            if (Bound<RoundCountedItemUseRequested>())
                context.Effect(messages.On<RoundCountedItemUseRequested>(game, round.HandleCountedItemUseAsync));
            if (options.InstantRevivalItemId > 0 && !Bound<RoundInstantRevivalRequested>())
                throw new ConfigException("Configured instant revival requires a native request binding.");
            if (Bound<RoundInstantRevivalRequested>())
                context.Effect(messages.On<RoundInstantRevivalRequested>(game, round.HandleInstantRevivalAsync));
            if (Bound<RoundPveRevivalRequested>())
                context.Effect(messages.On<RoundPveRevivalRequested>(game, async (session, request, ct) =>
                {
                    if (!await rooms.TryPveZombieRevivalAsync(session, ct).ConfigureAwait(false))
                        await round.HandlePveRevivalAsync(session, request, ct).ConfigureAwait(false);
                }));
            if (options.PvePointModeId > 0 && !Bound<RoundPvePangReported>())
                throw new ConfigException("Cooperative points require a native score-unit binding.");
            if (Bound<RoundPvePangReported>())
                context.Effect(messages.On<RoundPvePangReported>(game, round.HandlePvePangAsync));
            if (options.RescueModeId > 0 && !Bound<RoundRescueProgressReported>())
                throw new ConfigException("Cooperative rescue missions require a native progress binding.");
            if (Bound<RoundRescueProgressReported>())
                context.Effect(messages.On<RoundRescueProgressReported>(game, round.HandleRescueProgressAsync));
            rooms.SeatLeftDuringRound = round.OnSeatLeftAsync;
            rooms.TeamConvertedDuringRound = round.OnTeamConvertedAsync;
            round.RoundPlaying = rooms.ScheduleZombieKing;
            if (options.RandomSupplyItemKinds.Length > 0 && !Bound<RoundItemUseRequested>())
                throw new ConfigException("Configured random supply kinds require a native item-use binding.");
            // An immediate kind runs its effect through the direct item-effect sender (gspPveItemUsed); a build without
            // that packet (cn-279) cannot offer one.
            if (options.ImmediateSupplyItemKinds.Length > 0 && PlaySenders.Optional<RoundCountedItemUsed>(context, game) is null)
                throw new ConfigException("immediate_supply_item_kinds require the build's direct item-effect sender (gspPveItemUsed).");
            if (Bound<RoundSuppliesReported>())
            {
                _ = PlaySenders.Required<RoundSupplyCreated>(context, game);
                _ = PlaySenders.Required<RoundSupplyPicked>(context, game);
                context.Effect(messages.On<RoundSuppliesReported>(game, round.HandleSuppliesReportedAsync));
                context.Effect(messages.On<RoundSupplyPickupRequested>(game, round.HandleSupplyPickupAsync));
                context.Effect(messages.On<RoundSupplyDropRequested>(game, round.HandleSupplyDropAsync));
            }
            if (Bound<RoundLucciDropRequested>())
            {
                _ = PlaySenders.Required<RoundLucciCreated>(context, game);
                _ = PlaySenders.Required<RoundLucciPicked>(context, game);
                if (!Bound<RoundLucciPickupRequested>())
                    throw new ConfigException("Lucci drops require a native pickup binding.");
                context.Effect(messages.On<RoundLucciDropRequested>(game, round.HandleLucciDropAsync));
                context.Effect(messages.On<RoundLucciPickupRequested>(game, round.HandleLucciPickupAsync));
                if (Bound<RoundHordeAwardRequested>())
                    context.Effect(messages.On<RoundHordeAwardRequested>(game, round.HandleHordeAwardAsync));
            }
            var starModes = options.StarModeIds.Length + options.StarTeamModeIds.Length > 0;
            if (starModes && !Bound<RoundStarDropRequested>())
                throw new ConfigException("Star modes require the native star packets (dialect.rooms star_items).");
            if (Bound<RoundStarDropRequested>())
            {
                _ = PlaySenders.Required<RoundStarCreated>(context, game);
                _ = PlaySenders.Required<RoundStarPicked>(context, game);
                if (!Bound<RoundStarPickupRequested>())
                    throw new ConfigException("Star drops require a native pickup binding.");
                context.Effect(messages.On<RoundStarDropRequested>(game, round.HandleStarDropAsync));
                context.Effect(messages.On<RoundStarPickupRequested>(game, round.HandleStarPickupAsync));
            }
            if (Bound<RoundStarReported>())
                context.Effect(messages.On<RoundStarReported>(game, round.HandleStarReportedAsync));
            if (Bound<RoundItemUseRequested>())
            {
                _ = PlaySenders.Required<RoundItemUsed>(context, game);
                context.Effect(messages.On<RoundItemUseRequested>(game, round.HandleItemUseAsync));
            }
            if (options.NativeItemSlotCount > 1 && !Bound<RoundItemSlotSwitchRequested>())
                throw new ConfigException("Multiple native item slots require a slot-switch binding.");
            if (options.LotteryModes.Length > 0 && !Bound<RoundLotteryRequested>())
                throw new ConfigException("Reward cards require a native selection binding.");
            if (Bound<RoundLotteryRequested>())
                context.Effect(messages.On<RoundLotteryRequested>(game, round.HandleLotteryRequestedAsync));
            if (Bound<RoundItemSlotSwitchRequested>())
                context.Effect(messages.On<RoundItemSlotSwitchRequested>(game, round.HandleItemSlotSwitchAsync));
            if (PlaySenders.Optional<RoundWaveChanged>(context, game) is { } waveSender)
                context.Effect(messages.On<RoundWaveReported>(game,
                    (session, report, ct) => round.HandleWaveReportedAsync(session, report, waveSender, ct)));
            context.Effect(messages.On<RoundLoadReported>(game, round.HandleLoadReportedAsync));
            if (Bound<RoundObjectAddRequested>())
                context.Effect(messages.On<RoundObjectAddRequested>(game, round.HandleObjectAddRequestedAsync));
            if (options.NativeMatchRelay)
                context.Effect(messages.On<MatchConnectionReported>(game, round.HandleMatchConnectionReportedAsync));
            if (Bound<RoundEndRequested>())
                context.Effect(messages.On<RoundEndRequested>(game, round.HandleEndRequestedAsync));
            if (Bound<RoundKillCreditReported>())
                context.Effect(messages.On<RoundKillCreditReported>(game, round.HandleKillCreditReportedAsync));
            if (Bound<RoundFinishingBlowReported>())
                context.Effect(messages.On<RoundFinishingBlowReported>(game, round.HandleFinishingBlowReportedAsync));
            if (Bound<RoundFeedSent>())
                context.Effect(messages.On<RoundFeedSent>(game, round.HandleFeedAsync));
            if (options.NativeLifeModeIds.Length > 0 && !(Bound<RoundLifeReported>() && Bound<RoundResetRequested>()))
                throw new ConfigException("native_life_mode_ids requires a dialect that publishes the life reports and the reset request.");
            if (Bound<RoundLifeReported>())
                context.Effect(messages.On<RoundLifeReported>(game, round.HandleLifeReportedAsync));
            if (Bound<RoundResetRequested>())
            {
                var resetGranted = PlaySenders.Required<RoundResetGranted>(context, game);
                context.Effect(messages.On<RoundResetRequested>(game,
                    (session, request, ct) => round.HandleResetRequestedAsync(session, request, resetGranted, ct)));
            }
            if (Bound<RoundMovementReported>())
            {
                var movementSender = PlaySenders.Required<RoundMovementRelayed>(context, game);
                context.Effect(messages.On<RoundMovementReported>(game,
                    (session, report, ct) => round.HandleMovementReportedAsync(session, report, movementSender, ct)));
            }
            if (Bound<RoundRespawnRequested>())
                context.Effect(messages.On<RoundRespawnRequested>(game, round.HandleRespawnRequestedAsync));
            if (Bound<RoundDeathReported>())
                context.Effect(messages.On<RoundDeathReported>(game, round.HandleDeathReportedAsync));
            if (Bound<RoundTauntReported>())
                context.Effect(messages.On<RoundTauntReported>(game, round.HandleTauntReportedAsync));
            if (Bound<RoundSpawnAccepted>())
                context.Effect(messages.On<RoundSpawnAccepted>(game, round.HandleSpawnAcceptedAsync));
        }
        if (PlaySenders.Optional<RoomListSnapshot>(context, game) is { } listSender)
            context.Effect(messages.On<RoomListRequested>(game, gate.Expect<RoomListRequested>(
                (session, request, ct) => rooms.HandleListAsync(session, request, listSender, ct), Menu, Plaza, Room)));
        context.Effect(context.Events.On<ConnectionClosedEvent>(rooms.OnConnectionClosed));
        context.Effect(context.Events.On<ConnectionClosedEvent>(rooms.OnPlazaConnectionClosed));
        context.Logger.LogInformation("InGame serves typed room and round messages on {Game}.", game);
    }

    /// <summary>
    /// Publishes the room registry as the administration start seam. The match
    /// start is server-driven, so no client packet is invented for it.
    ///
    /// </summary>
    private sealed class RoomStartService(RoomRegistry registry, MatchStartFlow start) : IRoomStartService
    {
        public ValueTask StartAsync(int roomId, CancellationToken ct)
        {
            var room = registry.Find(roomId)
                ?? throw new InvalidOperationException($"Room {roomId} is not live.");
            return start.StartMatchAsync(room, ct);
        }

        public ValueTask<IReadOnlyList<(int RoomId, int Seats, string Phase)>> ListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            List<(int RoomId, int Seats, string Phase)> rows = [];
            foreach (var room in registry.All())
            {
                lock (room.Gate)
                    rows.Add((room.Id, room.Seats.Count, room.Phase.ToString()));
            }

            return ValueTask.FromResult<IReadOnlyList<(int RoomId, int Seats, string Phase)>>(rows);
        }
    }
}
