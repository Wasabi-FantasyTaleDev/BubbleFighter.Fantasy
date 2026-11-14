using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The plaza side of the room flow: who is present in the walkable lobby, where they reported
/// themselves, and the enter and leave announcements between them. A player seated in a room is
/// never present: entering a room removes them, and leaving a room or being kicked puts them back.
/// The build codec installs the native scene; this part decides who hears about membership
/// and actual movement reports, and in which order.
/// </summary>
internal sealed partial class RoomFlow
{
    // The plaza presence: one instance per flow, bounded by the row configuration.
    private readonly PlazaPresence _plaza;

    /// <summary>Whether this build's dialect publishes the plaza encoders.</summary>
    private bool PlazaBound => _plazaEntered is not null;

    /// <summary>A client asks to enter the plaza (from the main menu, or again while in it).</summary>
    public ValueTask HandlePlazaEnterAsync(ISession session, PlazaEnterRequested request, CancellationToken ct) =>
        RunSceneAsync(session, async (group, token) =>
        {
            await CloseTutorialForSceneAsync(session, token).ConfigureAwait(false);
            await EnterPlazaSceneAsync(session, group is not null, token).ConfigureAwait(false);
            if (_plaza.Find(session.Id) is not null && _registry.TryGet(session.Id) is null)
                await FollowPlazaAsync(group, token).ConfigureAwait(false);
        }, ct);

    private async ValueTask EnterPlazaSceneAsync(ISession session, bool partyTransition, CancellationToken ct)
    {
        if (!PlazaBound)
            return;
        if (_options?.PlazaEnterRequiresFinishedLogin == true && !SessionPlayer.IsPastLoginBurst(session))
        {
            _logger.LogWarning("Session {Id} asked to enter the plaza before its login burst finished; ignored.", session.Id);
            return;
        }
        if (_registry.TryGet(session.Id) is { } room)
        {
            _logger.LogInformation("Session {Id} asked to enter the plaza while seated in room {Room}; ignored.",
                session.Id, room.Id);
            return;
        }
        await EnterPlazaAsync(session, ct, partyTransition).ConfigureAwait(false);
    }

    /// <summary>Puts a session into the plaza: resolves its seat, announces it, and tells the entrant who is present.</summary>
    internal async ValueTask EnterPlazaAsync(ISession session, CancellationToken ct, bool partyTransition = false)
    {
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        var equipment = await ResolveEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        await _plaza.Executor.ExecuteAsync(
            operationCt => EnterPlazaCoreAsync(session, userNum, nickname, equipment, operationCt, partyTransition), ct).ConfigureAwait(false);
    }

    // Runs on the plaza executor: the entrant hears the whole plaza, every present player hears the entrant.
    private async ValueTask EnterPlazaCoreAsync(ISession session, int userNum, string nickname, SeatEquipment equipment,
        CancellationToken ct, bool partyTransition = false)
    {
        // A player who moved here may still be shown for the node it left; its own seat replaces that one.
        if (_plaza.FindRemote(userNum) is { } shadow)
            await RemoveRemotePlazaMemberAsync(shadow.UserNumber, ct).ConfigureAwait(false);
        // A new scene resets movement counters; peers must discard the previous actor before seeing the same identity again.
        if (_plaza.Find(session.Id) is { Reserved: false })
            await LeavePlazaCoreAsync(session.Id, ct).ConfigureAwait(false);
        var seat = _plaza.Enter(session, userNum, nickname, equipment);
        PreparePlazaRecruitment(seat);
        PlazaMember you;
        PlazaMember[] present;
        int[] memberOrder;
        lock (_plaza.Gate)
        {
            you = seat.ToMember();
            memberOrder = [.. _plaza.Snapshot().Where(other => _registry.TryGet(other.SessionId) is null).Select(other => other.Slot).Order()];
            present = _plaza.Snapshot()
                .Where(other => other.SessionId != seat.SessionId && _registry.TryGet(other.SessionId) is null)
                .Select(other => other.ToMember())
                .ToArray();
        }
        if (_plazaEntered is null || _plazaMemberEntered is null || _plazaPositionReceived is null)
            throw new InvalidOperationException("Plaza enter requested on a build without the plaza encoders.");
        await _plazaEntered.SendAsync(session, new PlazaEntered(you, present)
            { MemberOrder = memberOrder, PartyTransition = partyTransition }, ct).ConfigureAwait(false);
        // Only actual reports can be replayed; native scene construction owns the entry position.
        foreach (var member in _options?.PlazaReplayPositionsOnEnter != false ? present : [])
        {
            var (userNumber, sequence, position) = _plaza.LastPosition(member.UserNumber);
            if (position.IsEmpty) continue;
            await _plazaPositionReceived.SendAsync(session, new PlazaPositionReceived(userNumber, sequence, position, member.EntrySeed), ct)
                .ConfigureAwait(false);
        }
        foreach (var other in _plaza.Sessions())
        {
            if (other.SessionId == seat.SessionId || _registry.TryGet(other.SessionId) is not null)
                continue;
            await NotifyScenePeerAsync(other.Game!, _plazaMemberEntered, new PlazaMemberEntered(you), ct).ConfigureAwait(false);
        }
        ScenePhase.Entered(session, SessionPhase.Plaza, _logger);
        await ReplayPlazaRecruitmentAsync(session, ct).ConfigureAwait(false);
        PublishPlaza(new(PlazaFederationEventKind.Entered, you.UserNumber, you, default));
        _logger.LogInformation("Session {Id} entered the plaza at seat {Slot}; {Count} player(s) present.",
            session.Id, seat.Slot, present.Length);
    }

