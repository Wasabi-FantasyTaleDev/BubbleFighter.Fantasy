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
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Administration;
using Microsoft.Extensions.Logging;
namespace BubbleFighter.Fantasy.InGame;

/// <summary>
/// In-session play after login: plaza enter, room create / join / ready, and the round's start
/// and result on the game and relay connections, all through the typed messages the build's
/// dialect row binds. The row registers no packet handlers of its own.
/// This row does not emit packets on the lobby login 3 to 163 chain.
///
/// </summary>
[Plugin("ingame",
    Description = "Plaza enter, room create/join/ready, and combat over the typed room and round messages.",
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter)],
    OptionalMany = [typeof(IMatchCapabilityOfferSender), typeof(IInboundMessageBinding<MatchConnectionReported>), typeof(IMessageSender<RoomTeamConverted>), typeof(IInboundMessageBinding<RoomMatchRequested>), typeof(IMessageSender<RoomMatchNotified>), typeof(IMessageSender<RoomAutoStartNotified>), typeof(IMessageSender<ServerNotice>), typeof(IMessageSender<MatchSessionSignature>),
        typeof(IInboundMessageBinding<RoomListRequested>), typeof(IMessageSender<RoomListSnapshot>), typeof(IMessageSender<PartyUpdated>), typeof(IMessageSender<PartyVisitSucceeded>), typeof(IMessageSender<PartyInvitationMoveSucceeded>), typeof(IMessageSender<PartyVisitFailed>),
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
        typeof(IInboundMessageBinding<RoomChatSent>), typeof(IMessageSender<RoomChatReceived>), typeof(IInboundMessageBinding<RoomSeatLockRequested>), typeof(IInboundMessageBinding<RoomAiAddRequested>), typeof(IInboundMessageBinding<RoomAiRemoveRequested>),
        typeof(IMessageSender<RoomSeatLockChanged>), typeof(IInboundMessageBinding<RoomKickRequested>), typeof(IMessageSender<RoomMemberKicked>),
        typeof(IInboundMessageBinding<PlazaEnterRequested>), typeof(IInboundMessageBinding<PlazaPositionReported>),
        typeof(IInboundMessageBinding<PlazaMenuReturnRequested>),
        typeof(IInboundMessageBinding<InitialTutorialRequested>), typeof(IMessageSender<InitialTutorialEntered>),
        typeof(IInboundMessageBinding<InitialTutorialExitRequested>),
        typeof(IInboundMessageBinding<InitialTutorialNpcReported>), typeof(IMessageSender<InitialTutorialNpcAcknowledged>),
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
    Optional = [typeof(ICommandExecutor), typeof(IGameCommandEndpoints), typeof(IPartyDirectory), typeof(IOnlinePlayers), typeof(BubbleFighter.Fantasy.Contracts.Shop.IShopPurchaseStore), typeof(IInventoryRepository), typeof(IInventoryPushService), typeof(IGameText), typeof(ISeatCharacterResolver), typeof(IEquippedViewPush), typeof(IEquipmentSelection), typeof(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore), typeof(BubbleFighter.Fantasy.Contracts.Progress.IPlayerProgressRepository), typeof(BubbleFighter.Fantasy.Contracts.Wallet.IPlayerWalletRepository), typeof(BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore), typeof(BubbleFighter.Fantasy.Contracts.Shop.IShopGiftService), typeof(IOpcodeTableRegistry)],
    ProvidesMany = [typeof(IMatchCapabilityDirectory), typeof(IRoomAdminDirectory)],
    ConditionalProvides = [])]
