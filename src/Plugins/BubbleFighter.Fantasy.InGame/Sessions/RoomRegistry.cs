using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.RoomCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// Bounded in-memory table of rooms. Session-to-room maps are strictly capped by
/// <see cref="InGameOptions.MaxMatches"/> times <see cref="InGameOptions.MaxPlayersPerMatch"/>.
/// Rooms are created only from C2S 402; ready / start never invent a room.
/// Also publishes <see cref="IMatchConnectionTracker"/>: a session that completed
/// a relay authentication is a match connection, and the lobby login handler
/// consults that classification to leave the reconnect-shaped C2S 3 alone.
///
/// </summary>
public sealed partial class RoomRegistry : IMatchConnectionTracker, IMatchCapabilityDirectory, IRoomAdminDirectory
{
    private readonly ConcurrentDictionary<int, PlayRoom> _rooms = new();
    private readonly ConcurrentDictionary<long, int> _sessionToRoom = new();
    // Connection id to the session this registry attached, so a close event (which names only
    // the connection) can detach it. One entry per live attached connection; removed on close.
    private readonly ConcurrentDictionary<long, long> _connectionToSession = new();
    // The game connection's player state, shared onto the relay session bound to the same seat so
    // relay-side handlers know who is playing. One handle per bound relay; released with the relay.
    private readonly ConcurrentDictionary<long, IDisposable> _relayPlayers = new();
    // Executor shutdowns of deleted rooms still in flight; an entry leaves when its stop completes,
    // so the table never holds more than the rooms deleted during one running operation each.
    private readonly ConcurrentDictionary<int, Task> _retiring = new();
    private readonly InGameOptions _options;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public string ClientBuild => _options.GameProtocol.Split('/')[0];
    private int _nextId;

    // Room ids travel as the u16 SessionSetting+0 of list rows and room sessions, so they cycle
    // through 1..65535 and skip ids still live or retiring.
    private const int MaxRoomId = ushort.MaxValue;

