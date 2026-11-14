using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Waiting rooms driven by typed messages only: create, join, leave, ready and the seat and
/// master announcements that follow them. It knows no opcode and no layout; the build's dialect
/// row decodes the requests and encodes what is sent. Membership changes run on the room's
/// executor so every announcement is built from the seat table the change produced.
/// </summary>
internal sealed partial class RoomFlow
{
    private readonly RoomRegistry _registry;
    // Session -> rooms its open equipment writes were marked in, oldest first. Bounded by one entry per open write.
    private readonly Dictionary<long, Queue<PlayRoom>> _selectionRooms = [];
    private readonly ILogger _logger;
    private readonly IEquipmentSelection? _selection;
    private readonly IAccountMutationScopes? _mutations;
    private readonly ISeatCharacterResolver? _characters;
    private readonly IMessageSender<RoomEntered>? _enteredSender;
    private readonly IMessageSender<RoomSeatUpdated>? _seatUpdatedSender;
    private readonly IMessageSender<RoomSeatVacated>? _seatVacatedSender;
    private readonly IMessageSender<RoomMasterChanged>? _masterChangedSender;
    private IMessageSender<RoomEntered> _entered => _enteredSender
        ?? throw new InvalidOperationException("Room operations require the RoomEntered encoder.");
    private IMessageSender<RoomSeatUpdated> _seatUpdated => _seatUpdatedSender
        ?? throw new InvalidOperationException("Room operations require the RoomSeatUpdated encoder.");
    private IMessageSender<RoomSeatVacated> _seatVacated => _seatVacatedSender
        ?? throw new InvalidOperationException("Room operations require the RoomSeatVacated encoder.");
    private IMessageSender<RoomMasterChanged> _masterChanged => _masterChangedSender
        ?? throw new InvalidOperationException("Room operations require the RoomMasterChanged encoder.");
    private readonly IMessageSender<RoomMasterChangeReplied>? _masterChangeReply;
    private readonly IMessageSender<RoomJoinRefused>? _joinRefused;
    private readonly IMessageSender<RoomLeft>? _left;
    private readonly IMessageSender<RoomMatchStarting>? _matchStarting;
    private readonly IMessageSender<RoomSettingsChanged>? _settingsChanged;
    private readonly IMessageSender<RoomChatReceived>? _chat;
    private readonly IMessageSender<RoomSessionChatReceived>? _sessionChat;
    private readonly IMessageSender<RoomSeatLockChanged>? _seatLock;
    private readonly IMessageSender<RoomMemberKicked>? _kicked;
    private readonly IMessageSender<PlazaEntered>? _plazaEntered;
    private readonly IMessageSender<PlazaMemberEntered>? _plazaMemberEntered;
    private readonly IMessageSender<PlazaMemberLeft>? _plazaMemberLeft;
    private readonly IMessageSender<PlazaPositionReceived>? _plazaPositionReceived;
    private readonly IMessageSender<PlazaMenuReturnRequested>? _menuReturn;
    private readonly IMessageSender<MainMenuRoomOpened>? _mainMenu;
    private readonly IMessageSender<RoomAutoStartNotified>? _autoStart;
    private readonly IMessageSender<RoomTeamConverted>? _teamConverted;
    private readonly IMessageSender<RoundCountedItemsPrepared>? _countedItemsPrepared;
    private readonly IMessageSender<RoundRescueMissionsPrepared>? _rescuePrepared;
    private readonly IMessageSender<RoomUiChanged>? _uiChanged;
    private readonly IMessageSender<RoomProbeAcknowledged>? _probeAcknowledged;
    private readonly InGameOptions? _options;
    private readonly CancellationToken _lifetime;
    private readonly Func<long> _autoStartNow;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _autoStartDelay;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _zombieKingDelay;
    private readonly IMessageSender<ServerNotice>? _notices;
    private readonly IGameText? _texts;
    private readonly RoomNotices _roomNotices;
    private readonly GameCommandHandler _commands;

    internal void BindCommands(IPluginContext context, string scope, bool enabled) => _commands.BindShared(context, scope, enabled);

    internal void BindSessionState(IPluginContext context)
    {
        context.Effect(context.Services.RegisterMany<IGameSessionStateReader>(
            new GameSessionStateReader(_registry, context.Lifetime, () => PlazaBound && _left is not null)));
        var events = context.Events;
        _registry.SessionSceneChanged = session => events.Emit(new GameSessionStateChanged(session));
        context.Effect(() => _registry.SessionSceneChanged = null);
    }

    internal Func<ISession, CancellationToken, bool>? CommandAdmission { set => _commands.AdmitInput = value; }

    /// <summary>Supplies the live UDP status providers that <c>!udp status</c> reads.</summary>
    internal Func<IReadOnlyList<BubbleFighter.Fantasy.Contracts.Match.IUdpTransportStatus>>? UdpStatus
    {
        set => _commands.UdpStatus = value;
    }

    internal ModeCatalog Modes { get; set; } = new();
    internal IEventBus? AttributeEvents { get; set; }
    internal RoomModeNames? ModeNames { get; set; }
    /// <summary>Gets or sets the deployment room policies consulted at start, entry and appearance changes, or null.</summary>
    internal RoomPolicyHost? PolicyHost { get; set; }
    internal Action<PlayRoom>? ScriptZombiesStarting { get; set; }
    internal Action<PlayRoom, string>? ValidateRoundMap { get; set; }
    internal Func<PlayRoom, int?>? MapSeedPreparing { get; set; }
    internal Func<ISession, ScriptZombieConversion, CancellationToken, ValueTask>? ScriptZombieConversionRequested { get; set; }

    /// <summary>
    /// Gets or sets what runs on the room's executor after a player left a room that is not waiting,
    /// so a starting round can stop waiting for that player.
    /// </summary>
    internal Func<PlayRoom, CancellationToken, ValueTask>? SeatLeftDuringRound { get; set; }

    internal Func<PlayRoom, RoomSeat, CancellationToken, ValueTask>? BeforeSeatLeaves { get; set; }
    internal Action<PlayRoom>? ZombieEscapeStarting { get; set; }

    /// <summary>Builds the flow over the build's room encoders.</summary>
    /// <param name="registry">The room registry.</param>
    /// <param name="senders">The build's encoders for what the flow sends.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="selection">Stored equipment, when the equipment row is mounted.</param>
    /// <param name="characters">Fallback character resolver.</param>
    /// <param name="plazaCapacity">The plaza's player cap; the client's own plaza array is 36 seats.</param>
    /// <param name="options">Optional native automatic-start policy; absent keeps ordinary room behavior.</param>
    /// <param name="lifetime">Cancels scheduled room notifications when the plugin unloads.</param>
    /// <param name="texts">Game-facing localized messages supplied by the localization plugin.</param>
    /// <param name="parties">Current party membership, when the party plugin is mounted.</param>
    /// <param name="online">Connections for this game's party members.</param>
    /// <param name="mutations">Binds equipment normalization to the session whose appearance is resolved.</param>
    public RoomFlow(RoomRegistry registry, RoomSenders senders, ILogger logger,
        IEquipmentSelection? selection = null, ISeatCharacterResolver? characters = null, int plazaCapacity = 36,
        InGameOptions? options = null, CancellationToken lifetime = default, IGameText? texts = null,
        IPartyDirectory? parties = null, IOnlinePlayers? online = null, IAccountMutationScopes? mutations = null)
        : this(registry, senders, logger, selection, characters, plazaCapacity, options, lifetime, texts, parties, online,
            static () => Environment.TickCount64,
            static (delay, ct) => new(Task.Delay(delay, ct)),
            static (delay, ct) => new(Task.Delay(delay, ct)), mutations)
    {
    }

