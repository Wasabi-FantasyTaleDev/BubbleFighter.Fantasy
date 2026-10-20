using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Scenario.Tests.Harness;

/// <summary>One decoded S2C frame seen by a simulated client, with its arrival index on that client.</summary>
/// <param name="Index">Zero-based arrival index on the receiving client.</param>
/// <param name="Opcode">S2C opcode.</param>
/// <param name="Payload">Decrypted payload bytes.</param>
internal sealed record SimFrame(int Index, ushort Opcode, byte[] Payload);

/// <summary>
/// One simulated client as seen by a scenario script: the protocol actions a live client performs
/// (login, plaza, room list, create, join, ready, leave, return to menu) plus raw send and wait.
/// Each action waits for the reply that completes it and fails loudly when it does not arrive.
/// </summary>
internal sealed class SimActor
{
    // Opcodes the actions use. Requests reuse fixture payloads unless the body must come from a
    // reply (room id) or has a known flag layout.
    private const ushort C2SVersion = 1;
    private const ushort C2SLogin = 3;
    private const ushort C2SKeepalive = 41;
    private const ushort C2SMenuReturn = 105;
    private const ushort C2SPlazaEnter = 106;
    private const ushort C2SRoomList = 401;
    private const ushort C2SRoomCreate = 402;
    private const ushort C2SRoomJoin = 403;
    private const ushort C2SRoomLeave = 404;
    private const ushort C2SRoomStatus = 505;
    private const ushort C2SSessionProbe = 521;
    private const ushort C2SEnvironment = 1500;
    private const ushort S2CVersionOk = 2;
    private const ushort S2CLoginResult = 3;
    private const ushort S2CRoomList = 101;
    private const ushort S2CSceneInstall = 117;

    private readonly SimClient _client;
    private readonly SimFixtures _fixtures;
    private readonly TimeSpan _replyTimeout;
    private readonly CancellationToken _ct;

    /// <summary>Wraps a connected client.</summary>
    /// <param name="client">The connection.</param>
    /// <param name="fixtures">Captured request payloads.</param>
    /// <param name="replyTimeout">How long each action waits for its reply.</param>
    /// <param name="ct">Cancelled when the run must stop.</param>
    public SimActor(SimClient client, SimFixtures fixtures, TimeSpan replyTimeout, CancellationToken ct)
    {
        _client = client;
        _fixtures = fixtures;
        _replyTimeout = replyTimeout;
        _ct = ct;
    }

    /// <summary>The client name assigned by the test.</summary>
    public string Name => _client.Name;

    /// <summary>Number of S2C frames received so far; frames arriving later have an index at or above it.</summary>
    public int Cursor => _client.Cursor;

    /// <summary>S2C frames received at or after <paramref name="from"/>.</summary>
    public IReadOnlyList<SimFrame> Since(int from) => _client.Since(from);

    /// <summary>Sends one C2S frame with the given payload.</summary>
    public Task Send(ushort opcode, byte[] payload) => _client.SendAsync(opcode, payload, _ct);

    /// <summary>The fixture body of C2S <paramref name="opcode"/>, for a script that changes a field before sending it.</summary>
    /// <param name="opcode">The C2S opcode.</param>
    /// <param name="last">The last fixture body instead of the first.</param>
    /// <returns>A copy of the fixture body.</returns>
    public byte[] Reference(ushort opcode, bool last = false) =>
        [.. last ? _fixtures.Last(opcode) : _fixtures.First(opcode)];

    /// <summary>Sends the first (or with <paramref name="last"/>, the last) fixture payload of <paramref name="opcode"/>.</summary>
    public Task SendReference(ushort opcode, bool last = false) =>
        Send(opcode, last ? _fixtures.Last(opcode) : _fixtures.First(opcode));

    /// <summary>Waits for an S2C <paramref name="opcode"/> arriving at or after <paramref name="from"/> (default: now).</summary>
    public Task<SimFrame> WaitFor(ushort opcode, int? from = null, double? seconds = null) =>
        _client.WaitAsync(from ?? Cursor, f => f.Opcode == opcode, Timeout(seconds), $"S2C {opcode}", _ct);

    /// <summary>Waits for a frame matching <paramref name="match"/> arriving at or after <paramref name="from"/>.</summary>
    public Task<SimFrame> WaitFor(Func<SimFrame, bool> match, string what, int from, double? seconds = null) =>
        _client.WaitAsync(from, match, Timeout(seconds), what, _ct);