    /// <summary>Creates the registry.</summary>
    /// <param name="options">Bound row options.</param>
    /// <param name="logger">Receives failures of background executor shutdowns for deleted rooms.</param>
    public RoomRegistry(InGameOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Creates a room from a parsed C2S 402 session blob and seats the host.</summary>
    /// <param name="game">The creating game session.</param>
    /// <param name="setting">Client session prefix.</param>
    /// <param name="userNum">Host identity.</param>
    /// <param name="nickname">Host nickname.</param>
    public (PlayRoom Room, RoomSeat Seat) Create(ISession game, SessionSettingBlob setting, int userNum, string nickname) =>
        Create(game, setting, null, userNum, nickname, listed: true);

    /// <summary>
    /// Creates a room from a parsed C2S 503 / 107 rule record and seats the host.
    /// The room's <see cref="SessionSettingBlob"/> is built through
    /// <see cref="SessionSettingBlob.ForRoom"/>: the kind-2 room-stage gate, the
    /// room state that moves the client to stage 4, and the cleared
    /// <c>MapMeta</c> gate fields, with an empty map because the
    /// rule-record-to-map offset mapping is unverified. The rule record itself
    /// is kept on the room and echoed in the log.
    ///
    /// </summary>
    /// <param name="game">The creating game session.</param>
    /// <param name="rules">The parsed 56-byte rule record.</param>
    /// <param name="userNum">Host identity.</param>
    /// <param name="nickname">Host nickname.</param>
    /// <param name="roomName">Display name the S2C 101 room-list row carries.</param>
    public (PlayRoom Room, RoomSeat Seat) CreateFromRules(
        ISession game, RoomRuleRecord rules, int userNum, string nickname, string roomName = "") =>
        CreateFromRules(game, rules, userNum, nickname, roomName, MapMetaBlob.Empty());

    /// <summary>
    /// Creates a room from a parsed C2S 503 rule record, seeding the session's <c>MapMeta</c> from
    /// the map keys the request carried. </summary> <remarks> The C2S 503 builder <c>sub_9D7570</c> writes
    /// three prefixed wstrings between its leading value and its rule record, and its caller <c>sub_9A0880</c>
    /// fills them from the client's own map selection: <c>session+208</c> (the resolved map key, <c>MapMeta</c>
    /// <c>unknown68</c>) at <c>sub_9A0880</c>, the map record's <c>MapName</c> through <c>sub_10E49C0</c> at
    /// <c>:77-84</c>, and <c>session+184</c> (the raw selection, <c>unknown44</c>, possibly a <c>Random*</c>
    /// marker) at <c>:70-76</c>. Echoing them back is therefore what gives the room a scene key: the S2C 510
    /// stage enter copies its own three map keys into those same slots (<c>sub_9E5E70</c>,
    /// <c>:180-203</c>), so an empty triple erases whatever the S2C 117 install carried. Evidence: verified
    /// read sites and the 510 copy. </remarks> <param name="game">The creating game session.</param> <param
    /// name="rules">The parsed 56-byte rule record.</param> <param name="userNum">Host identity.</param> <param
    /// name="nickname">Host nickname.</param> <param name="roomName">Display name the S2C 101 room-list row
    /// carries. The C2S 503 body carries no room name, so this path passes an empty string.</param> <param
    /// name="map">The <c>MapMeta</c> the room's session carries; the map-selection strings are the scene
    /// keys.</param> <param name="listed">Whether other players can list and join the room at once. The client
    /// create paths pass <see langword="false"/> and call <see cref="Publish"/> after the host's create
    /// push.</param>
    public (PlayRoom Room, RoomSeat Seat) CreateFromRules(
        ISession game, RoomRuleRecord rules, int userNum, string nickname, string roomName, MapMetaBlob map,
        bool listed = true)
    {
        var setting = SessionSettingBlob.ForRoom(new SessionSettingBlob(
            0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
            SessionSettingBlob.MainMenuKind, map,
            0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0), _options.RoomState);
        var (room, seat) = Create(game, setting, rules, userNum, nickname, listed);
        room.RoomName = roomName;
        seat.Level = SessionPlayer.ResolveLevel(game);
        return (room, seat);
    }

    /// <summary>
    /// Creates a room from a parsed C2S 402 client setting and seats the host. The client's map
    /// selection (mode and map strings) is echoed through <see cref="SessionSettingBlob.ForRoom"/>, which
    /// normalizes the three fields the S2C 117 kind-2 branch reads: <c>kind</c> to the room-stage gate value 2,
    /// the room state to the value that takes the <c>SetStage(4)</c> path, and the <c>MapMeta</c> flags word
    /// plus its order-4 dword to zero. The live client sends <c>kind = 0</c> on create while the room snapshot handler applies the room stage only when <c>*(session+72) == 2</c>; the
    /// MapMeta ctor plants self-pointers that make <c>session+156</c> resolve to <c>MapMeta+0</c>, so the flags
    /// word gates both the stage-4 move and the member panel. When the session carries a resolved character id
    /// it is recorded on the host seat so the room push can write the profile-map entry. </summary> <param
    /// name="game">The creating game session.</param> <param name="setting">The client's <c>SessionSetting</c>
    /// from C2S 402.</param> <param name="userNum">Host identity.</param> <param name="nickname">Host
    /// nickname.</param> <param name="roomName">Display name the S2C 101 room-list row carries.</param> <param
    /// name="listed">Whether other players can list and join the room at once. The client create path passes
    /// <see langword="false"/> and calls <see cref="Publish"/> after the host's create push.</param>
    public (PlayRoom Room, RoomSeat Seat) CreateFromSetting(
        ISession game, SessionSettingBlob setting, int userNum, string nickname, string roomName = "",
        bool listed = true)
    {
        // The mode the client sent, unless a deployment overrides it. Mode 1 - the
        // base mode a default create carries - is outside every room-stage click
        // gate, so an override is the only way to make team-change, ready and start
        // dispatch at all. Both the room record and the MapMeta the client installs
        // carry it, because the client reads the mode from session+152, i.e. the
        // MapMeta slot, not from anything the server keeps. See
        // InGameOptions.ModeOverride.
        var mode = (ushort)(_options.ModeOverride != 0 ? _options.ModeOverride : setting.Map.Unknown4);
        var roomSetting = SessionSettingBlob.ForRoom(
            _options.ModeOverride != 0 ? setting with { Map = setting.Map with { Unknown4 = mode } } : setting,
            _options.RoomState);
        var (room, seat) = Create(game, roomSetting, null, userNum, nickname, listed);
        room.RoomName = roomName;
        room.ModeId = mode;
        seat.Level = SessionPlayer.ResolveLevel(game);
        return (room, seat);
    }

    /// <summary>
    /// Creates a room from a typed create request and seats the host. The session blob carries only
    /// the mode: a build whose codec needs more of it builds that from the room's own fields.
    ///
    /// </summary>
    /// <param name="game">The creating game session.</param>
    /// <param name="modeId">The game mode id.</param>
    /// <param name="mapKey">The selected map key or random-selection marker.</param>
    /// <param name="roomName">The room title.</param>
    /// <param name="userNum">Host identity.</param>
    /// <param name="nickname">Host nickname.</param>
    /// <param name="listed">Whether other players can list and join the room at once.</param>
    public (PlayRoom Room, RoomSeat Seat) CreateForMode(
        ISession game, ushort modeId, string mapKey, string roomName, int userNum, string nickname, bool listed)
    {
        var setting = SessionSettingBlob.ForRoom(new SessionSettingBlob(
            0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
            SessionSettingBlob.MainMenuKind, MapMetaBlob.Empty() with { Unknown4 = modeId },
            0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0), _options.RoomState);
        var (room, seat) = Create(game, setting, null, userNum, nickname, listed);
        room.RoomName = roomName;
        room.ModeId = modeId;
        room.MapKey = mapKey;
        seat.Level = SessionPlayer.ResolveLevel(game);
        return (room, seat);
    }

    private int NextRoomId() => (int)(unchecked((uint)Interlocked.Increment(ref _nextId) - 1u) % MaxRoomId) + 1;

    private (PlayRoom Room, RoomSeat Seat) Create(
        ISession game, SessionSettingBlob setting, RoomRuleRecord? rules, int userNum, string nickname, bool listed)
    {
        if (_sessionToRoom.ContainsKey(game.Id))
            throw new InvalidOperationException($"Session {game.Id} is already in a room.");
        if (_rooms.Count >= _options.MaxMatches)
            throw new InvalidOperationException($"Room table is full ({_options.MaxMatches}).");

        PlayRoom room;
        int id;
        var attempts = 0;
        while (true)
        {
            if (++attempts > MaxRoomId)
                throw new InvalidOperationException("No free room id.");
            id = NextRoomId();
            if (_rooms.ContainsKey(id) || _retiring.ContainsKey(id))
                continue;
            // The room session's leading u16 is the room id, as on the list row the client echoes in C2S 403.
            room = new PlayRoom(id, _options.MaxPlayersPerMatch, setting with { Unknown0 = checked((ushort)id) },
                _options.RoomQueueCapacity)
            {
                CreateRules = rules,
                Listed = listed,
            };
            if (_rooms.TryAdd(id, room))
                break;
            // Lost a race for the id: this executor never ran an operation, so its stop is immediate.
            var unused = room.Executor;
            _ = Task.Run(() => unused.DisposeAsync().AsTask());
        }

        lock (room.Gate)
        {
            var seat = room.Add(game, NewKey(), userNum, nickname);
            BindSession(game, id, seat.Slot);
            return (room, seat);
        }
    }

    /// <summary>
    /// Mode id of the single-player training ground. <c>sub_1134B50</c> maps it to
    /// <c>wgtTrainingRoom</c> and the kind-2 branch compares the mode against 40 at <c>sub_9AC490</c>
    /// <c>0x9ACB62</c>, where the stage move is skipped.
    /// </summary>
    public const ushort TrainingModeId = (ushort)GameModeId.TrainingRoom;

    /// <summary>
    /// Map name of the single-player training ground. The training entry resolves the map record
    /// under this key (<c>sub_892E10</c>), and the kind-2 room stage reads it back through <c>sub_9E4D10</c>
    /// when the selected-map slot holds a marker.
    /// </summary>
    public const string TrainingMapName = "rel_trainingRoom01";

    /// <summary>
    /// Creates the single-player training room from a C2S 108 rule record and seats the host. The
    /// mode is <see cref="TrainingModeId"/> and the map is <see cref="TrainingMapName"/>; neither is on the
    /// wire, so both are server-synthesized. </summary> <remarks> <see
    /// cref="SessionSettingBlob.RoomStateInRoom"/> stays set even for mode 40: the kind-2 branch tests
    /// <c>session+12</c> against 4 at <c>sub_9AC490</c> <c>0x9ACAB4</c> and only then compares the mode against
    /// 40 at <c>0x9ACB62</c>, so without the state value the stage move is skipped entirely and the room stage,
    /// <c>sub_962A60</c> and the scene-bootstrap builder <c>sub_96CF30</c> never run. This is why the training
    /// room carries the in-room state unconditionally rather than the configured <c>room_state</c>: it is the
    /// one room whose start depends on the value being exactly 4. The MapMeta carries the training map in
    /// <c>unknown92</c> (order 12, the current/scene map) and leaves <c>unknown44</c> (order 10, the selected
    /// map) in the marker form, because <c>sub_9E4D10</c> resolves the selected slot only when it is one of
    /// seven marker strings and a real name there arms the one unnulled map-key dereference in the path
    /// (<c>sub_96CF30:214</c>). </remarks> <param name="game">The creating game session.</param> <param
    /// name="rules">The parsed 56-byte rule record from C2S 108.</param> <param name="userNum">Host
    /// identity.</param> <param name="nickname">Host nickname.</param>
    public (PlayRoom Room, RoomSeat Seat) CreateTrainingRoom(
        ISession game, RoomRuleRecord rules, int userNum, string nickname)
    {
        // The training room carries the in-room state unconditionally: its stage
        // move depends on session+12 being exactly 4 (sub_9AC490 0x9ACAB4), so
        // the configured room_state does not apply here.
        var setting = SessionSettingBlob.ForRoom(new SessionSettingBlob(
            0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
            SessionSettingBlob.MainMenuKind,
            MapMetaBlob.ForTrainingRoom(TrainingModeId, TrainingMapName),
            0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0), SessionSettingBlob.RoomStateInRoom);
        // Never listed: the training ground is single-player, so no one else can find or join it.
        var (room, seat) = Create(game, setting, rules, userNum, nickname, listed: false);
        room.RoomName = "training";
        room.ModeId = TrainingModeId;
        seat.Level = SessionPlayer.ResolveLevel(game);
        return (room, seat);
    }

    /// <summary>Snapshots populated, published rooms including matches, for lobby list requests.</summary>
    public IReadOnlyList<PlayRoom> OpenRooms()
    {
        var open = new List<PlayRoom>();
        foreach (var room in _rooms.Values)
        {
            lock (room.Gate)
            {
                if (room.Listed && room.Phase != RoomPhase.Closed && room.Seats.Count > 0)
                    open.Add(room);
            }
        }
        return open;
    }

    /// <summary>
    /// Snapshots every live room regardless of phase, for the administration
    /// start seam. Rooms are keyed by id; the snapshot is ordered by id so the
    /// API output is stable.
    ///
    /// </summary>
    public IReadOnlyList<PlayRoom> All()
    {
        var rooms = new List<PlayRoom>(_rooms.Values);
        rooms.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return rooms;
    }

    /// <summary>Resolves one live room by its id, or <see langword="null"/> when it is gone.</summary>
    /// <param name="roomId">The room id handed out at create time.</param>
    public PlayRoom? Find(int roomId) => _rooms.TryGetValue(roomId, out var room) ? room : null;

    /// <summary>
    /// Resolves a room another player may join by its id: live and listed. An unlisted room (its
    /// host's create push not yet sent, or the training ground) answers <see langword="null"/>.
    ///
    /// </summary>
    /// <param name="roomId">The room id presented by C2S 403 or C2S 517.</param>
    public PlayRoom? FindJoinable(int roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            return null;
        lock (room.Gate)
            return room.Listed ? room : null;
    }

    /// <summary>
    /// Makes a room created with <c>listed: false</c> visible to the room list and to joins. Called
    /// after the host's create push has completed on the room's executor.
    ///
    /// </summary>
    /// <param name="room">The room to publish.</param>
    public void Publish(PlayRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        lock (room.Gate)
            room.Listed = true;
    }

    /// <summary>Joins an existing room and installs resolved character data under the room lock.</summary>
    /// <param name="game">The joining game session.</param>
    /// <param name="roomId">Room id presented by C2S 403 or C2S 517 (<c>sub_708310</c>).</param>
    /// <param name="userNum">Joiner identity.</param>
    /// <param name="nickname">Joiner nickname.</param>
    /// <param name="characterId">Resolved equipped character, or zero for callers that initialize the seat separately before publishing.</param>
    /// <param name="equippedItemIds">Resolved worn items installed before the seat becomes visible to publishers.</param>
    public (PlayRoom Room, RoomSeat Seat) Join(ISession game, int roomId, int userNum, string nickname,
        int characterId = 0, IReadOnlyList<int>? equippedItemIds = null)
    {
        if (characterId != 0 && characterId is < 300000 or > 301999)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        if (_sessionToRoom.ContainsKey(game.Id))
            throw new InvalidOperationException($"Session {game.Id} is already in a room.");
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException($"C2S 517 room id {roomId} is not a live room.");

        lock (room.Gate)
        {
            if (!room.Listed)
                throw new InvalidOperationException($"Room {roomId} is not open to joins.");
            var seat = room.Add(game, NewKey(), userNum, nickname);
            seat.CharacterId = characterId;
            seat.EquippedItemIds = equippedItemIds;
            seat.Level = SessionPlayer.ResolveLevel(game);
            BindSession(game, roomId, seat.Slot);
            return (room, seat);
        }
    }

    /// <summary>
    /// Resolves the room already attached to a game session, or <see langword="null"/>
    /// when there is none. The non-throwing form of <see cref="RequireGame"/>, for
    /// handlers where "not in a room" is an ordinary outcome rather than a fault.
    ///
    /// </summary>
    /// <param name="game">The game session.</param>
    public (PlayRoom Room, RoomSeat Seat)? TryGame(ISession game)
    {
        if (!_sessionToRoom.TryGetValue(game.Id, out var id) || !_rooms.TryGetValue(id, out var room))
            return null;
        lock (room.Gate)
            return room.FindByGame(game.Id) is { } seat ? (room, seat) : null;
    }

    /// <summary>Resolves the room already attached to a game session. Does not create one.</summary>
    /// <param name="game">The game session.</param>
    public (PlayRoom Room, RoomSeat Seat) RequireGame(ISession game)
    {
        if (!_sessionToRoom.TryGetValue(game.Id, out var id) || !_rooms.TryGetValue(id, out var room))
            throw new InvalidOperationException($"Session {game.Id} is not in a room. Create (C2S 402) or join (C2S 403) first.");
        lock (room.Gate)
        {
            var seat = room.FindByGame(game.Id)
                ?? throw new InvalidOperationException($"Session {game.Id} is mapped to room {id} but has no seat.");
            return (room, seat);
        }
    }

    /// <summary>Binds a relay session to the seat that owns <paramref name="key"/>.</summary>
    /// <param name="relay">The relay session.</param>
    /// <param name="key">The presented key.</param>
    public RoomSeat BindRelay(ISession relay, RelaySessionKey key)
    {
        foreach (var room in _rooms.Values)
        {
            lock (room.Gate)
            {
                var seat = room.FindByKey(key);
                if (seat is null)
                    continue;
                seat.Relay = relay;
                seat.RelayGeneration = room.StartGeneration;
                _sessionToRoom[relay.Id] = room.Id;
                _connectionToSession[relay.Connection.Id] = relay.Id;
                SharePlayer(seat.Game, relay);
                return seat;
            }
        }

        throw new InvalidOperationException("Relay authenticate presented an unknown RelaySessionKey.");
    }

    /// <inheritdoc />
    public bool IsMatchBound(ISession session)
    {
        if (!_sessionToRoom.TryGetValue(session.Id, out var roomId) || !_rooms.TryGetValue(roomId, out var room))
            return false;
        lock (room.Gate)
            return room.Seats.Any(seat => ReferenceEquals(seat.Relay, session));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The seat rewrite runs as an operation on the room's executor, so a bind cannot interleave with
    /// a start, a departure or the countdown expiry. The replaced transport is closed after the
    /// operation: its close raises a departure that queues its own operation. <see cref="MarkInitialized"/>
    /// and <see cref="Release"/> stay synchronous single-seat flag changes under the room gate, because
    /// the match transport calls them from its own locks and from close events.
    /// </remarks>
    public async ValueTask<MatchSeatBinding> BindAsync(ISession session, MatchAuthenticate authentication, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = RelaySessionKey.FromBytes(authentication.Ticket.Span);
        if (_sessionToRoom.ContainsKey(session.Id)) throw new InvalidOperationException("Session is already attached to a room.");
        PlayRoom? target = null;
        foreach (var room in _rooms.Values)
        {
            lock (room.Gate)
            {
                if (room.FindByKey(key) is null) continue;
                target = room;
                break;
            }
        }
        if (target is null) throw new InvalidOperationException("Unknown match capability.");

        ISession? previous = null;
        MatchSeatBinding? binding = null;
        try
        {
            await target.Executor.ExecuteAsync(_ =>
            {
                (previous, binding) = BindSeat(target, key, session, authentication);
                return ValueTask.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException("Match room was removed before the transport bound.");
        }
        if (previous is not null)
            await previous.Connection.CloseAsync("match transport replaced").ConfigureAwait(false);
        return binding!;
    }

    private (ISession? Previous, MatchSeatBinding Binding) BindSeat(PlayRoom room, RelaySessionKey key, ISession session,
        MatchAuthenticate authentication)
    {
        lock (room.Gate)
        {
            // Looked up again: the seat may have left, or a restart may have rekeyed it, while queued.
            var seat = room.FindByKey(key) ?? throw new InvalidOperationException("Unknown match capability.");
            if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing)
                || !seat.EndpointAdvertised || seat.Game.Connection.IsClosed
                || seat.NativeCapability is not null
                || seat.UserNum != authentication.UserNumber || seat.Nickname != authentication.Nickname)
                throw new InvalidOperationException("Match capability is not valid for this player and room phase.");
            return AttachMatchSeat(room, seat, session);
        }
    }

    private (ISession? Previous, MatchSeatBinding Binding) AttachMatchSeat(PlayRoom room, RoomSeat seat, ISession session)
    {
        lock (room.Gate)
        {
            if (session.Connection.IsClosed) throw new InvalidOperationException("A closed match transport cannot replace a seat binding.");
            var previous = seat.Relay;
            if (!_sessionToRoom.TryAdd(session.Id, room.Id)) throw new InvalidOperationException("Duplicate room transport.");
            if (previous is not null) Release(previous);
            seat.Relay = session;
            seat.RelayGeneration = room.StartGeneration;
            seat.RelayInitialized = false;
            seat.RelayConfirmed = false;
            _connectionToSession[session.Connection.Id] = session.Id;
            SharePlayer(seat.Game, session);
            return (previous, new(room.Id, seat.Slot, seat.UserNum, SceneEntity(seat.Slot),
                seat.Nickname, SeatMapKey(room), room.StartGeneration));
        }
    }

    /// <summary>
    /// The scene object of the character seated at <paramref name="slot"/>: every client numbers the seated
    /// characters <c>1000000 + 100000 * slot</c> (kr-812 and kr-1256 alike; see the round result's seat references).
    ///
    /// </summary>
    internal static int SceneEntity(int slot) => checked(1000000 + 100000 * slot);

    // The key the client's C2S 3 map report names: the round's stage (S2C 510 second slot).
    private static string SeatMapKey(PlayRoom room) =>
        room.RoundStageKey.Length > 0 ? room.RoundStageKey
            : room.RoundMapKey.Length > 0 ? room.RoundMapKey
            : room.Setting.Map.Unknown68;

    /// <inheritdoc />
    public bool IsCurrent(ISession session, MatchSeatBinding binding)
    {
        if (!_rooms.TryGetValue(binding.RoomId, out var room)) return false;
        lock (room.Gate)
            return room.StartGeneration == binding.Generation && room.Phase is RoomPhase.Starting or RoomPhase.Playing or RoomPhase.Result
                && room.Seats.Any(seat => seat.Slot == binding.Slot && ReferenceEquals(seat.Relay, session)
                    && !seat.Game.Connection.IsClosed && !session.Connection.IsClosed);
    }

    /// <inheritdoc />
    public void MarkInitialized(ISession session, MatchSeatBinding binding)
    {
        var room = Find(binding.RoomId) ?? throw new InvalidOperationException("Match room was removed.");
        lock (room.Gate)
        {
            if (!IsCurrent(session, binding)) throw new InvalidOperationException("Match binding was revoked.");
            room.Seats.Single(seat => seat.Slot == binding.Slot).RelayInitialized = true;
        }
    }

    /// <inheritdoc />
    public void Release(ISession session)
    {
        if (_sessionToRoom.TryGetValue(session.Id, out var roomId) && _rooms.TryGetValue(roomId, out var room))
        {
            lock (room.Gate)
            {
                var seat = room.Seats.FirstOrDefault(s => ReferenceEquals(s.Relay, session));
                if (seat is not null)
                {
                    seat.Relay = null;
                    seat.RelayInitialized = false;
                    seat.RelayConfirmed = false;
                    _sessionToRoom.TryRemove(session.Id, out _);
                    ForgetConnection(session);
                }
            }
        }
        UnsharePlayer(session.Id);
    }

    /// <summary>Resolves the room for a session, or <see langword="null"/>.</summary>
    /// <param name="sessionId">Game or relay session id.</param>
    public PlayRoom? TryGet(long sessionId)
    {
        return _sessionToRoom.TryGetValue(sessionId, out var id) && _rooms.TryGetValue(id, out var room)
            ? room
            : null;
    }

    /// <summary>Detaches whatever session this registry attached on a closed connection.</summary>
    /// <param name="connectionId">The closed connection.</param>
    public void DetachConnection(long connectionId)
    {
        if (_connectionToSession.TryRemove(connectionId, out var sessionId))
            Detach(sessionId);
    }

    /// <summary>Drops a session from its room and deletes empty rooms.</summary>
    /// <param name="sessionId">Game or relay session id.</param>
    public void Detach(long sessionId)
    {
        if (!_sessionToRoom.TryRemove(sessionId, out var id))
            return;
        if (!_rooms.TryGetValue(id, out var room))
            return;
        lock (room.Gate)
        {
            var relaySeat = room.Seats.FirstOrDefault(seat => seat.Relay?.Id == sessionId);
            if (relaySeat is not null)
            {
                var relay = relaySeat.Relay!;
                relaySeat.Relay = null;
                relaySeat.RelayInitialized = false;
                relaySeat.RelayConfirmed = false;
                ForgetConnection(relay);
                UnsharePlayer(relay.Id);
                return;
            }
            var gameSeat = room.FindByGame(sessionId);
            if (gameSeat is not null) ForgetConnection(gameSeat.Game);
            if (gameSeat?.Relay is { } attachedRelay)
            {
                _sessionToRoom.TryRemove(attachedRelay.Id, out _);
                ForgetConnection(attachedRelay);
                UnsharePlayer(attachedRelay.Id);
            }
            room.RemoveSession(sessionId);
            if (room.Phase == RoomPhase.Closed && _rooms.TryRemove(id, out _))
                Retire(room);
        }
    }

    // A departed transport must not retain its close-event index until the socket eventually closes.
    // Remove only its own value, so cleanup of an older lease cannot erase a newer attachment.
    private void ForgetConnection(ISession session) =>
        _connectionToSession.TryRemove(new KeyValuePair<long, long>(session.Connection.Id, session.Id));

    /// <summary>The session this registry attached on <paramref name="connectionId"/>, if any.</summary>
    /// <param name="connectionId">A connection id.</param>
    /// <param name="sessionId">Receives the attached session id.</param>
    public bool TryGetSessionOfConnection(long connectionId, out long sessionId) =>
        _connectionToSession.TryGetValue(connectionId, out sessionId);

    /// <summary>
    /// Resolves the room of the session this registry attached on a connection, or <see langword="null"/>
    /// when the connection carries no seated session. Used to pick the executor a close-time detach runs on.
    ///
    /// </summary>
    /// <param name="connectionId">The connection id.</param>
    public PlayRoom? FindByConnection(long connectionId) =>
        _connectionToSession.TryGetValue(connectionId, out var sessionId) ? TryGet(sessionId) : null;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AdminRoomSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rooms = _rooms.Values.Select(room => Snapshot(room, includePassword: false))
            .Where(room => room is not null).Cast<AdminRoomSnapshot>().OrderBy(room => room.RoomId).ToArray();
        return ValueTask.FromResult<IReadOnlyList<AdminRoomSnapshot>>(rooms);
    }

    /// <inheritdoc />
    public ValueTask<AdminRoomSnapshot?> GetAsync(int roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_rooms.TryGetValue(roomId, out var room) ? Snapshot(room, includePassword: true) : null);
    }

