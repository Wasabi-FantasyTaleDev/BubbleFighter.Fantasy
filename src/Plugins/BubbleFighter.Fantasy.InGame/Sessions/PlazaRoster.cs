using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// One player standing in the plaza (stage 3). The plaza has no room id, no
/// create-time rule record and no phase machine, so a seat keeps only what the
/// S2C 117 kind-3 snapshot needs: the identity pair the <c>players</c> vector
/// carries, the resolved equipped character id the <c>map564</c> entry is keyed
/// and valued with, and the session the snapshot is sent to.
///
/// </summary>
internal sealed class PlazaSeat
{
    /// <summary>Creates a plaza seat for a game session.</summary> <param name="sessionId">Game session id; the
    /// roster key.</param> <param name="game">The game-protocol session the snapshot is sent to.</param> <param
    /// name="userNum">Identity written into <c>RoomPlayerInfo</c> +8 and used as the profile-map key.</param>
    /// <param name="nickname">Display name written into <c>RoomPlayerInfo</c> +12.</param> <param
    /// name="characterId">Equipped character id in 300000..301999 written as the <c>map564</c> entry
    /// value.</param> <param name="level"> Player level written into <c>RoomPlayerInfo</c> +172, which every
    /// <c>LevelSymbolIcon</c> reader takes from the seat (<c>sub_912A00</c>, <c>sub_783D90</c>,
    /// <c>sub_65B190</c>). Without it the plaza snapshot pins every floating icon and every
    /// <c>DlgUserListPop</c> row to the level-1 badge. </param> <param name="slot">The plaza seat index this
    /// player keeps while in the plaza.</param>
    internal PlazaSeat(
        long sessionId, ISession game, int userNum, string nickname, int characterId, int level, int slot = 0)
    {
        Slot = slot;
        SessionId = sessionId;
        Game = game;
        UserNum = userNum;
        Nickname = nickname;
        CharacterId = characterId;
        Level = level;
    }

    /// <summary>Gets the game session id this seat is keyed by.</summary>
    internal long SessionId { get; }

    /// <summary>
    /// The seat this player holds in every client's kind-3 seat array, stable while the player
    /// stays: the entrant's S2C 117 places records by this index and later S2C 511 updates
    /// address it.
    ///
    /// </summary>
    internal int Slot { get; }

    /// <summary>Gets the game-protocol session the plaza snapshot is sent to.</summary>
    internal ISession Game { get; }

    /// <summary>Gets the player identity number.</summary>
    internal int UserNum { get; private set; }

    /// <summary>Gets the nickname the client matches itself by.</summary>
    internal string Nickname { get; private set; }

    /// <summary>Gets the equipped character id resolved at enter time.</summary>
    internal int CharacterId { get; private set; }

    /// <summary>Gets or sets the committed worn items used by plaza profile writers.</summary>
    internal IReadOnlyList<int>? EquippedItemIds { get; set; }

    /// <summary>Gets the player level resolved at enter time.</summary>
    internal int Level { get; private set; }

    /// <summary>
    /// Refreshes the identity, character id and level in place when a session
    /// re-enters the plaza, so the roster position (and with it the seat index
    /// the client derives from the vector position) survives a re-enter.
    ///
    /// </summary>
    /// <param name="userNum">The identity number.</param>
    /// <param name="nickname">The display name.</param>
    /// <param name="characterId">The equipped character id.</param>
    /// <param name="level">The player level.</param>
    internal void Refresh(int userNum, string nickname, int characterId, int level)
    {
        UserNum = userNum;
        Nickname = nickname;
        CharacterId = characterId;
        Level = level;
    }

    /// <summary>
    /// Builds the S2C 117 identity row for this seat at the given snapshot
    /// position. The client copies <c>players[i]</c> into seat <c>i</c>
    /// (<c>sub_9D9250</c>: the seat index is the vector position, not a field
    /// inside the record), so the slot must be the snapshot index. The level
    /// rides along because every <c>LevelSymbolIcon</c> reader takes it from the
    /// seat slot.
    ///
    /// </summary>
    /// <param name="slot">Zero-based position of this seat in the snapshot.</param>
    internal RoomPlayerIdentity ToIdentity(int slot) =>
        new(UserNum, Nickname, checked((byte)slot), Math.Max(1, Level));
}

/// <summary>
/// Bounded roster of the players currently in the plaza (stage 3). Every mutation takes <see
/// cref="Gate"/>; the collection is capped at <see cref="SeatLimit"/> and refuses the next player instead of
/// growing. </summary> <remarks> <para> Why this is not a <see cref="PlayRoom"/>: the client code shows kind 3 is
/// the same client session object as kind 2 (<c>sub_9D9250</c> runs before the kind switch and builds 36 seats
/// for kind 3), but the server-side room model does not describe a plaza. A room carries an id the S2C 101 list
/// and the C2S 403 row click resolve, a phase the administration start seam drives, and a relay key issued with
/// S2C 571; a plaza player has none of those, and folding plaza players into <see cref="RoomRegistry"/> would
/// put them in the room list and the start seam. The roster is therefore the smallest separate state that
/// carries exactly what the kind-3 snapshot needs. </para> <para> Membership changes only through C2S 106
/// (enter) and the connection close: there is no modeled incremental plaza enter/leave notification (every
/// <c>sub_9E1EB0</c> case reads one to three scalars, and the room-family membership notifies are all gated on
/// <c>RoomStage8vs8</c>), so the snapshot is the only thing that changes a client's plaza population. </para>
/// </remarks>
internal sealed class PlazaRoster
{
    /// <summary>
    /// Seat cap: the client's <c>sub_9D9250</c> builds 36 seats for kind 3
    /// (<c>sub_9D9250</c> case 2 of the <c>kind - 1</c> switch), so a snapshot
    /// cannot usefully carry more players and the roster fails loud on the 37th
    /// instead of growing without a bound. Protocol constant, not a tunable.
    ///
    /// </summary>
    internal const int SeatLimit = 36;