    /// <summary>Takes a session out of the plaza because it created or joined a room.</summary>
    internal async Task LeavePlazaForRoomAsync(ISession session, CancellationToken ct) =>
        await _plaza.Executor.ExecuteAsync(operationCt => LeavePlazaCoreAsync(session.Id, operationCt), ct).ConfigureAwait(false);

    // Runs on the plaza executor: the members who stay hear the seat vacate.
    private async ValueTask LeavePlazaCoreAsync(long sessionId, CancellationToken ct)
    {
        var left = _plaza.Remove(sessionId);
        if (left is not null) ClearPlazaRecruitment(left);
        if (left is null || left.Reserved)
            return;
        if (!left.Remote) PublishPlaza(new(PlazaFederationEventKind.Left, left.UserNumber, null, default));
        if (_plazaMemberLeft is null)
            throw new InvalidOperationException("Plaza leave on a build without a plaza-leave encoder.");
        var notice = new PlazaMemberLeft(left.ToMember());
        foreach (var other in _plaza.Sessions())
        {
            // A member whose own connection is closing leaves on its own; it must not stop the others hearing.
            if (other.Game!.Connection.IsClosed || _registry.TryGet(other.SessionId) is not null)
                continue;
            try
            {
                await _plazaMemberLeft.SendAsync(other.Game!, notice, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _logger.LogInformation("Plaza seat {Slot} closed while hearing that seat {Left} left.", other.Slot, left.Slot);
            }
        }
        _logger.LogInformation("Session {Id} left plaza seat {Slot}; {Count} player(s) told.", sessionId, left.Slot,
            _plaza.Count);
    }

    /// <summary>
    /// The plaza exit: the members who stay hear the seat vacate, the leaver gets the build's
    /// move-to-menu packet, and the phase follows. The client sends this from its plaza exit
    /// control, gated on its own connection and channel state; the move-to-menu answer alone is
    /// what switches its stage back.
    /// </summary>
    public async ValueTask HandlePlazaMenuReturnAsync(ISession session, PlazaMenuReturnRequested request, CancellationToken ct)
    {
        long? returnGeneration = null;
        await RunSceneAsync(session, async (group, token) =>
        {
            await CloseTutorialForSceneAsync(session, token).ConfigureAwait(false);
            var fromPlaza = _plaza.Find(session.Id) is not null;
            await ReturnMenuSceneAsync(session, request with { PartyTransition = fromPlaza && group is not null }, token).ConfigureAwait(false);
            if (fromPlaza)
                await ForFollowersAsync(group, async (member, followCt) =>
                {
                    if (_registry.TryGet(member.Id) is null && _plaza.Find(member.Id) is not null)
                        await ReturnMenuSceneAsync(member, new() { PartyTransition = true }, followCt).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            returnGeneration = CaptureHomeReturn(session);
        }, ct).ConfigureAwait(false);
        await ReturnHomeAfterSceneAsync(session, returnGeneration, ct).ConfigureAwait(false);
    }

    private async ValueTask ReturnMenuSceneAsync(ISession session, PlazaMenuReturnRequested request, CancellationToken ct)
    {
        if (!PlazaBound)
            return;
        // A session the plaza does not know still gets its answer: the client waits on it.
        var selectionReturn = session.TryFeature<IPlayerSession>(out var player)
            && player.Phase == SessionPhase.Room && _registry.TryGet(session.Id) is null;
        await _plaza.Executor.ExecuteAsync(operationCt => LeavePlazaCoreAsync(session.Id, operationCt), ct).ConfigureAwait(false);
        if (selectionReturn)
        {
            if (_mainMenu is null)
                throw new InvalidOperationException("Selection scene return requires a main-menu session encoder.");
            var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
            var equipment = await ResolveEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
            // cn-324 sub_707520 only switches the stage; it retains the selection scene's
            // session. Reuse the login installer before constructing the menu avatar.
            await _mainMenu.SendAsync(session, new MainMenuRoomOpened(userNum, nickname, equipment.CharacterId,
                equipment.ItemIds ?? [equipment.CharacterId], SessionPlayer.ResolveLevel(session))
            {
                OfferTutorial = false,
                ColorIndex = SessionPlayer.ResolveColorIndex(session),
                ItemAttributes = equipment.ItemAttributes ?? [],
            }, ct).ConfigureAwait(false);
            ScenePhase.Entered(session, SessionPhase.Plaza, _logger);
            ScenePhase.Entered(session, SessionPhase.MainMenu, _logger);
            return;
        }
        // The leaver's client keeps the seats it was shown; vacate them, or the players who stay in
        // the plaza are still drawn in its main menu.
        if (_plazaMemberLeft is not null && _options?.PlazaVacatePeersOnMenuReturn != false)
            foreach (var other in _plaza.Snapshot())
                if (other.SessionId != session.Id && _registry.TryGet(other.SessionId) is null)
                    await _plazaMemberLeft.SendAsync(session, new PlazaMemberLeft(other.ToMember()), ct).ConfigureAwait(false);
        if (_menuReturn is null)
            throw new InvalidOperationException("Plaza menu return requested on a build without the encoder.");
        var (menuUser, menuName) = SessionPlayer.RequireIdentity(session);
        var menuEquipment = await ResolveEquipmentAsync(session, menuUser, ct).ConfigureAwait(false);
        var menuMember = new PlazaMember(0, menuUser, menuName, SessionPlayer.ResolveLevel(session),
            menuEquipment.CharacterId, menuEquipment.ItemIds ?? [menuEquipment.CharacterId])
            { ColorIndex = SessionPlayer.ResolveColorIndex(session), ItemAttributes = menuEquipment.ItemAttributes ?? [] };
        await _menuReturn.SendAsync(session, request with { You = menuMember }, ct).ConfigureAwait(false);
        ScenePhase.Entered(session, SessionPhase.MainMenu, _logger);
    }

    /// <summary>Removes a closed connection's plaza seat and tells the players who stay. Failures are logged.</summary>
    /// <param name="e">The connection-closed event.</param>
    public void OnPlazaConnectionClosed(ConnectionClosedEvent e)
    {
        var member = _plaza.FindByConnection(e.Connection.Id);
        if (member is null)
            return;
        Task posted;
        using (ExecutionContext.SuppressFlow())
            posted = Task.Run(() => _plaza.Executor.ExecuteAsync(
                operationCt => LeavePlazaCoreAsync(member.SessionId, operationCt), CancellationToken.None));
        _ = posted.ContinueWith(completed => _logger.LogError(completed.Exception!.GetBaseException(),
                "Plaza could not remove closed connection {Connection}.", e.Connection.Id),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>A member's position report: stamp the per-entry sequence and broadcast it to everyone present.</summary>
    public async ValueTask HandlePlazaPositionAsync(ISession session, PlazaPositionReported request, CancellationToken ct)
    {
        if (_plazaPositionReceived is null || _plaza.Find(session.Id) is null)
            return;
        await _plaza.Executor.ExecuteAsync(async operationCt =>
        {
            var member = _plaza.Find(session.Id);
            if (member is null || member.Reserved || _registry.TryGet(session.Id) is not null)
                return;
            member.PositionSequence++;
            member.LastPosition = request.Body;
            var received = new PlazaPositionReceived(member.UserNumber, member.PositionSequence, request.Body, member.EntrySeed);
            PublishPlaza(new(PlazaFederationEventKind.Moved, member.UserNumber, null, request.Body.ToArray()));
            foreach (var other in _plaza.Sessions())
            {
                if (_registry.TryGet(other.SessionId) is not null
                    || (other.SessionId == session.Id && _options?.PlazaEchoPositions == false))
                    continue;
                await NotifyScenePeerAsync(other.Game!, _plazaPositionReceived, received, operationCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One player is present in the plaza, with what a seat record and a position broadcast need.
    /// </summary>
    internal sealed partial class PlazaMemberState
    {
        /// <summary>The game session this member rides.</summary>
        internal required long SessionId { get; init; }

        /// <summary>The game-connection session; null for a player present on another node of the cluster.</summary>
        internal required ISession? Game { get; init; }

        /// <summary>Whether the player is present on another node and is shown here through the plaza federation.</summary>
        internal bool Remote => Game is null;

        /// <summary>Holds admission capacity without exposing a player whose scene has not been installed.</summary>
        internal bool Reserved { get; set; }

        /// <summary>The plaza seat held while the member stays.</summary>
        internal int Slot { get; set; }

        /// <summary>The player's identity number.</summary>
        internal int UserNumber { get; set; }

        /// <summary>The display name.</summary>
        internal string Nickname { get; set; } = "";

        /// <summary>The player's level.</summary>
        internal int Level { get; set; }

        internal ushort ColorIndex { get; set; }

        /// <summary>The equipped character item.</summary>
        internal int CharacterItemId { get; set; }

        /// <summary>The worn items.</summary>
        internal IReadOnlyList<int> EquippedItemIds { get; set; } = [];

        internal IReadOnlyList<RoomItemAttribute> ItemAttributes { get; set; } = [];

        /// <summary>The identity of this plaza entry, used to distinguish reentry from a state refresh.</summary>
        internal int EntrySeed { get; set; }

        /// <summary>The per-entry position sequence, incremented once per broadcast.</summary>
        internal int PositionSequence { get; set; }

        /// <summary>The last reported position body, verbatim.</summary>
        internal ReadOnlyMemory<byte> LastPosition { get; set; }

        internal PlazaMember ToMember() => new(Slot, UserNumber, Nickname, Math.Max(1, Level), CharacterItemId, EquippedItemIds, EntrySeed)
            { ColorIndex = ColorIndex, Recruitment = Recruitment, ItemAttributes = ItemAttributes };
    }

    /// <summary>
    /// The bounded set of players in the plaza, serialized by one executor like a room's seat table.
    /// The cap is the client's plaza seat array size; the 37th player is refused instead of dropped.
    /// </summary>
    internal sealed class PlazaPresence
    {
        private readonly List<PlazaMemberState> _members = [];
        private readonly int _capacity;
        private int _entries;

        /// <summary>Creates the presence with its player cap and a bounded executor.</summary>
        /// <param name="capacity">The cap; the client's own plaza array is 36 seats.</param>
        public PlazaPresence(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The plaza capacity must be positive.");
            _capacity = capacity;
            Executor = new BubbleFighter.Fantasy.RoomCore.RoomExecutor(64);
        }

        /// <summary>Gets the lock every mutation and snapshot takes.</summary>
        public object Gate { get; } = new();

        /// <summary>Serializes membership changes and their announcements.</summary>
        public BubbleFighter.Fantasy.RoomCore.RoomExecutor Executor { get; }

        /// <summary>Gets the number of players currently present.</summary>
        public int Count
        {
            get
            {
                lock (Gate)
                    return _members.Count;
            }
        }

        /// <summary>Checks admission while the caller holds the plaza executor.</summary>
        public bool HasSpaceFor(long sessionId)
        {
            lock (Gate)
                return _members.Count < _capacity || _members.Any(member => member.SessionId == sessionId);
        }

        /// <summary>Adds or refreshes the session's seat; a full plaza fails loud.</summary>
        public PlazaMemberState Enter(ISession session, int userNumber, string nickname, SeatEquipment equipment, bool reserved = false)
        {
            lock (Gate)
            {
                // Closed seats remain until the serialized leave broadcasts their removal.
                if (_members.Any(member => member.SessionId != session.Id && member.UserNumber == userNumber))
                    throw new InvalidOperationException($"Player {userNumber} already has an active plaza session.");
                var seat = _members.FirstOrDefault(member => member.SessionId == session.Id);
                if (seat is null)
                {
                    if (_members.Count >= _capacity)
                        throw new InvalidOperationException($"Plaza is full ({_capacity} players).");
                    seat = new PlazaMemberState
                    {
                        SessionId = session.Id,
                        Game = session,
                        Slot = Enumerable.Range(0, _capacity).First(candidate => _members.All(other => other.Slot != candidate)),
                    };
                    _members.Add(seat);
                }
                seat.UserNumber = userNumber;
                seat.Reserved = reserved;
                seat.Nickname = nickname;
                seat.Level = SessionPlayer.ResolveLevel(session);
                seat.ColorIndex = SessionPlayer.ResolveColorIndex(session);
                seat.CharacterItemId = equipment.CharacterId;
                var snapshot = new BubbleFighter.Fantasy.Contracts.Progress.RoundEquipmentSnapshot(
                    equipment.ItemIds ?? [equipment.CharacterId], equipment.ItemAttributes ?? []);
                seat.EquippedItemIds = snapshot.WornItemIds;
                seat.ItemAttributes = snapshot.ItemAttributes;
                seat.PositionSequence = 0;
                // Only equality matters: a later entry invalidates the previous actor and its movement history.
                seat.EntrySeed = unchecked(++_entries);
                seat.LastPosition = ReadOnlyMemory<byte>.Empty;
                return seat;
            }
        }

        /// <summary>Removes and returns the session's member, or null when it was not present.</summary>
        public PlazaMemberState? Remove(long sessionId)
        {
            lock (Gate)
            {
                var member = _members.FirstOrDefault(candidate => candidate.SessionId == sessionId);
                if (member is not null)
                    _members.Remove(member);
                return member;
            }
        }

        /// <summary>Finds the session's member without removing it.</summary>
        public PlazaMemberState? Find(long sessionId)
        {
            lock (Gate)
                return _members.FirstOrDefault(candidate => candidate.SessionId == sessionId);
        }

        /// <summary>Finds the departing member even after its connection is marked closed.</summary>
        public PlazaMemberState? FindByConnection(long connectionId)
        {
            lock (Gate)
            {
                return _members.FirstOrDefault(member => member.Game?.Connection.Id == connectionId);
            }
        }

        /// <summary>Snapshots installed members; admission reservations are never visible to other players.</summary>
        public IReadOnlyList<PlazaMemberState> Snapshot()
        {
            lock (Gate)
                return [.. _members.Where(member => !member.Reserved)];
        }

        /// <summary>The installed members connected to this node, for broadcasts; remote members have no session.</summary>
        public IReadOnlyList<PlazaMemberState> Sessions()
        {
            lock (Gate)
                return [.. _members.Where(member => !member.Reserved && !member.Remote)];
        }

        /// <summary>The members shown here for other nodes.</summary>
        public IReadOnlyList<PlazaMemberState> RemoteMembers()
        {
            lock (Gate)
                return [.. _members.Where(member => member.Remote)];
        }

        /// <summary>
        /// Seats or refreshes a player present on another node. Returns the seat and whether it is new, or null when
        /// the player is connected here or every seat is taken (the client's array holds 36).
        /// </summary>
        public (PlazaMemberState Seat, bool Added)? EnterRemote(PlazaMember member)
        {
            lock (Gate)
            {
                var existing = _members.FirstOrDefault(candidate => candidate.UserNumber == member.UserNumber);
                if (existing is { Remote: false }) return null;
                var added = existing is null;
                if (existing is null)
                {
                    if (_members.Count >= _capacity) return null;
                    existing = new PlazaMemberState
                    {
                        // Negative ids never collide with a connection's session id.
                        SessionId = -(long)member.UserNumber,
                        Game = null,
                        Slot = Enumerable.Range(0, _capacity).First(candidate => _members.All(other => other.Slot != candidate)),
                        UserNumber = member.UserNumber,
                        PositionSequence = 0,
                        EntrySeed = member.EntrySeed,
                    };
                    _members.Add(existing);
                }
                existing.Nickname = member.Nickname;
                existing.Level = member.Level;
                existing.ColorIndex = member.ColorIndex;
                existing.CharacterItemId = member.CharacterItemId;
                var snapshot = new BubbleFighter.Fantasy.Contracts.Progress.RoundEquipmentSnapshot(member.EquippedItemIds, member.ItemAttributes);
                existing.EquippedItemIds = snapshot.WornItemIds;
                existing.ItemAttributes = snapshot.ItemAttributes;
                existing.Recruitment = member.Recruitment;
                return (existing, added);
            }
        }

        /// <summary>Finds a member shown for another node by its account number.</summary>
        public PlazaMemberState? FindRemote(int userNumber)
        {
            lock (Gate)
                return _members.FirstOrDefault(member => member.Remote && member.UserNumber == userNumber);
        }

        /// <summary>A member's last reported position and its sequence, by user number.</summary>
        public (int UserNumber, int Sequence, ReadOnlyMemory<byte> Position) LastPosition(int userNumber)
        {
            lock (Gate)
            {
                var member = _members.FirstOrDefault(candidate => candidate.UserNumber == userNumber);
                return member is null
                    ? (userNumber, 0, ReadOnlyMemory<byte>.Empty)
                    : (member.UserNumber, member.PositionSequence, member.LastPosition);
            }
        }
    }
}