    /// <summary>
    /// Logs in to the in-process server using the test actor's name as its launcher session,
    /// then waits for the main-menu install before starting the bounded test keepalive.
    /// </summary>
    public async Task Login()
    {
        var from = Cursor;
        await SendReference(C2SVersion);
        await WaitFor(S2CVersionOk, from);

        from = Cursor;
        await Send(C2SLogin, LauncherLogin(Name));
        await WaitFor(S2CLoginResult, from);

        // Client order: environment, session probe, keepalive. A direct server may hold the menu
        // install until the session probe, so the install is awaited after these requests.
        await SendReference(C2SEnvironment, last: true);
        await SendReference(C2SSessionProbe);
        await SendReference(C2SKeepalive);
        await WaitFor(S2CSceneInstall, from);
        _client.StartKeepalive(TimeSpan.FromSeconds(15),
        [
            (C2SEnvironment, _fixtures.Last(C2SEnvironment)),
            (C2SKeepalive, _fixtures.First(C2SKeepalive)),
        ]);
    }

    /// <summary>Enters the plaza (C2S 106) and waits for the scene install.</summary>
    public Task EnterPlaza() => RequestInstall(C2SPlazaEnter, []);

    /// <summary>Returns to the main menu (C2S 105) and waits for the scene install.</summary>
    public Task ReturnToMenu() => RequestInstall(C2SMenuReturn, []);

    /// <summary>Leaves the current room (C2S 404) and waits for the scene install.</summary>
    public Task LeaveRoom() => RequestInstall(C2SRoomLeave, []);

    /// <summary>Joins room <paramref name="roomId"/> (C2S 403) and waits for the room install.</summary>
    public Task JoinRoom(ushort roomId) => RequestInstall(C2SRoomJoin, RoomJoin(roomId));

    /// <summary>
    /// Creates a room with the fixture create request (C2S 402), waits for the room install, and
    /// returns the leading u16 of the installed room's <c>SessionSetting</c>, the id the room list
    /// carries (0 when the server leaves it empty; use <see cref="SimRoomList.FindNewRoom"/> then).
    /// </summary>
    public async Task<ushort> CreateRoom()
    {
        var from = Cursor;
        await SendReference(C2SRoomCreate);
        var install = await WaitFor(S2CSceneInstall, from);
        var reader = new PacketReader(install.Payload);
        return SimRoomList.ReadSessionSetting(ref reader).Id;
    }

    /// <summary>Sets the waiting-seat ready flag (C2S 505). No reply is awaited.</summary>
    public Task SetReady(bool ready) => Send(C2SRoomStatus, RoomStatus(ready));

    /// <summary>
    /// Reads every page of the room list. C2S 401 carries the page cursor and page size as two
    /// u16s (the fixture request asks for page 0 of 10); paging stops at an empty or repeated
    /// page, at the total the reply announces, or after 20 pages.
    /// </summary>
    public async Task<IReadOnlyList<SimRoom>> ListRooms()
    {
        var template = _fixtures.First(C2SRoomList);
        var pageSize = template.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(2)) : (ushort)10;
        var all = new List<SimRoom>();
        var seen = new HashSet<ushort>();
        for (ushort page = 0; page < 20; page++)
        {
            var request = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(request, page);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), pageSize);
            var from = Cursor;
            await Send(C2SRoomList, request);
            var reply = await WaitFor(S2CRoomList, from);
            var total = BinaryPrimitives.ReadInt32LittleEndian(reply.Payload.AsSpan(8));
            var rows = SimRoomList.Parse(reply.Payload);
            var added = rows.Where(r => seen.Add(r.Id)).ToList();
            all.AddRange(added);
            if (added.Count == 0 || rows.Count < pageSize || all.Count >= total)
                break;
        }
        return all;
    }

    /// <summary>C2S 403 join body (builder <c>kr-1256 sub_854C90</c>): i32 room id and an empty prefixed wstring password.</summary>
    public static byte[] RoomJoin(ushort roomId)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body, roomId);
        return body;
    }

    /// <summary>C2S 505 body (builder <c>0x9785E0</c>): ready, in-shop, in-inventory as four-byte booleans.</summary>
    public static byte[] RoomStatus(bool ready)
    {
        var body = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(body, ready ? 1 : 0);
        return body;
    }

    /// <summary>
    /// C2S 3 launcher login body (<c>kr-1256 sub_10C6A00</c>): prefixed wstring session, two empty
    /// wstrings, an empty i32 vector, two zero i32s, and an empty wstring.
    /// </summary>
    public static byte[] LauncherLogin(string session)
    {
        var name = Encoding.Unicode.GetBytes(session);
        var body = new byte[4 + name.Length + 6 * 4];
        BinaryPrimitives.WriteInt32LittleEndian(body, session.Length);
        name.CopyTo(body, 4);
        return body;
    }

    private async Task RequestInstall(ushort opcode, byte[] payload)
    {
        var from = Cursor;
        await Send(opcode, payload);
        await WaitFor(S2CSceneInstall, from);
    }

    private TimeSpan Timeout(double? seconds) => seconds is { } s ? TimeSpan.FromSeconds(s) : _replyTimeout;
}