    private readonly List<PlazaSeat> _seats = new(SeatLimit);

    /// <summary>Creates an empty plaza whose operations run on a bounded executor.</summary>
    /// <param name="queueCapacity">Waiting-operation bound, from <see cref="InGameOptions.RoomQueueCapacity"/>.</param>
    internal PlazaRoster(int queueCapacity = 64) => Executor = new BubbleFighter.Fantasy.RoomCore.RoomExecutor(queueCapacity);

    /// <summary>
    /// Serializes plaza membership changes and the announcements they cause, so an entry and a
    /// departure never interleave their S2C 511 updates.
    ///
    /// </summary>
    internal BubbleFighter.Fantasy.RoomCore.RoomExecutor Executor { get; }

    /// <summary>Gets the lock every mutation and snapshot takes.</summary>
    internal object Gate { get; } = new();

    /// <summary>Gets the number of players currently on the roster.</summary>
    internal int Count
    {
        get
        {
            lock (Gate)
                return _seats.Count;
        }
    }

    /// <summary>
    /// Adds a session to the plaza, or refreshes it in place when it is already
    /// rostered (C2S 106 has more than one emitter, so a re-enter must not
    /// duplicate the player).
    ///
    /// </summary>
    /// <param name="game">The entering game session.</param>
    /// <param name="userNum">Player identity.</param>
    /// <param name="nickname">Player display name.</param>
    /// <param name="characterId">Equipped character id resolved through the room order.</param>
    /// <param name="level">Player level resolved through the lobby record.</param>
    /// <returns>The seat the session occupies.</returns>
    internal PlazaSeat Enter(
        ISession game, int userNum, string nickname, int characterId, int level)
    {
        lock (Gate)
        {
            _seats.RemoveAll(seat => seat.Game.Connection.IsClosed);
            if (_seats.Any(seat => seat.SessionId != game.Id && seat.UserNum == userNum))
                throw new InvalidOperationException($"Player {userNum} already has an active plaza session.");
            foreach (var seat in _seats)
            {
                if (seat.SessionId != game.Id)
                    continue;
                seat.Refresh(userNum, nickname, characterId, level);
                return seat;
            }

            if (_seats.Count >= SeatLimit)
                throw new InvalidOperationException(
                    $"Plaza roster is full ({SeatLimit} players): the kind-3 seat array sub_9D9250 builds holds {SeatLimit} seats.");
            var slot = Enumerable.Range(0, SeatLimit).First(candidate => _seats.All(seat => seat.Slot != candidate));
            var added = new PlazaSeat(game.Id, game, userNum, nickname, characterId, level, slot);
            _seats.Add(added);
            return added;
        }
    }

    /// <summary>
    /// Removes every seat whose session rides on the closed connection. The
    /// connection close is the only plaza leave signal the server has: no C2S
    /// asks to leave the plaza, and the room-family leave notifies are
    /// <c>RoomStage8vs8</c>-gated.
    ///
    /// </summary>
    /// <param name="connection">The connection that closed.</param>
    /// <returns>Whether the roster changed.</returns>
    internal bool Leave(IConnection connection) => RemoveConnection(connection.Id) is not null;

    /// <summary>Removes and returns the seat of <paramref name="sessionId"/>, or null when it is not in the plaza.</summary>
    /// <param name="sessionId">The leaving game session.</param>
    internal PlazaSeat? Remove(long sessionId)
    {
        lock (Gate)
        {
            var seat = _seats.FirstOrDefault(candidate => candidate.SessionId == sessionId);
            if (seat is not null)
                _seats.Remove(seat);
            return seat;
        }
    }

    /// <summary>Removes and returns the seat riding on a closed connection, or null.</summary>
    /// <param name="connectionId">The closed connection.</param>
    internal PlazaSeat? RemoveConnection(long connectionId)
    {
        lock (Gate)
        {
            var seat = _seats.FirstOrDefault(candidate => candidate.Game.Connection.Id == connectionId);
            if (seat is not null)
                _seats.Remove(seat);
            return seat;
        }
    }

    /// <summary>Snapshots the roster in seat order.</summary>
    internal PlazaSeat[] Snapshot()
    {
        lock (Gate)
            return [.. _seats.OrderBy(seat => seat.Slot)];
    }

    /// <summary>
    /// Finds the seat a session occupies, or <see langword="null"/> when it is
    /// not on the roster.
    ///
    /// </summary>
    /// <param name="sessionId">The game session id to look up.</param>
    internal PlazaSeat? Find(long sessionId)
    {
        lock (Gate)
        {
            foreach (var seat in _seats)
            {
                if (seat.SessionId == sessionId)
                    return seat;
            }

            return null;
        }
    }

    /// <summary>
    /// Returns every seat except <paramref name="sessionId"/>, in roster order. Used by the plaza
    /// position relay, which is the plaza's only continuous multiplayer surface (S2C 301 is applied by
    /// <c>sub_91FB80</c> on stage 3, whose gate blocks only stage 5). </summary> <param name="sessionId">The
    /// sending session, which is skipped.</param>
    internal PlazaSeat[] Others(long sessionId)
    {
        lock (Gate)
            return [.. _seats.Where(seat => seat.SessionId != sessionId)];
    }
}