public sealed class InGamePlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<InGameOptions>();
        LevelTable.LoadConfiguredFile(options);
        var modes = ModeCatalog.Load(options.ModeTablePath);
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
        context.Effect(context.Services.RegisterMany<IRoomAdminDirectory>(registry));
        var chatRateLimit = new ChatRateLimit(options, context.Logger);
        context.Effect(chatRateLimit);
        context.Effect(context.Events.On<ConnectionClosedEvent>(chatRateLimit.OnConnectionClosed));
        // The room and round logic is typed only: the row registers no packet handlers of its own. The
        // protocol-scoped capability directory above is the match transport's admission seam.
        ActivateTyped(context, options, registry, chatRateLimit, modes);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The row's only form: the waiting-room, plaza and round requests the build's dialect row delivers
    /// as typed messages. A request family is served when the dialect publishes its binding, so a build
    /// without one has no such feature.
    ///
    /// </summary>
    private static void ActivateTyped(IPluginContext context, InGameOptions options, RoomRegistry registry, ChatRateLimit chatRateLimit, ModeCatalog modes)
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
        var roomsBound = Bound<RoomCreateRequested>();
        if (!roomsBound && !Bound<PlazaEnterRequested>())
            throw new ConfigException($"InGame typed_only needs a room create binding or plaza enter binding on '{game}'; the build's dialect row publishes neither.");
        if (roomsBound)
        {
            _ = PlaySenders.Required<RoomEntered>(context, game);
            _ = PlaySenders.Required<RoomSeatUpdated>(context, game);
            _ = PlaySenders.Required<RoomSeatVacated>(context, game);
            _ = PlaySenders.Required<RoomMasterChanged>(context, game);
        }
        if (Bound<PlazaEnterRequested>())
        {
            _ = PlaySenders.Required<PlazaEntered>(context, game);
            _ = PlaySenders.Required<PlazaMemberEntered>(context, game);
            _ = PlaySenders.Required<PlazaMemberLeft>(context, game);
            _ = PlaySenders.Required<PlazaPositionReceived>(context, game);
            _ = PlaySenders.Required<PlazaMenuReturnRequested>(context, game);
        }
        if (options.NativeMatchRelay && !Bound<RoundLoadReported>())
            throw new ConfigException("Native match relay requires a map-load binding and shared round handlers.");
        if (options.AiModeIds.Length > 0 && !(Bound<RoomAiAddRequested>() && Bound<RoomAiRemoveRequested>()))
            throw new ConfigException("ai_mode_ids requires the native AI add and remove bindings on the game protocol.");
        foreach (var key in options.AiPresets.Select(preset => preset.NicknameKey).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal))
            if (!context.Services.TryGet<IGameText>(out var names) || names is null || !names.TryFormat("", key, out _))
                throw new ConfigException($"ai_presets nickname_key '{key}' requires localization with a deployment-default translation.");
        var rooms = new RoomFlow(registry, RoomSenders.Resolve(context, game), context.Logger, selection, characters,
            options.PlazaCapacity, options, context.Lifetime,
            context.Services.TryGet<IGameText>(out var texts) ? texts : null,
            context.Services.TryGet<IPartyDirectory>(out var parties) ? parties : null,
            context.Services.TryGet<IOnlinePlayers>(out var online) ? online : null);
        rooms.Modes = modes;
        rooms.BindCommands(context, game, options.SharedChatCommands);
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
        if (roomsBound)
            context.Effect(messages.On<PartySceneFollowRequested>(game, async (session, request, ct) =>
            {
                var arrived = await rooms.HandlePartySceneFollowAsync(session, request, ct).ConfigureAwait(false);
                var target = online?.FindByUserNum(request.LeaderUserNum);
                var nickname = target?.TryFeature<IPlayerSession>(out var player) == true ? player.Identity.Nickname : "";
                if (!request.AcknowledgeVisit)
                {
                    if (arrived && PlaySenders.Optional<PartyInvitationMoveSucceeded>(context, game) is { } reply)
                        await reply.SendAsync(session, new(nickname), ct).ConfigureAwait(false);
                    return;
                }
                if (arrived)
                    await PlaySenders.Required<PartyVisitSucceeded>(context, game).SendAsync(session, new(nickname), ct).ConfigureAwait(false);
                else
                    await PlaySenders.Required<PartyVisitFailed>(context, game).SendAsync(session, new(PartyVisitFailure.Unavailable, nickname), ct).ConfigureAwait(false);
            }));
        if (roomsBound)
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
            context.Effect(messages.On<PlazaEnterRequested>(game, gate.Expect<PlazaEnterRequested>(rooms.HandlePlazaEnterAsync, Menu, Plaza, SessionPhase.Match)));
        if (Bound<PlazaPositionReported>())
            context.Effect(messages.On<PlazaPositionReported>(game, gate.Expect<PlazaPositionReported>(rooms.HandlePlazaPositionAsync, Plaza)));
        if (Bound<PlazaMenuReturnRequested>())
            context.Effect(messages.On<PlazaMenuReturnRequested>(game, gate.Expect<PlazaMenuReturnRequested>(rooms.HandlePlazaMenuReturnAsync, Plaza, Room, SessionPhase.Match)));
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
        if (Bound<RoomAiAddRequested>())
            context.Effect(messages.On<RoomAiAddRequested>(game, gate.Expect<RoomAiAddRequested>(rooms.HandleAiAddAsync, Room)));
        if (Bound<RoomAiRemoveRequested>())
            context.Effect(messages.On<RoomAiRemoveRequested>(game, gate.Expect<RoomAiRemoveRequested>(rooms.HandleAiRemoveAsync, Room)));
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
            if (Bound<InitialTutorialRequested>())
            {
                var entered = PlaySenders.Required<InitialTutorialEntered>(context, game);
                context.Effect(messages.On<InitialTutorialRequested>(game,
                    gate.Expect<InitialTutorialRequested>((session, request, ct) => rooms.HandleInitialTutorialAsync(session, request, entered, ct), Menu)));
                context.Effect(messages.On<InitialTutorialExitRequested>(game,
                    gate.Expect<InitialTutorialExitRequested>(rooms.HandleInitialTutorialExitAsync, Room, SessionPhase.Match)));
            }
            if (Bound<InitialTutorialNpcReported>())
            {
                var npc = PlaySenders.Required<InitialTutorialNpcAcknowledged>(context, game);
                context.Effect(messages.On<InitialTutorialNpcReported>(game,
                    (session, request, ct) => rooms.HandleInitialTutorialNpcAsync(session, request, npc, ct)));
            }
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
}
