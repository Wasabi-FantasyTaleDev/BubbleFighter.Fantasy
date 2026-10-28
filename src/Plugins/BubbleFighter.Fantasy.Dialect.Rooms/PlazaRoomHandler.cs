using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.RoomCore;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Plaza room loop: list (empty), create one seat, ready, pick map, host-start into play.
/// The loop is list / create / GotoRoom / ChangeGameSetting / SetSlot / RoomEnterGame /
/// RoomReqEnterGame / ReadyStatus. This handler answers create with GotoRoom kind 2, ready
/// with SetSlot, map pick by storing <c>gcpRoomReqEnterGame</c>'s first wstr, and host-start
/// with RoomEnterGame (not the tutorial PlayTutorial path). <c>gspUserHostStart</c> is not
/// sent: cn-717 <c>sub_8BEDF0</c> returns early unless capability 23 is enabled and room+532
/// (the host-ready UI, cn-717 <c>sub_8FB7B0</c>) is set (profiles/cn-717/opcodes.yaml
/// gspUserHostStart), while <c>gspRoomEnterGame</c> case 0 enters play without room+532
/// on the same build.
/// </summary>
internal sealed class PlazaRoomHandler
{
    private const int DefaultCharacterItemId = 300001;

    private readonly RoomSchema _room;
    private readonly PacketTables _packetTables;
    private readonly string _build;
    private readonly IOpcodeTableRegistry _tables;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<long, RoomMembers<PlazaSeat>> _rooms = [];
    private readonly Dictionary<long, long> _connectionToSession = [];
    private readonly int _maxRooms;