    internal RoomFlow(RoomRegistry registry, RoomSenders senders, ILogger logger,
        IEquipmentSelection? selection, ISeatCharacterResolver? characters, int plazaCapacity,
        InGameOptions? options, CancellationToken lifetime, IGameText? texts,
        IPartyDirectory? parties, IOnlinePlayers? online,
        Func<long> autoStartNow, Func<TimeSpan, CancellationToken, ValueTask> autoStartDelay,
        Func<TimeSpan, CancellationToken, ValueTask> zombieKingDelay, IAccountMutationScopes? mutations = null)
    {
        _registry = registry;
        _logger = logger;
        _selection = selection;
        _mutations = mutations;
        _characters = characters;
        _parties = parties;
        _online = online;
        if (parties is not null && online is null)
            throw new ConfigException("Party scene following requires the online player directory.");
        _sceneTransitions = new SceneTransitions(options?.MaxPendingSceneTransitions ?? new InGameOptions().MaxPendingSceneTransitions);
        _enteredSender = senders.Entered;
        _seatUpdatedSender = senders.SeatUpdated;
        _seatVacatedSender = senders.SeatVacated;
        _masterChangedSender = senders.MasterChanged;
        _masterChangeReply = senders.MasterChangeReply;
        _joinRefused = senders.JoinRefused;
        _left = senders.Left;
        _matchStarting = senders.MatchStarting;
        if (options?.BombRoundSeries == true
            && _matchStarting is not IRoundSeriesEntrySender)
            throw new ConfigException("bomb_round_series requires an audited round-series entry sender.");
        if (options?.BombRoundSeries == true
            && !((IRoundSeriesEntrySender)_matchStarting!).SupportsMode((int)GameModeId.Bomb))
            throw new ConfigException("bomb_round_series is not supported by this round-series entry sender.");
        if (options?.BombRoundSeries == true && ((IRoundSeriesEntrySender)_matchStarting!).RoundsToWin is < 2 or > 3)
            throw new ConfigException("bomb_round_series requires an audited best-of-three or best-of-five format.");
        _settingsChanged = senders.SettingsChanged;
        _chat = senders.Chat;
        _sessionChat = senders.SessionChat;
        _seatLock = senders.SeatLock;
        _kicked = senders.Kicked;
        _plazaEntered = senders.PlazaEntered;
        _plazaMemberEntered = senders.PlazaMemberEntered;
        _plazaMemberLeft = senders.PlazaMemberLeft;
        _plazaPositionReceived = senders.PlazaPositionReceived;
        _menuReturn = senders.MenuReturn;
        _mainMenu = senders.MainMenu;
        _autoStart = senders.AutoStart;
        _teamConverted = senders.TeamConverted;
        _countedItemsPrepared = senders.CountedItemsPrepared;
        _rescuePrepared = senders.RescuePrepared;
        _uiChanged = senders.UiChanged;
        _probeAcknowledged = senders.ProbeAcknowledged;
        if (options?.RescueModeId > 0 && _rescuePrepared is null)
            throw new ConfigException("Cooperative rescue missions require a preparation sender.");
        if (options?.CountedItemModeId > 0 && _countedItemsPrepared is null)
            throw new ConfigException("Counted cooperative items require a preparation sender.");
        if ((options?.ZombieConversionModeId > 0 || options?.ZombiePvpModeIds.Length > 0) && _teamConverted is null)
            throw new ConfigException("Zombie conversion requires a native room team-change sender.");
        _options = options;
        _lifetime = lifetime;
        _autoStartNow = autoStartNow;
        _autoStartDelay = autoStartDelay;
        _zombieKingDelay = zombieKingDelay;
        _notices = senders.Notices;
        _texts = texts;
        _roomNotices = new RoomNotices(_notices, _texts);
        _commands = new GameCommandHandler(registry, StartFromCommandAsync, _roomNotices,
            (room, mode, ct) => ApplyModeAsync(room, mode, null, ct), canStart: _matchStarting is not null,
            udpEnabled: options?.UdpEnabled ?? false)
        {
            LeaveToPlaza = LeaveToPlazaAsync,
            LeavePlayingMatchToPlaza = (session, ct) => LeaveToPlazaAsync(session, ct, null, null, requirePlayingMatch: true),
        };
        if (options is { AutoStartModeIds.Length: > 0 } && (_autoStart is null || _notices is null || _texts is null))
            throw new InvalidOperationException("Automatic room start requires native countdown, notice and localization services.");
        _plaza = new PlazaPresence(plazaCapacity);
    }

    /// <summary>Opens a room, seats the creator and sends them the room; the room is listed once that is sent.</summary>
    public ValueTask HandleCreateAsync(ISession session, RoomCreateRequested request, CancellationToken ct) =>
        RunSceneAsync(session, (group, token) => CreateSceneAsync(session, request, group, token), ct);