    private AdminRoomSnapshot? Snapshot(PlayRoom room, bool includePassword)
    {
        lock (room.Gate)
        {
            if (room.Phase == RoomPhase.Closed) return null;
            var players = room.Seats.Select(seat => new AdminRoomPlayer(seat.UserNum, seat.Nickname, seat.Level,
                    seat.Team, seat.Ready, seat.Slot == room.MasterSlot, false))
                .Concat(room.Bots.Select(bot => new AdminRoomPlayer(-1 - bot.Appearance.Slot, bot.Appearance.Nickname,
                    bot.Appearance.Level, bot.Appearance.Team, bot.Appearance.Ready, false, true)))
                .ToArray();
            var owner = players.FirstOrDefault(player => player.Host)?.Nickname;
            var clientBuild = room.Seats.FirstOrDefault()?.Game.Profile.Id ?? ClientBuild;
            return new AdminRoomSnapshot(room.Id, room.RoomName, clientBuild,
                room.ModeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(room.RoundMapKey) ? room.MapKey : room.RoundMapKey,
                room.Phase.ToString(), players.Length, room.SeatCount, room.Password.Length > 0,
                includePassword && room.Password.Length > 0 ? room.Password : null, owner, players);
        }
    }

    /// <summary>
    /// Stops the executor of every live room and waits for the shutdowns of already deleted rooms.
    /// Called on plugin unload after the handlers that enqueue room work have been removed; must not be
    /// called from a room operation.
    ///
    /// </summary>
    /// <returns>A task that completes when every executor has finished its running operation.</returns>
    public Task StopAllAsync()
    {
        var stops = new List<Task>(_retiring.Values);
        foreach (var room in _rooms.Values)
        {
            lock (room.Gate)
            {
                room.AutoStartCancellation?.Cancel();
                room.TutorialCancellation?.Cancel();
            }
            if (room.AutoStartTask is { } pending)
                stops.Add(pending);
            if (room.Tutorial is not null) stops.Add(room.CountdownTask);
            stops.Add(room.Executor.DisposeAsync().AsTask());
        }
        return Task.WhenAll(stops);
    }