    /// <summary>Creates the plaza handler for one legacy game protocol.</summary>
    /// <param name="room">The build's room field tables.</param>
    /// <param name="packetTables">Native layouts for the ready and start requests.</param>
    /// <param name="build">The client build selected by the dialect row.</param>
    /// <param name="maxRooms">Maximum number of live rooms held by this row.</param>
    /// <param name="tables">Opcode tables keyed by session profile.</param>
    /// <param name="logger">Plugin logger.</param>
    public PlazaRoomHandler(
        RoomSchema room,
        PacketTables packetTables,
        string build,
        int maxRooms,
        IOpcodeTableRegistry tables,
        ILogger logger)
    {
        _room = room;
        _packetTables = packetTables;
        _build = build;
        _ = packetTables.Resolve(build, "RoomEnterGame");
        _ = packetTables.Resolve(build, "RoomEnterGameHeader");
        _ = packetTables.Resolve(build, "RoomEnterGameTail");
        _ = packetTables.Resolve(build, "RoomReadyRequest");
        _ = packetTables.Resolve(build, "RoomListRequest");
        _ = packetTables.Resolve(build, "RoomListReply");
        _ = packetTables.Resolve(build, "RoomSeatUpdate");
        _ = packetTables.Resolve(build, "RoomEnterGameRequest");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRooms);
        _maxRooms = maxRooms;
        _tables = tables;
        _logger = logger;
    }

    /// <summary>
    /// Handles <c>gcpReqListSession</c>: replies with an empty <c>gspRepListSession</c> so the
    /// LobbyStage list UI can paint without invented public rooms.
    /// </summary>
    public async ValueTask HandleListSessionAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        PlazaRoomWire.ReadListSession(_packetTables, _build, packet.Payload.Span);
        RequirePlayer(session);
        var table = _tables.Get(session.Profile.Id);
        var body = PlazaRoomWire.WriteRepListSessionEmpty(_packetTables, _build);
        await session.SendAsync(table.S2C("gspRepListSession"), body, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} plaza list: S2C gspRepListSession empty ({Bytes} bytes).",
            session.Id, body.Length);
    }

    /// <summary>
    /// Handles <c>gcpReqCreateSession</c>: opens a one-seat play room via <c>gspGotoRoom</c>
    /// kind 2. Stores the first GameSetting wstr as the later RoomEnterGame map key.
    /// </summary>
    public async ValueTask HandleCreateSessionAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        CreateSessionRequest request;
        try
        {
            request = PlazaRoomWire.ReadCreateSession(packet.Payload.Span, _room);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gcpReqCreateSession does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gcpReqCreateSession").ConfigureAwait(false);
            return;
        }

        var player = RequirePlayer(session);
        var mapKey = string.IsNullOrEmpty(request.MapKey) ? PlazaRoomWire.DefaultMapKey : request.MapKey;
        lock (_gate)
        {
            if (_rooms.ContainsKey(session.Id))
                throw new InvalidOperationException($"Session {session.Id} already owns a room.");
            if (_rooms.Count >= _maxRooms)
                throw new InvalidOperationException($"Legacy room table is full ({_maxRooms}).");
            var members = new RoomMembers<PlazaSeat>(1);
            members.Add(slot => new PlazaSeat(slot, player.Room, mapKey));
            _rooms.Add(session.Id, members);
            _connectionToSession.Add(session.Connection.Id, session.Id);
        }

        var table = _tables.Get(session.Profile.Id);
        // Kind 2 play room; mode stays 0 (tutorial mode 103 is only WriteRoom'd from PlayTutorial).
        var body = RoomCodec.WriteSingleRoomEnvelope(
            player.Room, _packetTables, _build, _room, RoomCodec.PlayRoomKind, mode: 0, mapName: mapKey);
        await session.SendAsync(table.S2C("gspGotoRoom"), body, ct).ConfigureAwait(false);
        player.Session.MoveTo(SessionPhase.Room);
        _logger.LogInformation(
            "Session {Id} plaza create: S2C gspGotoRoom kind {Kind} ({Bytes} bytes, mapKey={MapKey}).",
            session.Id, RoomCodec.PlayRoomKind, body.Length, mapKey);
    }

    /// <summary>
    /// Handles <c>gcpRoomReqReadyStatus</c>: replies with <c>gspRoomSetSlot</c> for seat 0 with
    /// UserSlot +125 ready set when any of the three request flags is true.
    /// </summary>
    public async ValueTask HandleReadyStatusAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var (unknown0, unknown1, unknown2) = PlazaRoomWire.ReadReadyStatus(_packetTables, _build, packet.Payload.Span);
        var player = RequirePlayer(session);
        var ready = unknown0 || unknown1 || unknown2;
        PlazaSeat seat;
        lock (_gate)
        {
            seat = RequireSeat(session.Id);
            seat.Ready = ready;
        }

        var table = _tables.Get(session.Profile.Id);
        var body = PlazaRoomWire.WriteSetSlot(seat.Room, _packetTables, _build, _room, slot: 0, ready);
        await session.SendAsync(table.S2C("gspRoomSetSlot"), body, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} plaza ready: S2C gspRoomSetSlot ready={Ready} ({Bytes} bytes, flags={F0}/{F1}/{F2}).",
            session.Id, ready, body.Length, unknown0, unknown1, unknown2);
    }

    /// <summary>
    /// Handles <c>gcpRoomReqEnterGame</c>: stores the first wstr as the map key for the later
    /// host-start <c>gspRoomEnterGame</c>. Does not start the match here.
    /// </summary>
    public async ValueTask HandleRoomReqEnterGameAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        string mapKey;
        try
        {
            (mapKey, _) = PlazaRoomWire.ReadRoomReqEnterGame(_packetTables, _build, packet.Payload.Span);
        }
        catch (PacketFormatException ex)
        {
            _logger.LogError(
                "Session {Id} gcpRoomReqEnterGame does not match the expected layout ({Reason}); closing.",
                session.Id, ex.Message);
            await session.Connection.CloseAsync("malformed gcpRoomReqEnterGame").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(mapKey))
            mapKey = PlazaRoomWire.DefaultMapKey;

        var player = RequirePlayer(session);
        lock (_gate)
            RequireSeat(session.Id).MapKey = mapKey;
        _logger.LogInformation("Session {Id} plaza map pick: mapKey={MapKey}.", session.Id, mapKey);
    }

    /// <summary>
    /// Handles empty <c>gcpUserHostStart</c>: sends plaza <c>gspRoomEnterGame</c> with the
    /// stored map key. Does not send
    /// <c>gspUserHostStart</c> (the client gate needs room+532).
    /// </summary>
    public async ValueTask HandleUserHostStartAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        PlazaRoomWire.ReadUserHostStart(packet.Payload.Span);
        var player = RequirePlayer(session);
        string mapKey;
        lock (_gate)
            mapKey = RequireSeat(session.Id).MapKey;

        var table = _tables.Get(session.Profile.Id);
        var body = TutorialWire.WriteRoomEnterGame(_packetTables, _build, unknown0: 0, unknown1: mapKey);
        await session.SendAsync(table.S2C("gspRoomEnterGame"), body, ct).ConfigureAwait(false);
        player.Session.MoveTo(SessionPhase.Room);
        _logger.LogInformation(
            "Session {Id} plaza start: S2C gspRoomEnterGame ({Bytes} bytes, mapKey={MapKey}).",
            session.Id, body.Length, mapKey);
    }

    /// <summary>Releases room state when its owner connection closes.</summary>
    public void OnConnectionClosed(ConnectionClosedEvent e)
    {
        lock (_gate)
        {
            if (_connectionToSession.Remove(e.Connection.Id, out var sessionId))
                _rooms.Remove(sessionId);
        }
    }

    private PlazaSeat RequireSeat(long sessionId)
    {
        if (!_rooms.TryGetValue(sessionId, out var members))
            throw new InvalidOperationException($"Session {sessionId} has no room; create must complete first.");
        return members.Members[0];
    }

    private sealed class PlazaSeat(int slot, MainMenuRoomOpened room, string mapKey) : IRoomMember
    {
        public int Slot { get; } = slot;
        public MainMenuRoomOpened Room { get; } = room;
        public string MapKey { get; set; } = mapKey;
        public bool Ready { get; set; }
        public bool InShop => false;
        public bool InInventory => false;
    }

    private sealed record PlayerContext(MainMenuRoomOpened Room, IPlayerSession Session);

    private static PlayerContext RequirePlayer(ISession session)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
            throw new InvalidOperationException($"Session {session.Id} has no IPlayerSession; login must complete before plaza room.");
        var room = new MainMenuRoomOpened(
            player.Identity.UserNum,
            player.Identity.Nickname,
            DefaultCharacterItemId,
            [DefaultCharacterItemId], player.Identity.Level) { ColorIndex = player.Identity.ColorIndex };
        return new PlayerContext(room, player);
    }
}
