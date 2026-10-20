using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
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
    private readonly ILogger _logger;
    private readonly IEquipmentSelection? _selection;
    private readonly ISeatCharacterResolver? _characters;
    private readonly IMessageSender<RoomEntered> _entered;
    private readonly IMessageSender<RoomSeatUpdated> _seatUpdated;
    private readonly IMessageSender<RoomSeatVacated> _seatVacated;
    private readonly IMessageSender<RoomMasterChanged> _masterChanged;
    private readonly IMessageSender<RoomMasterChangeReplied>? _masterChangeReply;
    private readonly IMessageSender<RoomJoinRefused>? _joinRefused;
    private readonly IMessageSender<RoomLeft>? _left;
    private readonly IMessageSender<RoomMatchStarting>? _matchStarting;
    private readonly IMessageSender<RoomSettingsChanged>? _settingsChanged;
    private readonly IMessageSender<RoomChatReceived>? _chat;
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
    private readonly IMessageSender<ServerNotice>? _notices;
    private readonly IGameText? _texts;
    private readonly RoomNotices _roomNotices;
    private readonly GameCommandHandler _commands;

    /// <summary>
    /// Gets or sets what runs on the room's executor after a player left a room that is not waiting,
    /// so a starting round can stop waiting for that player.
    /// </summary>
    internal Func<PlayRoom, CancellationToken, ValueTask>? SeatLeftDuringRound { get; set; }

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
    public RoomFlow(RoomRegistry registry, RoomSenders senders, ILogger logger,
        IEquipmentSelection? selection = null, ISeatCharacterResolver? characters = null, int plazaCapacity = 36,
        InGameOptions? options = null, CancellationToken lifetime = default, IGameText? texts = null,
        IPartyDirectory? parties = null, IOnlinePlayers? online = null)
        : this(registry, senders, logger, selection, characters, plazaCapacity, options, lifetime, texts, parties, online,
            static () => Environment.TickCount64,
            static (delay, ct) => new(Task.Delay(delay, ct)))
    {
    }

    internal RoomFlow(RoomRegistry registry, RoomSenders senders, ILogger logger,
        IEquipmentSelection? selection, ISeatCharacterResolver? characters, int plazaCapacity,
        InGameOptions? options, CancellationToken lifetime, IGameText? texts,
        IPartyDirectory? parties, IOnlinePlayers? online,
        Func<long> autoStartNow, Func<TimeSpan, CancellationToken, ValueTask> autoStartDelay)
    {
        _registry = registry;
        _logger = logger;
        _selection = selection;
        _characters = characters;
        _parties = parties;
        _online = online;
        if (parties is not null && online is null)
            throw new ConfigException("Party scene following requires the online player directory.");
        _sceneTransitions = new SceneTransitions(options?.MaxPendingSceneTransitions ?? new InGameOptions().MaxPendingSceneTransitions);
        _entered = senders.Entered;
        _seatUpdated = senders.SeatUpdated;
        _seatVacated = senders.SeatVacated;
        _masterChanged = senders.MasterChanged;
        _masterChangeReply = senders.MasterChangeReply;
        _joinRefused = senders.JoinRefused;
        _left = senders.Left;
        _matchStarting = senders.MatchStarting;
        _settingsChanged = senders.SettingsChanged;
        _chat = senders.Chat;
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
        _notices = senders.Notices;
        _texts = texts;
        _roomNotices = new RoomNotices(_notices, _texts);
        _commands = new GameCommandHandler(registry, StartFromCommandAsync, _roomNotices,
            (room, mode, ct) => ApplyModeAsync(room, mode, null, ct));
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

        var equipment = await SeatCharacter.ResolveEquipmentAsync(session, userNum, _selection, _characters, ct).ConfigureAwait(false);
        if (PlazaBound)
            await LeavePlazaForRoomAsync(session, ct).ConfigureAwait(false);
        // A "#id#" tag in the title forces the mode and is not part of the title the room shows.
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
        }
        _logger.LogInformation("Session {Id} created room {Room} '{Name}' mode={Mode} map={Map} character={Character}.",
            session.Id, room.Id, titleTag.Title, modeId, request.MapKey, seat.CharacterId);
        await room.Executor.ExecuteAsync(operationCt => SendEnteredAsync(session, room, operationCt, RoomEntryReason.Created,
            group is not null), ct).ConfigureAwait(false);
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
        var open = _registry.OpenRooms();
        var rows = new List<RoomListEntry>(open.Count);
        foreach (var room in open)
        {
            lock (room.Gate)
            {
                var master = room.Seats.FirstOrDefault(seat => seat.Slot == room.MasterSlot)?.Nickname ?? string.Empty;
                // Closed seats cannot be joined, so the row counts only the open ones.
                rows.Add(new RoomListEntry(room.Id, room.RoomName, master, room.ModeId, room.Seats.Count,
                    room.MaxSeats - room.ClosedSlots.Count, room.Password.Length > 0, room.BuildSettings)
                    { MatchInProgress = room.Phase != RoomPhase.Waiting });
            }
        }
        return sender.SendAsync(session, new RoomListSnapshot(request.Cursor, rows), ct);
    }

    /// <summary>Seats the player in a listed room, or answers with the reason no seat was given.</summary>
    public ValueTask HandleJoinAsync(ISession session, RoomJoinRequested request, CancellationToken ct) =>
        RunSceneAsync(session, (group, token) => JoinSceneAsync(session, request, group, token), ct);

    private async ValueTask JoinSceneAsync(ISession session, RoomJoinRequested request, SceneGroup? group, CancellationToken ct)
    {
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        if (_registry.TryGet(session.Id) is { } current)
            throw new InvalidOperationException($"Session {session.Id} asked to join room {request.RoomId} while seated in room {current.Id}.");
        if (_registry.FindJoinable(request.RoomId) is not { } target)
        {
            await RefuseAsync(session, request.RoomId, RoomJoinRefusal.NoRoom, ct).ConfigureAwait(false);
            return;
        }

        var equipment = await SeatCharacter.ResolveEquipmentAsync(session, userNum, _selection, _characters, ct).ConfigureAwait(false);
        try
        {
            await target.Executor.ExecuteAsync(
                operationCt => JoinCoreAsync(session, request, target, userNum, nickname, equipment, operationCt,
                    partyTransition: group is not null), ct).ConfigureAwait(false);
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
        bool partyTransition = false)
    {
        RoomJoinRefusal? refusal;
        lock (target.Gate)
        {
            refusal = target.Phase switch
            {
                RoomPhase.Waiting when target.IsFull => RoomJoinRefusal.Full,
                RoomPhase.Waiting when target.Password.Length > 0 && request.Password != target.Password => RoomJoinRefusal.WrongPassword,
                RoomPhase.Waiting => null,
                RoomPhase.Closed => RoomJoinRefusal.NoRoom,
                _ => RoomJoinRefusal.Started,
            };
        }
        if (refusal is { } reason)
        {
            if (notifyRefusal)
                await RefuseAsync(session, target.Id, reason, ct).ConfigureAwait(false);
            return;
        }

        var (room, joined) = _registry.Join(session, target.Id, userNum, nickname, equipment.CharacterId, equipment.ItemIds);
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
        await ArmWhenFullAsync(room, ct).ConfigureAwait(false);
    }

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
                new SeatEquipment(change.CharacterId, change.EquippedItemIds).Apply(seat);
                members = [.. room.Seats];
                update = new RoomSeatUpdated(room.Id, View(seat)) { Room = View(room) };
            }
            foreach (var member in members)
                await _seatUpdated.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
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
                    && _registry.TryGet(member.SessionId) is null).Select(member => member.Game).ToArray();
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
    public ValueTask HandleLeaveAsync(ISession session, RoomLeaveRequested request, CancellationToken ct) =>
        RunSceneAsync(session, async (group, token) =>
        {
            var source = _registry.TryGet(session.Id);
            var canFollow = source is not null && source.Phase == RoomPhase.Waiting;
            await LeaveSceneAsync(session, group is not null && canFollow, token).ConfigureAwait(false);
            if (canFollow && PlazaBound)
                await FollowRoomLeaveAsync(group, source!, token).ConfigureAwait(false);
        }, ct);

    private async ValueTask LeaveSceneAsync(ISession session, bool partyTransition, CancellationToken ct,
        SceneGroup? followingGroup = null, PlayRoom? followingSource = null)
    {
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogInformation("Session {Id} asked to leave a room but is seated in none.", session.Id);
            return;
        }
        RoomMemberView? left = null;
        RoomView? view = null;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            if (followingGroup is not null && (!FollowerIsCurrent(followingGroup, session)
                || !ReferenceEquals(room, followingSource) || room.Phase != RoomPhase.Waiting)) return;
            lock (room.Gate)
                view = View(room);
            left = await LeaveCoreAsync(room, session.Id, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (left is null) return;
        ScenePhase.Entered(session, SessionPhase.Plaza, _logger);
        if (_left is not null && left is not null)
            await _left.SendAsync(session, new RoomLeft(room.Id, left) { Room = view }, ct).ConfigureAwait(false);
        // Restore the plaza entry after leaving the room without waiting for another request.
        if (PlazaBound && left is not null)
            await EnterPlazaAsync(session, ct, partyTransition).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the player to the other team and shows the seat to every member. A ready player keeps
    /// their team. An unassigned seat counts as red, the team every seat of an untouched room shows.
    /// </summary>
    public async ValueTask HandleTeamChangeAsync(ISession session, RoomTeamChangeRequested request, CancellationToken ct)
    {
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
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || seat.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting)
                {
                    _logger.LogWarning("Session {Id} changed the settings of room {Room} without being its master in a waiting room; ignored.",
                        session.Id, room.Id);
                    return;
                }
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
                members = [.. room.Seats];
                changed = new RoomSettingsChanged(View(room));
            }
            foreach (var member in members)
                await sender.SendAsync(member.Game, changed, operationCt).ConfigureAwait(false);
            await ArmWhenFullAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Reads the mode tag of a room title; a tag that names no valid mode is logged and the title stays as typed.
    private (string Title, int? ModeId) ReadModeTag(ISession session, string title)
    {
        var tag = RoomModeTag.Extract(title);
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
        RoomSeat[] members;
        RoomSettingsChanged changed;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Waiting)
                return false;
            room.ModeId = mode;
            if (room.BuildSettings is { } held)
                room.BuildSettings = held with { Mode = mode };
            members = [.. room.Seats.Where(seat => seat.Game.Id != except?.Id)];
            changed = new RoomSettingsChanged(View(room));
        }
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

    private async ValueTask StartRoomCoreAsync(PlayRoom room, ISession session, RoomStartRequested request, CancellationToken ct)
    {
        var sender = _matchStarting
            ?? throw new InvalidOperationException("Room start requested on a build without a match start encoder.");
        RoomSeat[] members;
        RoomMatchStarting starting;
        var humanTeams = new List<RoomTeamConverted>();
        lock (room.Gate)
        {
            var seat = room.FindByGame(session.Id);
            var nativeAutoStart = room.AutoStartArmed && IsAutoStartMode(room.ModeId);
            if (seat is null || seat.Slot != room.MasterSlot || (!room.CanStart && !nativeAutoStart && !request.Force) || room.Phase != RoomPhase.Waiting)
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
            var draw = _options?.ServerMapDraw == true ? DrawRoundMap(room, request) : null;
            if (_options?.ServerMapDraw == true && draw is null)
            {
                _logger.LogWarning("Room {Room} cannot start: no map fits mode {Mode} and selection '{Selection}'.",
                    room.Id, room.ModeId, request.MapSelection);
                return;
            }
            room.MarkStarting();
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
            room.AutoStartCancellation?.Cancel();
            members = [.. room.Seats];
            PrepareRescues(room);
            starting = new RoomMatchStarting(View(room), room.RoundMapKey)
            {
                StageKey = room.RoundStageKey, IsMission = draw?.IsMission == true,
            };
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
    }

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
                    await _chat.SendAsync(other.Game, line, operationCt).ConfigureAwait(false);
                return;
            }
            // The main menu has no audience: the sender hears their own line alone.
            var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
            await _chat.SendAsync(session, new RoomChatReceived(0, 0, nickname, request.Channel, request.Text), operationCt)
                .ConfigureAwait(false);
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
                        await LeaveCoreAsync(room, sessionId, operationCt).ConfigureAwait(false);
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

    // Runs on the room's executor: the vacated seat first, then the new master when the master left.
    private async ValueTask<RoomMemberView?> LeaveCoreAsync(PlayRoom room, long sessionId, CancellationToken ct)
    {
        int slot;
        bool wasMaster;
        RoomMemberView left;
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
        }

        _registry.Detach(sessionId);
        RoomSeat[] remaining;
        int newMaster;
        RoomSeatUpdated? masterRecord = null;
        bool inRound;
        lock (room.Gate)
        {
            remaining = [.. room.Seats];
            if (remaining.Length == 0)
                return left;
            inRound = room.Phase != RoomPhase.Waiting;
            newMaster = room.MasterSlot;
            if (wasMaster)
            {
                // The promoted master never counts as ready.
                var master = room.Seats.First(seat => seat.Slot == newMaster);
                master.Ready = false;
                masterRecord = new RoomSeatUpdated(room.Id, View(master)) { Room = View(room) };
            }
        }

        var vacated = new RoomSeatVacated(room.Id, left);
        var changed = new RoomMasterChanged(room.Id, newMaster);
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
        if (inRound && SeatLeftDuringRound is { } hook)
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

    private async ValueTask SendEnteredAsync(ISession session, PlayRoom room, CancellationToken ct,
        RoomEntryReason reason = RoomEntryReason.Unspecified, bool partyTransition = false)
    {
        RoomEntered entered;
        lock (room.Gate)
        {
            var seat = room.FindByGame(session.Id)
                ?? throw new InvalidOperationException($"Session {session.Id} holds no seat in room {room.Id}.");
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
        room.MasterSlot, room.Password.Length > 0, [.. room.ClosedSlots.Order()], [.. room.Seats.OrderBy(seat => seat.Slot).Select(View)],
        room.BuildSettings) { MemberOrder = [.. room.Seats.Select(seat => seat.Slot)] };

    private static RoomMemberView View(RoomSeat seat) => new(seat.Slot, seat.UserNum, seat.Nickname, Math.Max(1, seat.Level),
        seat.Team, seat.Ready, seat.CharacterId != 0 ? seat.CharacterId : seat.LocalCharacterId,
        seat.EquippedItemIds ?? []) { InShop = seat.InShop, InInventory = seat.InInventory, ColorIndex = seat.ColorIndex };
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
    IMessageSender<RoomEntered> Entered,
    IMessageSender<RoomSeatUpdated> SeatUpdated,
    IMessageSender<RoomSeatVacated> SeatVacated,
    IMessageSender<RoomMasterChanged> MasterChanged,
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
    IMessageSender<RoomMasterChangeReplied>? MasterChangeReply = null)
{
    /// <summary>Resolves the encoders published for <paramref name="protocol"/>; a missing required one is a configuration error.</summary>
    public static RoomSenders Resolve(IPluginContext context, string protocol) => new(
        PlaySenders.Required<RoomEntered>(context, protocol),
        PlaySenders.Required<RoomSeatUpdated>(context, protocol),
        PlaySenders.Required<RoomSeatVacated>(context, protocol),
        PlaySenders.Required<RoomMasterChanged>(context, protocol),
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
        PlaySenders.Optional<RoomMasterChangeReplied>(context, protocol));
}