    // A room is usually deleted by the operation running on its own executor, which may not stop itself
    // (RoomExecutor rejects reentry), so shutdown starts outside the operation's execution context and
    // completes once that operation returns.
    private void Retire(PlayRoom room)
    {
        lock (room.Gate)
        {
            room.AutoStartCancellation?.Cancel();
            room.TutorialCancellation?.Cancel();
        }
        Task stop;
        if (ExecutionContext.IsFlowSuppressed())
            stop = Task.Run(() => room.Executor.DisposeAsync().AsTask());
        else
        {
            using (ExecutionContext.SuppressFlow())
                stop = Task.Run(() => room.Executor.DisposeAsync().AsTask());
        }
        stop = Task.WhenAll(stop, room.AutoStartTask ?? Task.CompletedTask);
        if (room.Tutorial is not null) stop = Task.WhenAll(stop, room.CountdownTask);
        _retiring[room.Id] = stop;
        _ = stop.ContinueWith(completed =>
        {
            _retiring.TryRemove(new KeyValuePair<int, Task>(room.Id, stop));
            room.TutorialCancellation?.Dispose();
            if (completed.Exception is { } failure)
                _logger.LogError(failure, "Room {Room} executor failed to stop after the room was deleted.", room.Id);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void BindSession(ISession game, int roomId, int slot)
    {
        if (!_sessionToRoom.TryAdd(game.Id, roomId))
            throw new InvalidOperationException($"Session {game.Id} is already in a room.");
        _connectionToSession[game.Connection.Id] = game.Id;
    }

    private void SharePlayer(ISession game, ISession relay)
    {
        UnsharePlayer(relay.Id);
        if (relay is not ISessionWithFeatures || !game.TryFeature<IPlayerSession>(out var player)
            || relay.TryFeature<IPlayerSession>(out _))
            return;
        _relayPlayers[relay.Id] = relay.RegisterFeature<IPlayerSession>(player);
    }

    private void UnsharePlayer(long relaySessionId)
    {
        if (_relayPlayers.TryRemove(relaySessionId, out var handle))
            handle.Dispose();
    }

    internal static RelaySessionKey NewKey()
    {
        Span<byte> bytes = stackalloc byte[RelaySessionKey.ByteLength];
        RandomNumberGenerator.Fill(bytes);
        return RelaySessionKey.FromBytes(bytes);
    }
}