    private async ValueTask CreateSceneAsync(ISession session, RoomCreateRequested request, SceneGroup? group, CancellationToken ct)
    {
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        if (_registry.TryGet(session.Id) is { } current)
        {
            // A repeated create from a seated player is answered with the room they already have.
            _logger.LogInformation("Session {Id} asked to create a room while seated in room {Room}; the room is sent again.",
                session.Id, current.Id);
            await current.Executor.ExecuteAsync(async operationCt =>
            {
                int? taggedMode = null;
                lock (current.Gate)
                    if (request.UpdateExistingTitle && current.Phase == RoomPhase.Waiting
                        && current.FindByGame(session.Id)?.Slot == current.MasterSlot)
                    {
                        // The title typed after the create may carry the mode tag too; the requester gets the room
                        // below, so the others are told here.
                        var tag = ReadModeTag(session, request.Name);
                        current.RoomName = tag.Title;
                        taggedMode = tag.ModeId;
                    }
                if (taggedMode is { } mode)
                    await ApplyModeAsync(current, checked((ushort)mode), session, operationCt).ConfigureAwait(false);
                await SendEnteredAsync(session, current, operationCt, RoomEntryReason.Refreshed).ConfigureAwait(false);
                if (taggedMode is { } announced)
                    await _roomNotices.AnnounceModeAsync(current, announced, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return;
        }

        var equipment = await ResolveEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        if (PlazaBound)
            await LeavePlazaForRoomAsync(session, ct).ConfigureAwait(false);
        // A recognized mode tag forces the mode and is not part of the title the room shows.
        var titleTag = ReadModeTag(session, request.Name);
        var modeId = titleTag.ModeId ?? request.ModeId;
        var (room, seat) = _registry.CreateForMode(session, checked((ushort)modeId), request.MapKey,
            titleTag.Title, userNum, nickname, listed: false);
        equipment.Apply(seat);
        lock (room.Gate)
        {
            room.Password = request.Password;
            // The client's own block still holds the mode it chose; the dialect writes the forced one over it.
            room.BuildSettings = titleTag.ModeId is { } forced && request.BuildSettings is { } own
                ? own with { Mode = forced }
                : request.BuildSettings;
            ResetCustomRules(room);
        }
        _logger.LogInformation("Session {Id} created room {Room} '{Name}' mode={Mode} map={Map} character={Character}.",
            session.Id, room.Id, titleTag.Title, modeId, request.MapKey, seat.CharacterId);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            await SendEnteredAsync(session, room, operationCt, RoomEntryReason.Created, group is not null).ConfigureAwait(false);
            if (PolicyHost is { } policies)
                await policies.MemberEnteredAsync(room, session.Id, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        _registry.Publish(room);
        await FollowRoomAsync(group, room, request.Password, ct).ConfigureAwait(false);
        if (titleTag.ModeId is { } tagged)
            await room.Executor.ExecuteAsync(operationCt => _roomNotices.AnnounceModeAsync(room, tagged, operationCt), ct)
                .ConfigureAwait(false);
        await ScheduleAutoStartAsync(room, ct).ConfigureAwait(false);
        await room.Executor.ExecuteAsync(operationCt => ArmWhenFullAsync(room, operationCt), ct).ConfigureAwait(false);
    }

    /// <summary>Answers a list request with every open room.</summary>
    public ValueTask HandleListAsync(ISession session, RoomListRequested request, IMessageSender<RoomListSnapshot> sender,
        CancellationToken ct)
    {
        if (Browser is not null) return ListBrowserAsync(session, request, sender, ct);
        var rows = _registry.SnapshotListedRooms(matchRoomsOnly: false, ct).Select(room => room.Entry).ToArray();
        return sender.SendAsync(session, new RoomListSnapshot(request.Cursor, rows), ct);
    }

    /// <summary>Seats the player in a listed room, or answers with the reason no seat was given.</summary>
    public ValueTask HandleJoinAsync(ISession session, RoomJoinRequested request, CancellationToken ct) =>
        RunSceneAsync(session, (group, token) => Browser is null
            ? JoinSceneAsync(session, request, group, token)
            : JoinBrowserAsync(session, request, group, token), ct);

    private async ValueTask JoinSceneAsync(ISession session, RoomJoinRequested request, SceneGroup? group, CancellationToken ct,
        Guid? expectedInstance = null, PreparedAdmission? admission = null)
    {
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        if (_registry.TryGet(session.Id) is { } current)
            throw new InvalidOperationException($"Session {session.Id} asked to join room {request.RoomId} while seated in room {current.Id}.");
        if (_registry.FindJoinable(request.RoomId) is not { } target
            || expectedInstance is { } instance && target.InstanceId != instance)
        {
            await RefuseAsync(session, request.RoomId, RoomJoinRefusal.NoRoom, ct).ConfigureAwait(false);
            return;
        }

        var equipment = await ResolveEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        try
        {
            await target.Executor.ExecuteAsync(
                operationCt => JoinCoreAsync(session, request, target, userNum, nickname, equipment, operationCt,
                    partyTransition: group is not null, expectedInstance: expectedInstance, admission: admission), ct).ConfigureAwait(false);
            // The plaza has its own executor, so its seat is vacated once the room's callback is over.
            if (PlazaBound && _registry.TryGet(session.Id) is not null)
                await LeavePlazaForRoomAsync(session, ct).ConfigureAwait(false);
            if (ReferenceEquals(_registry.TryGet(session.Id), target))
                await FollowRoomAsync(group, target, request.Password, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The room closed between the lookup and the queue.
            await RefuseAsync(session, request.RoomId, RoomJoinRefusal.NoRoom, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask JoinCoreAsync(ISession session, RoomJoinRequested request, PlayRoom target,
        int userNum, string nickname, SeatEquipment equipment, CancellationToken ct, bool notifyRefusal = true,
        bool partyTransition = false, Guid? expectedInstance = null, PreparedAdmission? admission = null)
    {
        RoomJoinRefusal? refusal;
        (PlayRoom Room, RoomSeat Seat)? joinedRoom = null;
        lock (target.Gate)
        {
            refusal = expectedInstance is { } instance && (target.InstanceId != instance
                || !ReferenceEquals(_registry.Find(target.Id), target) || !target.Listed)
                ? RoomJoinRefusal.NoRoom : admission is not null
                ? IsAdmissionSession(session, admission) ? target.ValidateAdmission(admission.Reservation, userNum) : RoomJoinRefusal.NoRoom
                : target.Phase switch
            {
                RoomPhase.Waiting when target.IsFull => RoomJoinRefusal.Full,
                RoomPhase.Waiting when target.Password.Length > 0 && request.Password != target.Password => RoomJoinRefusal.WrongPassword,
                RoomPhase.Waiting => null,
                RoomPhase.Closed => RoomJoinRefusal.NoRoom,
                _ => RoomJoinRefusal.Started,
            };
            if (refusal is null)
                joinedRoom = _registry.Join(session, target.Id, userNum, nickname, equipment.CharacterId, equipment.ItemIds,
                    admission?.Reservation);
        }
        if (refusal is { } reason)
        {
            if (notifyRefusal)
                await RefuseAsync(session, target.Id, reason, ct).ConfigureAwait(false);
            return;
        }

        var (room, joined) = joinedRoom ?? throw new InvalidOperationException("Accepted room admission did not install a seat.");
        lock (room.Gate)
        {
            equipment.Apply(joined);
            if (room.Phase is RoomPhase.Starting or RoomPhase.Playing)
                room.FreezeRewardEquipment(joined);
        }
        _logger.LogInformation("Session {Id} joined room {Room} slot={Slot} character={Character}.",
            session.Id, room.Id, joined.Slot, joined.CharacterId);
        // The joiner gets the whole room; the members already inside get only the new seat.
        await SendEnteredAsync(session, room, ct, RoomEntryReason.Joined, partyTransition).ConfigureAwait(false);
        RoomSeat[] others;
        RoomSeatUpdated announcement;
        lock (room.Gate)
        {
            others = [.. room.Seats.Where(seat => seat.Slot != joined.Slot)];
            announcement = new RoomSeatUpdated(room.Id, View(joined)) { Room = View(room), Reason = RoomSeatUpdateReason.Joined };
        }
        foreach (var seat in others)
            await NotifyScenePeerAsync(seat.Game, _seatUpdated, announcement, ct).ConfigureAwait(false);
        await PublishRoomUiAsync(room, ct).ConfigureAwait(false);
        if (room.AutoStartArmed && _autoStart is not null)
            await _autoStart.SendAsync(session, new RoomAutoStartNotified(), ct).ConfigureAwait(false);
        if (PolicyHost is { } policies)
            await policies.MemberEnteredAsync(room, session.Id, ct).ConfigureAwait(false);
        await ArmWhenFullAsync(room, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the seat's equipment write as open while its room waits, so a start in between is held instead of capturing
    /// the appearance the write replaces. A write that begins after the start leaves the room as it is.
    /// </summary>
    internal void OnEquipmentSelectionStarting(EquipmentSelectionStarting change)
    {
        if (_registry.TryGet(change.Session.Id) is not { } room) return;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Waiting || room.FindByGame(change.Session.Id) is not { } seat
                || !ReferenceEquals(seat.Game, change.Session))
                return;
            room.PendingSelections[change.Session.Id] = room.PendingSelections.GetValueOrDefault(change.Session.Id) + 1;
        }
        // The settlement finds the room it was marked in even if the player has moved on since.
        lock (_selectionRooms)
        {
            if (!_selectionRooms.TryGetValue(change.Session.Id, out var marked))
                _selectionRooms.Add(change.Session.Id, marked = new Queue<PlayRoom>());
            marked.Enqueue(room);
        }
    }

    /// <summary>Closes the seat's open equipment write and repeats a start it held once no write in the room is open.</summary>
    internal async ValueTask OnEquipmentSelectionSettledAsync(EquipmentSelectionSettled change, CancellationToken ct)
    {
        PlayRoom room;
        lock (_selectionRooms)
        {
            // A write that began outside a waiting room marked nothing and has nothing to settle.
            if (!_selectionRooms.TryGetValue(change.Session.Id, out var marked)) return;
            room = marked.Dequeue();
            if (marked.Count == 0) _selectionRooms.Remove(change.Session.Id);
        }
        bool resume;
        lock (room.Gate)
        {
            if (room.PendingSelections.TryGetValue(change.Session.Id, out var open))
            {
                if (open <= 1) room.PendingSelections.Remove(change.Session.Id);
                else room.PendingSelections[change.Session.Id] = open - 1;
            }
            resume = room.PendingSelections.Count == 0 && room.HeldStart is not null && room.Phase == RoomPhase.Waiting;
        }
        if (resume)
            await ResumeHeldStartIfIdleAsync(room, ct).ConfigureAwait(false);
    }

    // Repeats the held start on the room's executor if nothing changed meanwhile: the same holder is seated, the room
    // still waits and no equipment write reopened. The start itself asks the policies again.
    private ValueTask ResumeHeldStartIfIdleAsync(PlayRoom room, CancellationToken ct) =>
        new(room.Executor.ExecuteAsync(async operationCt =>
        {
            PlayRoom.HeldRoomStart held;
            ISession session;
            lock (room.Gate)
            {
                if (room.HeldStart is not { } current || room.PendingSelections.Count != 0 || room.Phase != RoomPhase.Waiting
                    || room.FindByGame(current.SessionId) is not { } seat)
                    return;
                held = current;
                session = seat.Game;
            }
            await StartRoomCoreAsync(room, session, held.Request, operationCt, held.AllowMemberStart, resumed: true).ConfigureAwait(false);
        }, ct));

    /// <summary>Updates the waiting-room snapshot and its observers from the committed inventory selection.</summary>
    internal async ValueTask OnEquipmentSelectionCommittedAsync(EquipmentSelectionCommitted change, CancellationToken ct)
    {
        if (_registry.TryGet(change.Session.Id) is not { } room)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomSeatUpdated update;
            lock (room.Gate)
            {
                var seat = room.FindByGame(change.Session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, change.Session)
                    || seat.UserNum != change.UserNum || room.Phase != RoomPhase.Waiting)
                    return;
                new SeatEquipment(change.CharacterId, change.EquippedItemIds) { ItemAttributes = change.ItemAttributes }.Apply(seat);
                members = [.. room.Seats];
                update = new RoomSeatUpdated(room.Id, View(seat)) { Room = View(room) };
            }
            foreach (var member in members)
                await _seatUpdated.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
            if (PolicyHost is { } policies)
                await policies.AppearanceChangedAsync(room, change.Session.Id, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask OnNicknameChangedAsync(BubbleFighter.Fantasy.Contracts.Lobby.PlayerNicknameChanged change, CancellationToken ct)
    {
        await _plaza.Executor.ExecuteAsync(async operationCt =>
        {
            PlazaMember? oldMember = null;
            PlazaMember? newMember = null;
            ISession[] observers = [];
            lock (_plaza.Gate)
            {
                var seat = _plaza.Snapshot().FirstOrDefault(member => member.UserNumber == change.UserNum);
                if (seat is null) return;
                oldMember = seat.ToMember();
                seat.Nickname = change.Nickname;
                newMember = seat.ToMember();
                observers = _plaza.Sessions().Where(member => member.SessionId != seat.SessionId
                    && _registry.TryGet(member.SessionId) is null).Select(member => member.Game!).ToArray();
                if (!seat.Remote) PublishPlaza(new(PlazaFederationEventKind.Entered, newMember.UserNumber, newMember, default));
            }
            if (_plazaMemberLeft is null || _plazaMemberEntered is null) return;
            foreach (var observer in observers)
            {
                await _plazaMemberLeft.SendAsync(observer, new(oldMember), operationCt).ConfigureAwait(false);
                await _plazaMemberEntered.SendAsync(observer, new(newMember), operationCt).ConfigureAwait(false);
                if (_plazaPositionReceived is not null)
                {
                    var (userNumber, sequence, position) = _plaza.LastPosition(change.UserNum);
                    await _plazaPositionReceived.SendAsync(observer, new(userNumber, sequence, position, newMember.EntrySeed), operationCt).ConfigureAwait(false);
                }
            }
        }, ct).ConfigureAwait(false);
        foreach (var room in _registry.All())
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] members;
                RoomSeatUpdated update;
                lock (room.Gate)
                {
                    var seat = room.Seats.FirstOrDefault(member => member.UserNum == change.UserNum);
                    if (seat is null) return;
                    seat.Nickname = change.Nickname;
                    members = [.. room.Seats];
                    update = new RoomSeatUpdated(room.Id, View(seat)) { Room = View(room) };
                }
                foreach (var member in members)
                    await _seatUpdated.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Records the player's ready mark and shows it to every member, the player included.</summary>
    public async ValueTask HandleReadyAsync(ISession session, RoomReadyRequested request, CancellationToken ct)
    {
        if ((request.Ready ? 1 : 0) + (request.InShop ? 1 : 0) + (request.InInventory ? 1 : 0) > 1)
            throw new ArgumentException("Ready, shop and inventory states are mutually exclusive.", nameof(request));
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomSeatUpdated update;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || room.Phase != RoomPhase.Waiting)
                    return;
                // The master starts the round directly and has no cancel-ready control.
                seat.Ready = seat.Slot != room.MasterSlot && request.Ready;
                // kr-1256 sub_9785E0 reports all three states; shared projections retain them for every dialect.
                seat.InShop = request.InShop;
                seat.InInventory = request.InInventory;
                members = [.. room.Seats];
                update = new RoomSeatUpdated(room.Id, View(seat)) { Room = View(room) };
            }
            foreach (var member in members)
                await _seatUpdated.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
            await ArmWhenFullAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Takes the player out of their room, tells the members who stay and confirms to the leaver.</summary>
    public async ValueTask HandleLeaveAsync(ISession session, RoomLeaveRequested request, CancellationToken ct)
    {
        long? returnGeneration = null;
        await RunSceneAsync(session, async (group, token) =>
        {
            var source = _registry.TryGet(session.Id);
            var canFollow = source is not null && source.Phase == RoomPhase.Waiting;
            await LeaveSceneAsync(session, group is not null && canFollow, token).ConfigureAwait(false);
            if (canFollow && PlazaBound)
                await FollowRoomLeaveAsync(group, source!, token).ConfigureAwait(false);
            if (source is not null) returnGeneration = CaptureHomeReturn(session);
        }, ct).ConfigureAwait(false);
        await ReturnHomeAfterSceneAsync(session, returnGeneration, ct).ConfigureAwait(false);
    }

    private sealed record RelayDeparture(PlayRoom Room, RoomSeat Seat, ISession Relay, int Generation);

    /// <summary>Leaves through the game scene owner only while the requesting relay still owns its captured seat.</summary>
    internal async ValueTask HandleMatchLeaveAsync(ISession relay, MatchLeaveRequested request, CancellationToken ct)
    {
        if (_registry.TryGet(relay.Id) is not { } room) return;
        RelayDeparture departure;
        lock (room.Gate)
        {
            var seat = room.Seats.FirstOrDefault(candidate => ReferenceEquals(candidate.Relay, relay));
            if (seat is null) return;
            departure = new(room, seat, relay, room.StartGeneration);
            if (!RelayDepartureIsCurrent(departure, seat.Game)) return;
        }
        var game = departure.Seat.Game;
        long? returnGeneration = null;
        await RunSceneAsync(game, async (group, token) =>
        {
            var canFollow = room.Phase == RoomPhase.Waiting;
            if (!await LeaveSceneAsync(game, group is not null && canFollow, token,
                    relayDeparture: departure).ConfigureAwait(false)) return;
            if (canFollow && PlazaBound)
                await FollowRoomLeaveAsync(group, room, token).ConfigureAwait(false);
            returnGeneration = CaptureHomeReturn(game);
        }, ct).ConfigureAwait(false);
        await ReturnHomeAfterSceneAsync(game, returnGeneration, ct).ConfigureAwait(false);
    }

    // Checked again inside the room executor: reconnects and new rounds may overtake a queued scene request.
    private bool RelayDepartureIsCurrent(RelayDeparture departure, ISession game) =>
        !departure.Relay.Connection.IsClosed && !game.Connection.IsClosed
        && departure.Room.Phase != RoomPhase.Closed && departure.Room.StartGeneration == departure.Generation
        && departure.Seat.RelayGeneration == departure.Generation
        && ReferenceEquals(departure.Seat.Relay, departure.Relay)
        && ReferenceEquals(departure.Room.FindByGame(game.Id), departure.Seat)
        && ReferenceEquals(departure.Seat.Game, game)
        && ReferenceEquals(_registry.TryGet(departure.Relay.Id), departure.Room)
        && ReferenceEquals(_registry.TryGet(game.Id), departure.Room);

    // !plaza and a cancelled round: the seat is released as on a voluntary leave, in any phase (a round settles the
    // leaver and an empty room closes), then the player gets the plaza a waiting-room leaver gets.
    private ValueTask<bool> LeaveToPlazaAsync(ISession session, CancellationToken ct) =>
        LeaveToPlazaAsync(session, ct, null, null);

    // A delayed cancellation owns only the room and start that scheduled it.
    internal ValueTask<bool> ReturnCancelledPlayerAsync(ISession session, PlayRoom room, int generation, CancellationToken ct) =>
        LeaveToPlazaAsync(session, ct, room, generation);

    private async ValueTask<bool> LeaveToPlazaAsync(ISession session, CancellationToken ct,
        PlayRoom? cancelledRoom, int? cancelledGeneration, bool requirePlayingMatch = false)
    {
        if (_registry.TryGet(session.Id) is null) return false;
        long? returnGeneration = null;
        await RunSceneAsync(session, async (_, token) =>
        {
            if (_registry.TryGet(session.Id) is not { } current) return;
            if (cancelledRoom is not null && (!ReferenceEquals(current, cancelledRoom)
                || current.StartGeneration != cancelledGeneration || !current.RoundCancelled)) return;
            await LeaveSceneAsync(session, false, token, cancelledSource: cancelledRoom,
                cancelledGeneration: cancelledGeneration, requirePlayingMatch: requirePlayingMatch).ConfigureAwait(false);
            if (_registry.TryGet(session.Id) is null)
                returnGeneration = CaptureHomeReturn(session);
        }, ct).ConfigureAwait(false);
        await ReturnHomeAfterSceneAsync(session, returnGeneration, ct).ConfigureAwait(false);
        return _registry.TryGet(session.Id) is null;
    }

    private async ValueTask<bool> LeaveSceneAsync(ISession session, bool partyTransition, CancellationToken ct,
        SceneGroup? followingGroup = null, PlayRoom? followingSource = null,
        PlayRoom? cancelledSource = null, int? cancelledGeneration = null, bool requirePlayingMatch = false,
        RelayDeparture? relayDeparture = null)
    {
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogInformation("Session {Id} asked to leave a room but is seated in none.", session.Id);
            return false;
        }
        RoomMemberView? left = null;
        RoomView? view = null;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            if (followingGroup is not null && (!FollowerIsCurrent(followingGroup, session)
                || !ReferenceEquals(room, followingSource) || room.Phase != RoomPhase.Waiting)) return;
            lock (room.Gate)
            {
                if (relayDeparture is not null && (!ReferenceEquals(room, relayDeparture.Room)
                    || !RelayDepartureIsCurrent(relayDeparture, session))) return;
                if (requirePlayingMatch && (!room.IsMatchRoom || room.Phase != RoomPhase.Playing || room.RoundCancelled
                    || !ReferenceEquals(room.FindByGame(session.Id)?.Game, session)
                    || !session.TryFeature<IPlayerSession>(out var player) || player.Phase is not (SessionPhase.Room or SessionPhase.Match))) return;
                if (cancelledSource is not null && (!ReferenceEquals(room, cancelledSource)
                    || room.StartGeneration != cancelledGeneration || !room.RoundCancelled
                    || !ReferenceEquals(_registry.TryGet(session.Id), room))) return;
                view = View(room);
            }
            left = await LeaveCoreAsync(room, session.Id, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (left is null) return false;
        ScenePhase.Entered(session, SessionPhase.Plaza, _logger);
        if (_left is not null && left is not null)
            await _left.SendAsync(session, new RoomLeft(room.Id, left) { Room = view }, ct).ConfigureAwait(false);
        // Restore the plaza entry after leaving the room without waiting for another request.
        if (PlazaBound && left is not null)
            await EnterPlazaAsync(session, ct, partyTransition).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Moves the player to the other team and shows the seat to every member. A ready player keeps
    /// their team. An unassigned seat counts as red, the team every seat of an untouched room shows.
    /// </summary>
    public async ValueTask HandleTeamChangeAsync(ISession session, RoomTeamChangeRequested request, CancellationToken ct)
    {
        if (request.ZombieConversion is { } conversion)
        {
            if (ScriptZombieConversionRequested is { } handle)
                await handle(session, conversion, ct).ConfigureAwait(false);
            return;
        }
        var (room, _) = _registry.RequireGame(session);
        if (_options?.ZombieConversionModeId > 0 && room.ModeId == _options.ZombieConversionModeId
            && room.Phase == RoomPhase.Playing)
        {
            await ConvertZombieAsync(room, session, ct).ConfigureAwait(false);
            return;
        }
        if (IsZombiePvp(room) && room.Phase == RoomPhase.Playing)
        {
            await HandleZombiePvpDeathAsync(room, session, ct).ConfigureAwait(false);
            return;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomSeatUpdated update;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || seat.Ready || room.Phase != RoomPhase.Waiting)
                    return;
                seat.Team = seat.Team == 1 ? (byte)2 : (byte)1;
                members = [.. room.Seats];
                update = new RoomSeatUpdated(room.Id, View(seat)) { Room = View(room), Reason = RoomSeatUpdateReason.TeamChanged };
            }
            foreach (var member in members)
                await _seatUpdated.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Applies the master's settings change and shows the room's new settings to every member.</summary>
    public async ValueTask HandleSettingsChangeAsync(ISession session, RoomSettingsChangeRequested request, CancellationToken ct)
    {
        var sender = _settingsChanged
            ?? throw new InvalidOperationException("Room settings change requested on a build without a settings encoder.");
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomSettingsChanged changed;
            RoomMemberView[] removedBots;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || seat.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting)
                {
                    _logger.LogWarning("Session {Id} changed the settings of room {Room} without being its master in a waiting room; ignored.",
                        session.Id, room.Id);
                    return;
                }
                removedBots = _options?.AiModeIds.Contains(request.ModeId) == true ? [] : room.RemoveBots();
                var modeChanged = room.ModeId != request.ModeId;
                var customRulesNeedDefaults = room.BuildSettings is null
                    || request.BuildSettings is { } incoming && incoming.Lineage != room.BuildSettings.Lineage;
                room.ModeId = checked((ushort)request.ModeId);
                if (request.MapKey.Length > 0)
                    room.MapKey = request.MapKey;
                if (request.BuildSettings is { } change)
                {
                    // The change lies over the creator's block when both follow one lineage; a change of
                    // another lineage replaces a block its codec could not combine with. The master's own
                    // choice of mode ends any mode the server had set.
                    room.BuildSettings = room.BuildSettings is { } held && held.Lineage == change.Lineage
                        ? held with { Change = change.Change, Mode = null }
                        : change;
                }
                else if (room.BuildSettings is { Mode: not null } serverSet)
                    room.BuildSettings = serverSet with { Mode = room.ModeId };
                if (modeChanged || customRulesNeedDefaults) ResetCustomRules(room);
                members = [.. room.Seats];
                changed = new RoomSettingsChanged(View(room));
            }
            await NotifyRemovedBotsAsync(room.Id, removedBots, members, operationCt).ConfigureAwait(false);
            foreach (var member in members)
                await sender.SendAsync(member.Game, changed, operationCt).ConfigureAwait(false);
            await ArmWhenFullAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Reads the mode tag of a room title; a tag that names no valid mode is logged and the title stays as typed.
    private (string Title, int? ModeId) ReadModeTag(ISession session, string title)
    {
        var tag = RoomModeTag.Extract(title, ModeNames);
        if (tag.RejectedTag is { } rejected)
            _logger.LogWarning("Session {Id} room title '{Title}' has the mode tag {Tag}, which is not a mode id from {Min} to {Max}; the tag is ignored.",
                session.Id, title, rejected, RoomModeTag.MinModeId, RoomModeTag.MaxModeId);
        return (tag.Title, tag.ModeId);
    }

    // Changes a waiting room's mode on the room's executor and shows it to every member (except the one the caller
    // answers itself): the room's settings change where the build has one, the whole room again where it has not.
    // The caller announces the change with a notice.
    private async ValueTask<bool> ApplyModeAsync(PlayRoom room, ushort mode, ISession? except, CancellationToken ct)
    {
        if (Modes.HasRows && !Modes.Ids.Contains(mode))
            return false;
        RoomSeat[] members;
        RoomSettingsChanged changed;
        RoomMemberView[] removedBots;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Waiting)
                return false;
            removedBots = _options?.AiModeIds.Contains(mode) == true ? [] : room.RemoveBots();
            var modeChanged = room.ModeId != mode;
            room.ModeId = mode;
            if (room.BuildSettings is { } held)
                room.BuildSettings = held with { Mode = mode };
            if (modeChanged) ResetCustomRules(room);
            members = [.. room.Seats.Where(seat => seat.Game.Id != except?.Id)];
            changed = new RoomSettingsChanged(View(room));
        }
        // The caller may answer the requester separately, but every human must see removed AI seats.
        RoomSeat[] allMembers;
        lock (room.Gate) allMembers = [.. room.Seats];
        await NotifyRemovedBotsAsync(room.Id, removedBots, allMembers, ct).ConfigureAwait(false);
        foreach (var member in members)
        {
            if (_settingsChanged is { } sender)
                await sender.SendAsync(member.Game, changed, ct).ConfigureAwait(false);
            else
                await SendEnteredAsync(member.Game, room, ct, RoomEntryReason.Refreshed).ConfigureAwait(false);
        }
        _logger.LogInformation("Room {Room} mode set to {Mode} by the server for {Count} member(s).", room.Id, mode, members.Length);
        await ArmWhenFullAsync(room, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Starts loading the round for every member when the master asks and everyone else is ready.
    /// The room leaves its waiting phase, so joins are refused from here on.
    /// </summary>
    public ValueTask HandleStartAsync(ISession session, RoomStartRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        return new(room.Executor.ExecuteAsync(operationCt => StartRoomCoreAsync(room, session, request, operationCt), ct));
    }

    /// <summary>Records a validated start click on a room-only binding without changing the room or sending packets.</summary>
    public ValueTask HandleStartUnavailableAsync(ISession session, RoomStartUnavailableRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        return new(room.Executor.ExecuteAsync(_ =>
        {
            lock (room.Gate)
            {
                if (room.FindByGame(session.Id) is null || room.Phase != RoomPhase.Waiting || room.StartUnavailableLogged)
                    return ValueTask.CompletedTask;
                room.StartUnavailableLogged = true;
            }
            _logger.LogInformation("Room {Room}: start is unavailable on the room-only binding; the validated request sends no packets and leaves the room waiting.", room.Id);
            return ValueTask.CompletedTask;
        }, ct));
    }

    /// <summary>Repeats a start a room policy held; the caller already runs on the room's executor.</summary>
    internal ValueTask ResumeHeldStartAsync(PlayRoom room, ISession session, RoomStartRequested request, bool allowMemberStart, CancellationToken ct) =>
        StartRoomCoreAsync(room, session, request, ct, allowMemberStart, resumed: true);

    private async ValueTask StartRoomCoreAsync(PlayRoom room, ISession session, RoomStartRequested request, CancellationToken ct,
        bool allowMemberStart = false, bool resumed = false)
    {
        var sender = _matchStarting
            ?? throw new InvalidOperationException("Room start requested on a build without a match start encoder.");
        RoomPolicyHost.HeldStartPolicies? held = null;
        var selectionBusy = false;
        RoomSeat[] members = [];
        RoomMatchStarting? starting = null;
        var humanTeams = new List<RoomTeamConverted>();
        lock (room.Gate)
        {
            var seat = room.FindByGame(session.Id);
            var nativeAutoStart = room.AutoStartArmed && IsAutoStartMode(room.ModeId);
            if (seat is null || (!allowMemberStart && seat.Slot != room.MasterSlot) || (!room.CanStart && !nativeAutoStart && !request.Force) || room.Phase != RoomPhase.Waiting)
            {
                _logger.LogInformation("Session {Id} start of room {Room} refused: master={Master} canStart={CanStart} phase={Phase}.",
                    session.Id, room.Id, seat?.Slot == room.MasterSlot, room.CanStart, room.Phase);
                return;
            }
            if (IsAutoStartMode(room.ModeId)
                && (string.IsNullOrWhiteSpace(request.MapKey) || request.MapKey.StartsWith("Random", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Room {Room}: native automatic start requires the master's resolved map; received '{Map}'.", room.Id, request.MapKey);
                return;
            }
            if (!nativeAutoStart && !request.Force && StartRuleRefusal(room) is { } refusal)
            {
                _logger.LogInformation("Session {Id} start of room {Room} refused by the start rules: {Refusal}.",
                    session.Id, room.Id, refusal);
                return;
            }
            // The draw only picks a map; it changes no room state, so it may run before the policies are asked.
            var draw = _options?.ServerMapDraw == true ? DrawRoundMap(room, request) : null;
            if (_options?.ServerMapDraw == true && draw is null)
            {
                _logger.LogWarning("Room {Room} cannot start: no map fits mode {Mode} and selection '{Selection}'.",
                    room.Id, room.ModeId, request.MapSelection);
                return;
            }
            var selectedMap = draw?.Map ?? (request.MapKey.Length > 0 ? request.MapKey : room.MapKey);
            ValidateRoundMap?.Invoke(room, draw?.Stage is { Length: > 0 } selectedStage ? selectedStage : selectedMap);
            if (_options?.UsesScriptZombieMode(room.ModeId) == true && room.Bots.Count != 0)
                throw new InvalidOperationException("Script zombie selection requires native player-controlled participants.");
            if (ZombieEscapeStarting is not null && room.ModeId == (int)GameModeId.ZombieEscape && room.Bots.Count != 0)
            {
                _logger.LogWarning("Room {Room}: Zombie Escape requires human-controlled participants.", room.Id);
                return;
            }
            // A seat whose equipment write is still open would start with the appearance it is replacing; the room waits
            // and the write's settlement resumes this start.
            selectionBusy = room.PendingSelections.Count != 0;
            // Deployment room policies (a ban list, a pending inventory write) decide last, in the same locked section
            // that leaves waiting: a start they let proceed always starts, and nothing they would hold can slip in
            // between. The native automatic start and a policy's replay are asked too, each flagged as such.
            if (PolicyHost is { Active: true } policies)
                held = policies.EvaluateStart(room, session.Id, nativeAutoStart, resumed, roomHeld: selectionBusy);
            if (held is not null || selectionBusy)
                room.HeldStart = new(session.Id, request, allowMemberStart);
            else
            {
                room.MarkStarting();
                room.HeldStart = null;
                if (_options?.BombRoundSeries == true && room.ModeId == (int)GameModeId.Bomb)
                    room.BeginBombSeries(((IRoundSeriesEntrySender)_matchStarting!).RoundsToWin);
                // kr-812 sub_816300 copies session+132 and session+188 into separate request strings.
                room.RoundMapKey = draw?.Map ?? (request.MapKey.Length > 0 ? request.MapKey : room.MapKey);
                room.RoundStageKey = draw?.Stage ?? "";
                room.RoundTimeLimits = request.TimeLimits;
                if (_options?.AssignSpawnTeamsOnStart == true)
                    room.AssignSpawnTeams();
                if (_options?.ZombieConversionModeId > 0 && room.ModeId == _options.ZombieConversionModeId)
                    foreach (var member in room.Seats)
                    {
                        member.Team = 2;
                        humanTeams.Add(new(member.Slot, 2));
                    }
                // Every player starts the zombie battle human; outside play the client only stores the team
                // (kr-812 sub_776870 -> sub_7FCF50).
                if (IsZombiePvp(room))
                    foreach (var member in room.Seats)
                    {
                        member.Team = ZombiePvpHumanTeam;
                        humanTeams.Add(new(member.Slot, ZombiePvpHumanTeam));
                    }
                ScriptZombiesStarting?.Invoke(room);
                ZombieEscapeStarting?.Invoke(room);
                room.AutoStartCancellation?.Cancel();
                members = [.. room.Seats];
                PrepareRescues(room);
                starting = new RoomMatchStarting(View(room), room.RoundMapKey)
                {
                    StageKey = room.RoundStageKey, IsMission = draw?.IsMission == true,
                    Series = SeriesEntry(room),
                    MapSeed = MapSeedPreparing?.Invoke(room),
                };
            }
        }
        if (starting is null)
        {
            // Every refusal returned inside the lock, so leaving it without a start means a policy or an open equipment
            // write held it.
            if (held is null && !selectionBusy)
                throw new InvalidOperationException("A room start left the room lock without starting or being held.");
            _logger.LogInformation("Session {Id} start of room {Room} held: policy={Policy} equipment write={Busy}.",
                session.Id, room.Id, held is { Holders.Count: > 0 }, selectionBusy);
            if (held is not null)
                await RoomPolicyHost.StartHeldAsync(held, session.Id, ct).ConfigureAwait(false);
            return;
        }
        foreach (var change in humanTeams)
            foreach (var member in members)
                await _teamConverted!.SendAsync(member.Game, change, ct).ConfigureAwait(false);
        foreach (var member in members)
        {
            if (_options?.CountedItemModeId > 0 && room.ModeId == _options.CountedItemModeId)
                await _countedItemsPrepared!.SendAsync(member.Game, new(_options.CountedItemKinds), ct).ConfigureAwait(false);
            if (room.Rescues is { } rescues)
                await _rescuePrepared!.SendAsync(member.Game, new(rescues.Missions), ct).ConfigureAwait(false);
            await sender.SendAsync(member.Game, starting, ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Room {Room} round loading on {Map} for {Count} member(s).", room.Id, starting.MapKey, members.Length);
        ArmLoadTimeout(room);
    }

    // Runs on the existing result sequence's executor step after its cancellation ownership is released.
    internal async ValueTask ContinueSeriesAsync(PlayRoom room, int completedGeneration, CancellationToken ct)
    {
        RoomSeat[] members;
        RoomMatchStarting starting;
        lock (room.Gate)
        {
            if (!room.ContinueBombSeries(completedGeneration)) return;
            members = [.. room.Seats];
            starting = new(View(room), room.RoundMapKey) { StageKey = room.RoundStageKey, Series = SeriesEntry(room) };
        }
        foreach (var member in members)
            await _matchStarting!.SendAsync(member.Game, starting, ct).ConfigureAwait(false);
        ArmLoadTimeout(room);
    }

    private static RoundSeriesEntry? SeriesEntry(PlayRoom room) => room.BombSeries is { } series
        ? new(series.RoundIndex, series.AttackingTeam) : null;

    /// <summary>
    /// Shows a chat line to the speaker's audience: the room when they are seated, the plaza when
    /// they stand there, and themselves alone otherwise (the main menu has no audience).
    /// </summary>
    public async ValueTask HandleChatAsync(ISession session, RoomChatSent request, CancellationToken ct)
    {
        if (await _commands.TryHandleAsync(session, request.Text, ct).ConfigureAwait(false))
            return;
        if (_chat is null)
            return;
        if (_registry.TryGet(session.Id) is { } room)
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] members;
                RoomChatReceived line;
                lock (room.Gate)
                {
                    if (room.FindByGame(session.Id) is not { } seat)
                        return;
                    members = [.. room.Seats];
                    line = new RoomChatReceived(room.Id, seat.Slot, seat.Nickname, request.Channel, request.Text);
                }
                foreach (var member in members)
                    await _chat.SendAsync(member.Game, line, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return;
        }
        if (!PlazaBound)
        {
            // A build whose plaza is not bound still shows the speaker their own line.
            var (_, name) = SessionPlayer.RequireIdentity(session);
            await _chat.SendAsync(session, new RoomChatReceived(0, 0, name, request.Channel, request.Text), ct).ConfigureAwait(false);
            return;
        }
        await _plaza.Executor.ExecuteAsync(async operationCt =>
        {
            var speaker = _plaza.Find(session.Id);
            if (speaker?.Reserved == true) return;
            if (speaker is not null)
            {
                var line = new RoomChatReceived(0, speaker.Slot, speaker.Nickname, request.Channel, request.Text);
                foreach (var other in _plaza.Sessions())
                    await _chat.SendAsync(other.Game!, line, operationCt).ConfigureAwait(false);
                return;
            }
            // The main menu has no audience: the sender hears their own line alone.
            var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
            await _chat.SendAsync(session, new RoomChatReceived(0, 0, nickname, request.Channel, request.Text), operationCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a waiting-room chat line to the room: every member, or only the speaker's team when the speaker chose
    /// team chat. The speaker hears their own line too, because the client draws nothing until this answer arrives
    /// (kr-812 sub_8145C0, cn-717 sub_899D30). A speaker who is not seated has no audience and is not answered.
    /// </summary>
    public async ValueTask HandleSessionChatAsync(ISession session, RoomSessionChatSent request, CancellationToken ct)
    {
        if (await _commands.TryHandleAsync(session, request.Text, ct).ConfigureAwait(false))
            return;
        if (_sessionChat is null || _registry.TryGet(session.Id) is not { } room)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] audience;
            RoomSessionChatReceived line;
            lock (room.Gate)
            {
                if (room.FindByGame(session.Id) is not { } speaker)
                    return;
                audience = request.TeamOnly ? [.. room.Seats.Where(seat => seat.Team == speaker.Team)] : [.. room.Seats];
                line = new RoomSessionChatReceived(room.Id, speaker.Slot, request.TeamOnly, request.Text);
            }
            foreach (var member in audience)
                await _sessionChat.SendAsync(member.Game, line, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Closes or opens an empty seat for the master of a waiting room and shows it to every member.</summary>
    public async ValueTask HandleSeatLockAsync(ISession session, RoomSeatLockRequested request, CancellationToken ct)
    {
        if (_seatLock is null || _registry.TryGet(session.Id) is not { } room)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || seat.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting
                    || request.Slot < 0 || request.Slot >= room.MaxSeats || room.Seats.Any(other => other.Slot == request.Slot)
                    || !room.SetSlotClosed(request.Slot, request.Closed))
                {
                    _logger.LogInformation("Session {Id} seat lock of room {Room} slot {Slot} refused.", session.Id, room.Id, request.Slot);
                    return;
                }
                members = [.. room.Seats];
            }
            var changed = new RoomSeatLockChanged(room.Id, request.Slot, request.Closed);
            foreach (var member in members)
                await _seatLock.SendAsync(member.Game, changed, operationCt).ConfigureAwait(false);
            await ArmWhenFullAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a member for the master of a waiting room: every member is told who was removed, the
    /// seat empties for those who stay, and the removed player is sent back to the lobby.
    /// </summary>
    public async ValueTask HandleKickAsync(ISession session, RoomKickRequested request, CancellationToken ct)
    {
        if (_kicked is null || _registry.TryGet(session.Id) is not { } room)
            return;
        ISession? removed = null;
        RoomMemberView? left = null;
        RoomView? view = null;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomMemberKicked notice;
            lock (room.Gate)
            {
                view = View(room);
                var master = room.FindByGame(session.Id);
                var target = room.Seats.FirstOrDefault(seat => string.Equals(seat.Nickname, request.Nickname, StringComparison.Ordinal));
                if (master is null || master.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting
                    || target is null || target.Slot == master.Slot)
                {
                    _logger.LogInformation("Session {Id} kick of '{Nickname}' from room {Room} refused.", session.Id, request.Nickname, room.Id);
                    return;
                }
                removed = target.Game;
                members = [.. room.Seats];
                notice = new RoomMemberKicked(room.Id, master.Nickname, target.Nickname);
            }
            foreach (var member in members)
                await _kicked.SendAsync(member.Game, notice, operationCt).ConfigureAwait(false);
            left = await LeaveCoreAsync(room, removed.Id, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (removed is null || left is null)
            return;
        ScenePhase.Entered(removed, SessionPhase.Plaza, _logger);
        if (_left is not null)
            await _left.SendAsync(removed, new RoomLeft(room.Id, left) { Room = view }, ct).ConfigureAwait(false);
        // The removed player is back in the plaza, like a leaver.
        if (PlazaBound)
            await EnterPlazaAsync(removed, ct).ConfigureAwait(false);
    }

    /// <summary>Queues the departure of the player on a closed connection without blocking the event bus.</summary>
    public void OnConnectionClosed(ConnectionClosedEvent closed)
    {
        _registry.ObserveConnectionClosed(closed.Connection);
        var connectionId = closed.Connection.Id;
        var room = _registry.FindByConnection(connectionId);
        if (room is null)
        {
            _registry.DetachConnection(connectionId);
            return;
        }

        Task Post()
        {
            try
            {
                return room.Executor.ExecuteAsync(async operationCt =>
                {
                    if (_registry.TryGetSessionOfConnection(connectionId, out var sessionId))
                        await LeaveCoreAsync(room, sessionId, operationCt, disconnected: true).ConfigureAwait(false);
                    _registry.DetachConnection(connectionId);
                });
            }
            catch (ObjectDisposedException)
            {
                _registry.DetachConnection(connectionId);
                return Task.CompletedTask;
            }
        }

        // The close event can be raised inline from a send inside another room's operation; a fresh
        // execution context keeps that from tripping the executor's reentry guard.
        Task posted;
        if (ExecutionContext.IsFlowSuppressed())
            posted = Task.Run(Post);
        else
        {
            using (ExecutionContext.SuppressFlow())
                posted = Task.Run(Post);
        }
        _ = posted.ContinueWith(completed =>
        {
            if (completed.Exception is { } failure)
                _logger.LogError(failure.GetBaseException(),
                    "Room {Room} could not detach closed connection {Connection}; the seat stays until the room closes.",
                    room.Id, connectionId);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Runs on the room's executor: the vacated seat first, then the new master when the master left. A dropped
    // connection (disconnected) during a loading or playing round cancels the round for everyone; a leave plays on.
    private async ValueTask<RoomMemberView?> LeaveCoreAsync(PlayRoom room, long sessionId, CancellationToken ct,
        bool disconnected = false)
    {
        int slot;
        bool wasMaster;
        RoomMemberView left;
        RoomSeat departing;
        lock (room.Gate)
        {
            var seat = room.FindByGame(sessionId);
            if (seat is null)
            {
                _registry.Detach(sessionId);
                return null;
            }
            slot = seat.Slot;
            wasMaster = slot == room.MasterSlot;
            left = View(seat);
            departing = seat;
        }

        if (BeforeSeatLeaves is { } recordDeparture)
            await recordDeparture(room, departing, ct).ConfigureAwait(false);
        _registry.Detach(sessionId);
        RoomSeat[] remaining;
        int newMaster;
        RoomSeatUpdated? masterRecord = null;
        bool inRound;
        bool cancelRound;
        lock (room.Gate)
        {
            remaining = [.. room.Seats];
        }
        if (remaining.Length == 0)
        {
            if (PolicyHost is { } emptied)
                await emptied.MemberLeftAsync(room, departing.Game, ct).ConfigureAwait(false);
            return left;
        }
        lock (room.Gate)
        {
            inRound = room.Phase != RoomPhase.Waiting;
            cancelRound = (disconnected && _options?.CancelRoundOnDisconnect == true
                || wasMaster && (room.ScriptZombies is not null || room.ZombieEscape is not null)
                || room.ScriptZombies?.KingSlot == slot) && TryCancelRoundCore(room);
            newMaster = room.MasterSlot;
            if (wasMaster)
            {
                // The promoted master never counts as ready.
                var master = room.Seats.First(seat => seat.Slot == newMaster);
                master.Ready = false;
                room.TransferBots(master);
                masterRecord = new RoomSeatUpdated(room.Id, View(master)) { Room = View(room) };
            }
        }

        var vacated = new RoomSeatVacated(room.Id, left);
        var changed = new RoomMasterChanged(room.Id, newMaster)
        {
            PreviousAiControllerUserNum = wasMaster && room.Bots.Count > 0 ? left.UserNum : null,
        };
        foreach (var seat in remaining)
        {
            // A member whose own connection is closing has its departure queued behind this one;
            // it must not stop the members who stay from hearing about this seat.
            if (seat.Game.Connection.IsClosed)
                continue;
            try
            {
                await _seatVacated.SendAsync(seat.Game, vacated, ct).ConfigureAwait(false);
                if (masterRecord is null)
                    continue;
                await _masterChanged.SendAsync(seat.Game, changed, ct).ConfigureAwait(false);
                await _seatUpdated.SendAsync(seat.Game, masterRecord, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _logger.LogInformation("Room {Room}: slot {Slot} closed while hearing that slot {Left} left.", room.Id, seat.Slot, slot);
            }
        }
        _logger.LogInformation("Room {Room} slot {Slot} left; {Count} member(s) told{Master}.",
            room.Id, slot, remaining.Length, wasMaster ? $", master moved to slot {newMaster}" : "");
        if (PolicyHost is { } policies)
            await policies.MemberLeftAsync(room, departing.Game, ct).ConfigureAwait(false);
        if (cancelRound)
        {
            ReturnCancelledRound(room, wasMaster && (room.ScriptZombies is not null || room.ZombieEscape is not null)
                ? "the native zombie script master left" : room.ScriptZombies?.KingSlot == slot
                    ? "the ghost zombie king left" : $"slot {slot} disconnected");
            return left;
        }
        bool cancelled;
        lock (room.Gate)
            cancelled = room.RoundCancelled;
        if (inRound && !cancelled && SeatLeftDuringRound is { } hook)
            await hook(room, ct).ConfigureAwait(false);
        return left;
    }

    /// <summary>Sends the whole room to every seated player again, as after a round. Runs on the room's executor.</summary>
    internal async ValueTask ReturnToRoomAsync(PlayRoom room, CancellationToken ct)
    {
        RoomSeat[] members;
        lock (room.Gate)
            members = [.. room.Seats];
        foreach (var member in members)
            await SendEnteredAsync(member.Game, room, ct, RoomEntryReason.RoundReturned).ConfigureAwait(false);
        await ScheduleAutoStartCoreAsync(room, ct).ConfigureAwait(false);
        HoldRematch(room);
        if (PolicyHost is { } policies)
            await policies.WaitingAgainAsync(room, ct).ConfigureAwait(false);
    }

    /// <summary>Answers a seated player's stage probe without treating its wire word as a room to join.</summary>
    public async ValueTask HandleRoomUiRefreshAsync(ISession session, RoomUiRefreshRequested request, CancellationToken ct)
    {
        // The client's room stage keeps probing on a timer, so a probe can arrive just after its seat is gone.
        if (_registry.TryGame(session) is not { } seated)
        {
            _logger.LogInformation("Session {Id} sent a room-stage probe without a seat; ignored.", session.Id);
            return;
        }
        var room = seated.Room;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            bool install;
            bool firstProbe;
            int challenge;
            int slot;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, session) || room.Phase != RoomPhase.Waiting)
                    return;
                firstProbe = !seat.JoinAcked;
                install = firstProbe && _options?.ReinstallRoomOnFirstProbe != false;
                challenge = seat.Key.Unknown0;
                slot = seat.Slot;
            }
            var probe = _probeAcknowledged
                ?? throw new InvalidOperationException("Room UI probe requires a challenge acknowledgement sender.");
            await probe.SendAsync(session, new(challenge) { Slot = slot }, operationCt).ConfigureAwait(false);
            if (install)
                await SendEnteredAsync(session, room, operationCt, RoomEntryReason.ProbeRefresh).ConfigureAwait(false);
            if (firstProbe)
            {
                lock (room.Gate)
                    if (room.FindByGame(session.Id) is { } seat)
                        seat.JoinAcked = true;
            }
            await PublishRoomUiAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask PublishRoomUiAsync(PlayRoom room, CancellationToken ct)
    {
        if (_uiChanged is null)
            return;
        RoomSeat[] members;
        RoomUiChanged update;
        lock (room.Gate)
        {
            members = [.. room.Seats];
            update = new(View(room));
        }
        foreach (var member in members)
            await NotifyScenePeerAsync(member.Game, _uiChanged, update, ct).ConfigureAwait(false);
    }

    private async ValueTask<SeatEquipment> ResolveEquipmentAsync(ISession session, int userNum, CancellationToken ct)
    {
        // Normalization writes inventory; followers need their own authority on the leader's asynchronous flow.
        using var authority = _mutations?.Enter(session);
        return await SeatCharacter.ResolveEquipmentAsync(session, userNum, _selection, _characters, ct,
            AttributeEvents, _options?.GameProtocol).ConfigureAwait(false);
    }

    private async ValueTask SendEnteredAsync(ISession session, PlayRoom room, CancellationToken ct,
        RoomEntryReason reason = RoomEntryReason.Unspecified, bool partyTransition = false)
    {
        RoomEntered entered;
        lock (room.Gate)
        {
            var seat = room.FindByGame(session.Id)
                ?? throw new InvalidOperationException($"Session {session.Id} holds no seat in room {room.Id}.");
            room.PeerInstallationVersion = checked(room.PeerInstallationVersion + 1);
            entered = new RoomEntered(View(room), seat.Slot) { Reason = reason, PartyTransition = partyTransition };
        }
        await _entered.SendAsync(session, entered, ct).ConfigureAwait(false);
        ScenePhase.Entered(session, SessionPhase.Room, _logger);
    }

    private async ValueTask RefuseAsync(ISession session, int roomId, RoomJoinRefusal reason, CancellationToken ct)
    {
        _logger.LogInformation("Session {Id} join of room {Room} refused: {Reason}.", session.Id, roomId, reason);
        if (_joinRefused is null)
            throw new InvalidOperationException($"Room join refused ({reason}) on a build without a join-refusal encoder.");
        await _joinRefused.SendAsync(session, new RoomJoinRefused(roomId, reason), ct).ConfigureAwait(false);
    }

    /// <summary>Gets or sets the client's map pools a server-drawn round map comes from; null draws from the shipped keys.</summary>
    internal MapCatalog? Maps { get; set; }

    // The selection slots of the start request as the client's MapMeta holds them: key at +68 and +92, name at
    // +116 and the master's raw selection at +44 (kr-1256 sub_9D7570 writes them from session+208/+184).
    // Called under the room gate. Null when the selection is empty or no map of the room's mode fits it.
    private (string Map, string Stage, bool IsMission)? DrawRoundMap(PlayRoom room, RoomStartRequested request)
    {
        var selection = room.Setting.Map with
        {
            Unknown44 = request.MapSelection, Unknown68 = request.MapKey, Unknown92 = request.MapKey, Unknown116 = request.MapName,
        };
        if (!MapCatalog.HasSelection(selection))
            return null;
        var map = Maps is { } maps
            ? maps.Resolve(room.ModeId, selection, Random.Shared)
            : MapCatalog.ConcreteSelection(selection) ?? KnownMapKeys.All[Random.Shared.Next(KnownMapKeys.All.Count)];
        if (map is null)
            return null;
        return (map, Maps?.StageKey(map) ?? map, Maps?.IsDifficultyMode(room.ModeId) == true);
    }

    // Callers hold the room gate.
    private static RoomView View(PlayRoom room) => new(room.Id, room.RoomName, room.ModeId, room.MapKey, room.MaxSeats,
        room.MasterSlot, room.Password.Length > 0, [.. room.ClosedSlots.Order()], [.. room.Seats.Select(View).Concat(room.Bots.Select(bot => bot.Appearance)).OrderBy(seat => seat.Slot)],
        room.BuildSettings) { MemberOrder = [.. room.Seats.Select(seat => seat.Slot), .. room.Bots.Select(bot => bot.Appearance.Slot)] };

    private static RoomMemberView View(RoomSeat seat) => new(seat.Slot, seat.UserNum, seat.Nickname, Math.Max(1, seat.Level),
        seat.Team, seat.Ready, seat.CharacterId != 0 ? seat.CharacterId : seat.LocalCharacterId,
        seat.EquippedItemIds ?? []) { InShop = seat.InShop, InInventory = seat.InInventory, ColorIndex = seat.ColorIndex,
            InventorySelection = seat.InventorySelection, ItemAttributes = seat.ItemAttributes };
}

/// <summary>The encoders a build's dialect publishes for the room flow; the first four are needed by every room.</summary>
/// <param name="Entered">Sends the whole room to a player who was just seated.</param>
/// <param name="SeatUpdated">Sends one seat's record to a member.</param>
/// <param name="SeatVacated">Tells a member that a seat emptied.</param>
/// <param name="MasterChanged">Tells a member who the new master is.</param>
/// <param name="JoinRefused">Answers a refused join; null on a build that cannot join rooms.</param>
/// <param name="Left">Confirms a leave to the leaver; null when the build needs no confirmation.</param>
/// <param name="MatchStarting">Tells a member the round is loading; null on a build whose start is not bound.</param>
/// <param name="SettingsChanged">Shows a member the room's new settings; null on a build without a settings change.</param>
/// <param name="Chat">Shows a member a chat line; null on a build whose room chat is not bound.</param>
/// <param name="SessionChat">Shows a member a waiting-room chat line; null on a build without that chat pair.</param>
/// <param name="SeatLock">Shows a member a closed or opened seat; null on a build whose seat lock is not bound.</param>
/// <param name="Kicked">Tells a member who was removed; null on a build whose kick is not bound.</param>
/// <param name="PlazaEntered">Sends an entrant the whole plaza; null on a build without the plaza encoders.</param>
/// <param name="PlazaMemberEntered">Shows a present player the player who just entered.</param>
/// <param name="PlazaMemberLeft">Shows a present player the player who just left.</param>
/// <param name="PlazaPositionReceived">Shows a present player a member's reported position.</param>
/// <param name="MenuReturn">Answers the plaza exit with the build's move-to-menu packet; null when the build's table has no such packet.</param>
/// <param name="AutoStart">Arms a native automatic-start countdown, when the build supports it.</param>
/// <param name="Notices">Displays a localized command or countdown message through the native announcement path.</param>
/// <param name="TeamConverted">Broadcasts native in-round allegiance changes when the build supports them.</param>
/// <param name="RescuePrepared">Installs cooperative rescue definitions before native map initialization.</param>
/// <param name="CountedItemsPrepared">Installs cooperative item slots before the client creates its game UI.</param>
/// <param name="MainMenu">Restores the local session after leaving a standalone selection scene.</param>
/// <param name="UiChanged">Refreshes the waiting-room member UI after join or a stage probe.</param>
/// <param name="ProbeAcknowledged">Answers a seated member's native room-stage probe.</param>
/// <param name="MasterChangeReply">Acknowledges an explicit ownership delegation request.</param>
internal sealed record RoomSenders(
    IMessageSender<RoomEntered>? Entered,
    IMessageSender<RoomSeatUpdated>? SeatUpdated,
    IMessageSender<RoomSeatVacated>? SeatVacated,
    IMessageSender<RoomMasterChanged>? MasterChanged,
    IMessageSender<RoomJoinRefused>? JoinRefused = null,
    IMessageSender<RoomLeft>? Left = null,
    IMessageSender<RoomMatchStarting>? MatchStarting = null,
    IMessageSender<RoomSettingsChanged>? SettingsChanged = null,
    IMessageSender<RoomChatReceived>? Chat = null,
    IMessageSender<RoomSeatLockChanged>? SeatLock = null,
    IMessageSender<RoomMemberKicked>? Kicked = null,
    IMessageSender<PlazaEntered>? PlazaEntered = null,
    IMessageSender<PlazaMemberEntered>? PlazaMemberEntered = null,
    IMessageSender<PlazaMemberLeft>? PlazaMemberLeft = null,
    IMessageSender<PlazaPositionReceived>? PlazaPositionReceived = null,
    IMessageSender<PlazaMenuReturnRequested>? MenuReturn = null,
    IMessageSender<RoomAutoStartNotified>? AutoStart = null,
    IMessageSender<ServerNotice>? Notices = null,
    IMessageSender<RoomTeamConverted>? TeamConverted = null,
    IMessageSender<RoundCountedItemsPrepared>? CountedItemsPrepared = null,
    IMessageSender<RoundRescueMissionsPrepared>? RescuePrepared = null,
    IMessageSender<MainMenuRoomOpened>? MainMenu = null,
    IMessageSender<RoomUiChanged>? UiChanged = null,
    IMessageSender<RoomProbeAcknowledged>? ProbeAcknowledged = null,
    IMessageSender<RoomMasterChangeReplied>? MasterChangeReply = null,
    IMessageSender<RoomSessionChatReceived>? SessionChat = null)
{
    /// <summary>Resolves the encoders published for <paramref name="protocol"/>; a missing required one is a configuration error.</summary>
    public static RoomSenders Resolve(IPluginContext context, string protocol) => new(
        PlaySenders.Optional<RoomEntered>(context, protocol),
        PlaySenders.Optional<RoomSeatUpdated>(context, protocol),
        PlaySenders.Optional<RoomSeatVacated>(context, protocol),
        PlaySenders.Optional<RoomMasterChanged>(context, protocol),
        PlaySenders.Optional<RoomJoinRefused>(context, protocol),
        PlaySenders.Optional<RoomLeft>(context, protocol),
        PlaySenders.Optional<RoomMatchStarting>(context, protocol),
        PlaySenders.Optional<RoomSettingsChanged>(context, protocol),
        PlaySenders.Optional<RoomChatReceived>(context, protocol),
        PlaySenders.Optional<RoomSeatLockChanged>(context, protocol),
        PlaySenders.Optional<RoomMemberKicked>(context, protocol),
        PlaySenders.Optional<PlazaEntered>(context, protocol),
        PlaySenders.Optional<PlazaMemberEntered>(context, protocol),
        PlaySenders.Optional<PlazaMemberLeft>(context, protocol),
        PlaySenders.Optional<PlazaPositionReceived>(context, protocol),
        PlaySenders.Optional<PlazaMenuReturnRequested>(context, protocol),
        PlaySenders.Optional<RoomAutoStartNotified>(context, protocol),
        PlaySenders.Optional<ServerNotice>(context, protocol),
        PlaySenders.Optional<RoomTeamConverted>(context, protocol),
        PlaySenders.Optional<RoundCountedItemsPrepared>(context, protocol),
        PlaySenders.Optional<RoundRescueMissionsPrepared>(context, protocol),
        PlaySenders.Optional<MainMenuRoomOpened>(context, protocol),
        PlaySenders.Optional<RoomUiChanged>(context, protocol),
        PlaySenders.Optional<RoomProbeAcknowledged>(context, protocol),
        PlaySenders.Optional<RoomMasterChangeReplied>(context, protocol),
        PlaySenders.Optional<RoomSessionChatReceived>(context, protocol));
}
