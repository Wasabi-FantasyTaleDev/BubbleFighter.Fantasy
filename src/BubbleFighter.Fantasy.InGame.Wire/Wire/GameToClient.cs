using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Protocol;

using System.Buffers.Binary;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// Server-to-client builders for the post-join combat sequence, including the
/// server-driven room-to-match start chain. Each method cites the client reader
/// it was derived from.
///
/// </summary>
public static class GameToClient
{
    /// <summary>
    /// S2C 115 <c>gspGotoMenu</c>. Handler <c>sub_9AC0F0</c>: one <c>i32 result</c>.
    /// Zero is the success branch; nonzero copies a localized string. Evidence: verified.
    ///
    /// </summary>
    /// <param name="result">Client error code; 0 leaves the match.</param>
    public static byte[] GotoMenu(int result)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(result);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 503 create-room reply. Handler <c>sub_9DA720</c> (disassembly fallback; dispatcher
    /// <c>sub_9D9930</c> case 0x1F7): <c>i32 slotIndex</c>, one <see cref="RoomPlayerInfo"/> (no count prefix),
    /// two count-prefixed keyed maps (<c>sub_10C3C70</c> / <c>sub_10C3D80</c>), and the <see
    /// cref="ServerTeamInfoBlob"/> tail. The body ends after the team panel — there is no trailing scalar.
    /// Minimum length for one player with both maps empty is 212 bytes (4 + 188 + 4 + 4 + 12). Evidence:
    /// verified read order and widths. </summary> <remarks> The first keyed map is the profile-map writer the
    /// room-stage spawner and the dresser read: <c>sub_9DA720</c> applies it before the kind switch, exactly
    /// like the S2C 117 handler does at <c>0x9AC964</c>. An empty map leaves the seat without a profile record,
    /// so the avatar gate fails and no character is spawned for the host. Every seat that carries a resolved
    /// character id therefore gets one entry here too, keyed by its <c>UserNum</c> with the equipped character
    /// id as the value. </remarks> <param name="slotIndex">Seat index the client writes the record into
    /// (verified as an index).</param> <param name="player">The seat record; use <see
    /// cref="RoomPlayerInfo.Default"/> for the constructor-zero form.</param> <param name="profileEntries">
    /// Profile-map entries for the first keyed map. Pass an empty list only when no seat carries a resolvable
    /// character id; an empty map is the documented default and the avatar gate then fails for every seat.
    /// </param> <returns>The body with the first map populated from <paramref
    /// name="profileEntries"/>.</returns>
    public static byte[] CreateRoomReply(
        int slotIndex, RoomPlayerInfo player, IReadOnlyList<ProfileMapEntry>? profileEntries = null)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(slotIndex);
        player.Write(w);
        if (profileEntries is null || profileEntries.Count == 0)
        {
            w.WriteUInt32(0); // mapA: empty (entry shape documented, entries unverified)
        }
        else
        {
            w.WriteUInt32(checked((uint)profileEntries.Count));
            foreach (var entry in profileEntries)
                entry.Write(w);
        }

        w.WriteUInt32(0); // mapB: empty
        ServerTeamInfoBlob.Empty().Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 101 room-list snapshot. Dispatched on the LobbyStage virtual
    /// <c>sub_853CF0</c> at <c>0x853CF0</c> through <c>sub_84F550</c> and the
    /// <c>SessionSetting</c> reader <c>sub_113BD20</c>, then rendered by
    /// <c>sub_856CD0</c> -&gt; <c>sub_63CF90</c> (the <c>DlgRoomListPop</c>
    /// rows). Body: three <c>i32</c> headers, a <c>u32</c> count, then that
    /// many <c>SessionSetting</c> records (225-byte minimum each). Evidence:
    /// verified dispatch chain and body shape; the header meanings and the
    /// C2S 401 pairing are inferred.
    ///
    /// </summary>
    /// <param name="header0">First header <c>i32</c>; inferred page index.</param>
    /// <param name="header1">Second header <c>i32</c>; the meaning is
    /// unverified. The server writes 1, so the
    /// room-list sender passes 1.</param>
    /// <param name="header2">Third header <c>i32</c>; inferred total room count.</param>
    /// <param name="rooms">One <c>SessionSetting</c> per open room, in display order.</param>
    public static byte[] RoomList(int header0, int header1, int header2, IReadOnlyList<SessionSettingBlob> rooms)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(header0);
        w.WriteInt32(header1);
        w.WriteInt32(header2);
        w.WriteUInt32(checked((uint)rooms.Count));
        foreach (var room in rooms)
            room.Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 508 per-seat room record. Parser <c>sub_10C47E0</c>: <c>u16</c>
    /// seat index, <c>i32</c>, <c>wstring</c>, <c>wstring</c>, <c>i32</c>,
    /// <c>i32</c>, <c>wstring</c>, <c>i32</c>. The handler indexes the
    /// 320-byte seat array (bounded by the count at <c>session+0x240</c>,
    /// presence byte at <c>session+592+320*idx</c>), renders the fourth
    /// field's wstring as a chat line through <c>sub_4D6020</c>, and reads the
    /// displayed nickname from the seat record itself. Used for room chat and
    /// the per-seat notice variants. Evidence: verified field table and the
    /// chat render path.
    ///
    /// </summary>
    /// <param name="seatIndex">Sender seat index; must be present in the room.</param>
    /// <param name="message">Chat text; empty is a valid parse.</param>
    public static byte[] SeatChat(int seatIndex, string message)
    {
        var w = PacketWriter.Create();
        w.WriteUInt16(checked((ushort)seatIndex));
        w.WriteInt32(0);                      // unknown2
        w.WriteWideStringPrefixed(string.Empty); // unknown4
        w.WriteWideStringPrefixed(message);   // unknown28: the chat line
        w.WriteInt32(0);                      // unknown52: ARGB colour
        w.WriteInt32(0);                      // unknown56: 0 = character-bubble branch
        w.WriteWideStringPrefixed(string.Empty); // unknown60
        w.WriteInt32(0);                      // unknown84: 0 makes the handler guard run
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 508 per-seat attribute set. Handler <c>sub_9DAE50</c>: <c>i32</c>
    /// seat index, a one-byte value, an optional tagged tail (<c>0x3A</c>
    /// then two <c>i32</c>) feeding a <c>ServerTeamInfo</c>. The value is
    /// written to seat+4 through <c>sub_9E4C80</c>; value 36 short-circuits
    /// the handler. Not emitted yet: no proven server-side trigger.
    ///
    /// </summary>
    /// <param name="seatIndex">Seat index.</param>
    /// <param name="value">The one-byte attribute value.</param>
    public static byte[] SeatAttribute(int seatIndex, byte value)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(seatIndex);
        w.WriteByte(value);
        w.WriteByte(0);                      // optional tail absent
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 511 per-seat room record. Handler <c>sub_9DB1E0</c> at
    /// <c>0x9DB1E0</c> (<c>sub_9D9930</c> case 0x1FF): <c>i32 slotIndex</c>,
    /// one <c>RoomPlayerInfo</c>, one <c>ServerTeamInfo</c> — no keyed maps,
    /// unlike the S2C 503 create reply. The handler copies the record into
    /// <c>session+592+320*slot</c> (bounded by the seat count at
    /// <c>session+0x240</c>) and registers it through <c>sub_9E4C80</c>; on
    /// the room stage it clears the room-UI state words at stage+2016 / +2024
    /// / +2028 / +2033, and the room-UI walk grades the seat spawn as reachable
    /// from this path. Evidence: verified body order and slot bounds check
    /// (sub_9DB1E0 lines 79-93);
    ///
    /// </summary>
    /// <param name="slotIndex">Seat index the record belongs to.</param>
    /// <param name="player">The seat record.</param>
    public static byte[] SeatRecord(int slotIndex, RoomPlayerInfo player)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(slotIndex);
        player.Write(w);
        ServerTeamInfoBlob.Empty().Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 511 for a seat that was just vacated: the slot index with
    /// <see cref="RoomPlayerInfo.VacatedSeat"/>, 204 bytes. The remaining members'
    /// clients remove that seat's character (<c>sub_9DB1E0</c> unoccupied branch) without a
    /// room reinstall.
    ///
    /// </summary>
    /// <param name="slotIndex">The vacated seat.</param>
    public static byte[] SeatVacated(int slotIndex) => SeatRecord(slotIndex, RoomPlayerInfo.VacatedSeat());

    /// <summary>
    /// S2C 511 for an empty seat the room master closed or reopened: the vacated record with the
    /// byte at <c>RoomPlayerInfo</c> +1 set to 1 (closed) or 0 (open), 204 bytes. The seat widget
    /// refresh <c>sub_980730</c> draws the seat closed when that byte is 1, and the 511 handler runs that refresh for every seat on the room stage
    /// (<c>sub_9DB1E0</c> stage-4 branch). The click that asks for it is C2S 509.
    ///
    /// </summary>
    /// <param name="slotIndex">The empty seat.</param>
    /// <param name="closed">Whether the seat is now closed.</param>
    public static byte[] SeatClosed(int slotIndex, bool closed) =>
        SeatRecord(slotIndex, RoomPlayerInfo.VacatedSeat() with { Unknown1 = closed ? 1 : 0 });

    /// <summary>
    /// S2C 502, one <c>i32</c>: the seat index of the new room master. Handler
    /// <c>sub_9DA3C0</c> (<c>sub_9D9930</c> case 0x1F6) shows the "became room master" notice
    /// with that seat's nickname; it does not itself write the session's master field. Sent
    /// only when the master leaves. Evidence: kr-1256 sub_9DA3C0.
    ///
    /// </summary>
    /// <param name="newMasterSlot">The seat that became room master.</param>
    public static byte[] MasterChanged(int newMasterSlot)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(newMasterSlot);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 103, the room-join failure result: <c>i32 result</c>, <c>i32 argument</c>, read by
    /// <c>sub_853E90</c> case 0x67 and rendered by <c>sub_857990</c>. Result codes used here:
    /// <see cref="JoinResultRoomStarted"/>, <see cref="JoinResultRoomFull"/>,
    /// <see cref="JoinResultWrongPassword"/>, <see cref="JoinResultNoRoom"/>. A successful join
    /// sends no S2C 103.
    ///
    /// </summary>
    /// <param name="result">The failure code.</param>
    /// <param name="argument">Second <c>i32</c>; zero for the codes above.</param>
    public static byte[] JoinResult(int result, int argument = 0)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(result);
        w.WriteInt32(argument);
        return WireUtil.Written(w);
    }

    /// <summary>S2C 103 result: the room has already started (<c>sub_857990</c> case 1).</summary>
    public const int JoinResultRoomStarted = 1;

    /// <summary>S2C 103 result: the room is full (<c>sub_857990</c> case 3).</summary>
    public const int JoinResultRoomFull = 3;

    /// <summary>S2C 103 result: wrong room password (<c>sub_857990</c> case 4).</summary>
    public const int JoinResultWrongPassword = 4;

    /// <summary>S2C 103 result: the room does not exist (<c>sub_857990</c> case 5).</summary>
    public const int JoinResultNoRoom = 5;

    /// <summary>
    /// S2C 571 <c>gspConnectToRelayServer</c>. Handler <c>sub_9A7250</c>: 4-byte IPv4
    /// (network order), <c>u16</c> port, then <see cref="RelaySessionKey"/>. Evidence: verified.
    ///
    /// </summary>
    /// <param name="address">IPv4 advertise address.</param>
    /// <param name="port">TCP port.</param>
    /// <param name="key">Key the relay C2S 1 must echo.</param>
    public static byte[] ConnectRelay(IPAddress address, ushort port, RelaySessionKey key)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("S2C 571 requires IPv4.", nameof(address));
        var w = PacketWriter.Create();
        w.WriteBytes(address.GetAddressBytes());
        w.WriteUInt16(port);
        key.Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 38281. One <c>i32</c> entity id, resolved by the client's
    /// <c>sub_434840</c> through an FNV-1a hash of those four bytes.
    ///
    /// </summary>
    /// <param name="objectId">The scene-object id the client reported on C2S 607.</param>
    /// <returns>The payload.</returns>
    public static byte[] SceneObject(int objectId)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(objectId);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 128, the waiting dialog (client UI resource 1760 opened by
    /// <c>sub_858250</c> on state 0): one <c>i32</c> state, one <c>i32</c>
    /// argument and one prefixed wstring, 12 bytes when the string is empty. The parser
    /// <c>sub_84F6C0</c> performs two 4-byte reads and then <c>sub_1119C10</c>, the
    /// stream's wide-string read (the same call reads <c>SessionSetting+8</c> in
    /// <c>sub_113BD20</c>); the empty form is 12 bytes. Verified
    /// states: 0 shows the wall; 0x14 dismisses it (<c>sub_858FC0(0)</c> at
    /// <c>sub_858250</c>) and then, only in the client whose first seat is itself
    /// (<c>sub_906490</c>), re-sends C2S 402. The argument's and the string's meanings
    /// are unverified and ship zero and empty.
    ///
    /// </summary>
    /// <param name="state">0 opens the wall, 0x14 dismisses it and prompts the
    /// host's own C2S 402.</param>
    /// <param name="argument">The second <c>i32</c>; unverified, sent zero.</param>
    /// <returns>The payload.</returns>
    public static byte[] WaitingDialog(int state, int argument)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(state);
        w.WriteInt32(argument);
        w.WriteWideStringPrefixed(string.Empty);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 509, the chat render frame (chat-panel line + speech bubble). Parser
    /// <c>sub_10C4A50</c>; handler <c>sub_9DE360</c>: a non-negative actorCode takes the seat-chat
    /// branch (nickname resolved from the seat table via <c>sub_9DA160</c>, bubble through <c>sub_9DF410</c>,
    /// panel insert <c>sub_4DBBD0</c> with kind colouring 0-12). The drop branch exempts the sender's seat, so
    /// the broadcast must include the sender - the sender's own bubble arrives only via this echo.
    /// </summary>
    /// <param name="actorCode">Sender seat index (non-negative takes the seat-chat branch).</param> <param
    /// name="name">Display name (consumed on the negative-code branches).</param> <param name="kind">Chat kind;
    /// 0 = normal colour.</param> <param name="subkind">Sub-kind.</param> <param name="message">The chat
    /// line.</param> <param name="colour1">Primary colour.</param> <param name="colour2">Secondary
    /// colour.</param> <param name="style">Force/style boolean, serialized as a four-byte value.</param> <param
    /// name="extra">Trailing wstring at +96.</param> <param name="suppress">Suppress boolean, serialized as
    /// four bytes (0 keeps the frame visible in-stage).</param> <returns>The payload.</returns>
    public static byte[] ChatPanel(
        short actorCode,
        string name,
        int kind,
        int subkind,
        string message,
        int colour1,
        int colour2,
        byte style,
        string extra,
        byte suppress)
    {
        var w = PacketWriter.Create();
        w.WriteUInt16(unchecked((ushort)actorCode));
        w.WriteWideStringPrefixed(name);
        w.WriteInt32(kind);
        w.WriteInt32(subkind);
        w.WriteWideStringPrefixed(string.Empty);
        // 0x10C4A50 stores the second string at object+60, consumed as visible text by 0x9DE360.
        w.WriteWideStringPrefixed(message);
        w.WriteInt32(colour1);
        w.WriteInt32(colour2);
        w.WriteInt32(style != 0 ? 1 : 0);
        w.WriteWideStringPrefixed(extra);
        w.WriteInt32(suppress != 0 ? 1 : 0);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 32885, the full movement state frame (21 bytes): one <c>i32</c> actor id, three <c>f32</c>
    /// position components, three bytes (yaw, pitch, state) and a <c>u16</c> tick (sub_709700's 4,4,4,4,1,1,1,2
    /// reads). This is the frame the server rebroadcasts to every OTHER seat from each seat's high-cadence
    /// samples - the client drops frames carrying its own id before applying anything. </summary> <param
    /// name="actorId">The mover's client-computed entity id.</param> <param name="x">Position x.</param> <param
    /// name="y">Position y.</param> <param name="z">Position z.</param> <param name="yaw">Yaw byte.</param>
    /// <param name="pitch">Pitch byte.</param> <param name="state">State byte.</param> <param name="tick">The
    /// sample's tick, forwarded verbatim.</param> <returns>The 21-byte payload.</returns>
    public static byte[] MovementStateFrame(
        int actorId, float x, float y, float z, byte yaw, byte pitch, byte state, ushort tick)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(actorId);
        w.WriteSingle(x);
        w.WriteSingle(y);
        w.WriteSingle(z);
        w.WriteByte(yaw);
        w.WriteByte(pitch);
        w.WriteByte(state);
        w.WriteUInt16(tick);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 4, the arena zone-mask pair (268 bytes): one <c>i32</c> actor id,
    /// then two count-prefixed dword vectors - unpacked by <c>sub_10F48B0</c>
    /// into 1000 bools per group at <c>this+4604</c> (<c>sub_4832D0</c>).
    /// The retained initialization fixture has 22 nonzero dwords in vector 1,
    /// an empty vector 2 and zero padding in the fixed buffers. This builder
    /// always uses that fixture; it does not select masks by map or derive
    /// their individual bit meanings. Byte-pinning tests preserve the payload.
    ///
    /// </summary>
    /// <param name="actorId">The seat's client-computed entity id
    /// (1000000 + 100000 * slot).</param>
    /// <returns>The 268-byte payload.</returns>
    public static byte[] ArenaZoneMasks(int actorId)
    {
        var payload = new byte[268];
        Buffer.BlockCopy(ZoneMaskHead, 0, payload, 0, ZoneMaskHead.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload, actorId);
        return payload;
    }

    /// <summary>The 96 non-zero leading bytes of the zone-mask pair:
    /// actor placeholder, vector 1 count 32 with its 22 non-zero dwords, and
    /// vector 2's empty count.</summary>
    private static readonly byte[] ZoneMaskHead =
    {
        0x40, 0x42, 0x0F, 0x00, 0x20, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0B, 0x00, 0x80, 0x01, 0x01, 0x00,
        0x02, 0x94, 0x00, 0x05, 0x02, 0x00, 0x00, 0x01, 0x40, 0x02, 0x20, 0x00, 0x09, 0x00, 0x01, 0x00,
        0x00, 0xA0, 0x00, 0x02, 0x00, 0x00, 0x00, 0x41, 0x00, 0x80, 0x00, 0x00, 0x04, 0x01, 0x10, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x10, 0x10, 0x08, 0x81, 0x00, 0x10, 0x08, 0x00, 0x80,
        0x08, 0x00, 0x00, 0x00, 0x00, 0x02, 0x0C, 0x00, 0x00, 0x05, 0x00, 0x08, 0x40, 0x04, 0x00, 0x11,
        0x00, 0x50, 0x02, 0x20, 0x84, 0xB0, 0x0C, 0x20, 0x00, 0x18, 0x10, 0x00, 0x20, 0x18, 0xA0, 0x00,
    };

    /// <summary>
    /// S2C 5, the arena cell grid (1008 bytes): one <c>i32</c> actor id, one
    /// <c>i32</c> count 1000, then 1000 bytes stored at <c>this+10628</c>
    /// (<c>kr-1256 sub_483420</c>). The retained initialization fixture
    /// has 88 nonzero cells in the 0x12..0x1C band followed by zero padding.
    /// This builder uses one fixed fixture for all maps. The individual cell
    /// meanings remain unverified; byte-pinning tests preserve the payload.
    ///
    /// </summary>
    /// <param name="actorId">The seat's client-computed entity id.</param>
    /// <returns>The 1008-byte payload.</returns>
    public static byte[] ArenaCellGrid(int actorId)
    {
        var payload = new byte[1008];
        Buffer.BlockCopy(CellGridHead, 0, payload, 0, CellGridHead.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload, actorId);
        return payload;
    }

    /// <summary>The 96 non-zero leading bytes of the cell grid: the
    /// actor placeholder, the count 1000, and cells 0..87 (values 0x12..0x1C);
    /// cells 88..999 are zero.</summary>
    private static readonly byte[] CellGridHead =
    {
        0x40, 0x42, 0x0F, 0x00, 0xE8, 0x03, 0x00, 0x00, 0x16, 0x13, 0x1B, 0x1B, 0x12, 0x1A, 0x12, 0x19,
        0x1A, 0x1A, 0x1C, 0x19, 0x1B, 0x13, 0x18, 0x1B, 0x17, 0x18, 0x1A, 0x15, 0x16, 0x19, 0x1A, 0x18,
        0x12, 0x19, 0x13, 0x19, 0x12, 0x18, 0x12, 0x17, 0x15, 0x1C, 0x15, 0x1A, 0x15, 0x1B, 0x16, 0x14,
        0x14, 0x13, 0x1C, 0x16, 0x19, 0x15, 0x1B, 0x18, 0x16, 0x15, 0x16, 0x13, 0x18, 0x1B, 0x13, 0x19,
        0x16, 0x14, 0x1A, 0x16, 0x19, 0x16, 0x19, 0x13, 0x19, 0x17, 0x15, 0x1B, 0x1A, 0x1B, 0x17, 0x19,
        0x13, 0x18, 0x13, 0x14, 0x19, 0x15, 0x1B, 0x19, 0x1A, 0x18, 0x14, 0x19, 0x13, 0x1C, 0x1B, 0x17,
    };

    /// <summary>
    /// S2C 6, the third arena mask (136 bytes): one <c>i32</c> actor id, one
    /// <c>u32</c> count 32, then 32 <c>i32</c> unpacked into the mask at
    /// <c>this+11640</c> (<c>sub_483540</c>). Zero content, same caveat.
    ///
    /// </summary>
    /// <param name="actorId">The seat's client-computed entity id.</param>
    /// <returns>The 136-byte payload.</returns>
    public static byte[] ArenaThirdMask(int actorId)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(actorId);
        w.WriteInt32(32);
        for (var i = 0; i < 32; i++)
            w.WriteInt32(0);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 7, the altitude float (8 bytes): one <c>u32</c> actor id (read then
    /// discarded by <c>sub_9601E0</c>) and one <c>f32</c> stored at
    /// <c>game+0xF20</c> only when <c>game+0xF54</c> is set. The value is
    /// 110.0f; no consumer of
    /// <c>game+0xF20</c> was found, so the semantics are unproven.
    ///
    /// </summary>
    /// <param name="actorId">The seat's client-computed entity id.</param>
    /// <param name="altitude">The float value (110.0).</param>
    /// <returns>The 8-byte payload.</returns>
    public static byte[] ArenaAltitude(int actorId, float altitude)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(actorId);
        w.WriteSingle(altitude);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 38278, the in-session event: one <c>i32</c> entity id then one <c>i32</c> event code, 8
    /// bytes. Its handler <c>sub_48D3E0</c> (dispatcher <c>sub_6D55D0</c> case at <c>0x6D8952</c>)
    /// maps the code through <c>sub_49F8D0</c> and <c>sub_B002D0</c> to an event type rather than treating it
    /// as an argument: code <c>0</c> is an animation or effect event (<c>sub_48D3E0</c>), code <c>29</c>
    /// reaches the respawn <c>sub_49C870</c> that writes full HP and leaves the <c>csDead</c> state, and code
    /// <c>21</c> is the unguarded stand-up <c>sub_49D020</c>. </summary> <param name="entityId">The in-session
    /// entity the event targets.</param> <param name="code">The event code; see the
    /// <c>in_session_spawn_code</c> option of the in-game row.</param> <returns>The payload.</returns>
    public static byte[] InSessionSpawn(int entityId, int code)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(entityId);
        w.WriteInt32(code);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 504, one seat's team label: <c>i32</c> seat index, <c>u8</c> label,
    /// then the absent-form of the optional tail, six bytes. The handler
    /// <c>sub_9DAE50</c> stores the label into <c>seat+4</c> through
    /// <c>sub_9E4C80</c> and refreshes every slot board, so this is how a team
    /// change reaches the UI without the S2C 117's unconditional stage rebuild
    /// (<c>sub_9DAE50,131,143-164</c>, <c>sub_9E4C80</c>,
    /// <c>sub_980730</c>). Label 36 short-circuits the write and the
    /// refresh (<c>sub_9DAE50</c>); the values the board switches on are 1, 2
    /// and 36.
    ///
    /// </summary>
    /// <param name="seatIndex">The seat whose label changes.</param>
    /// <param name="label">The team label written to <c>seat+4</c>.</param>
    /// <returns>The payload.</returns>
    public static byte[] SeatTeamLabel(int seatIndex, byte label)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(seatIndex);
        w.WriteByte(label);
        w.WriteByte(0);           // the optional tail's absent tag
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 584. <c>sub_9E1EB0</c> case 0x248: one <c>i32</c> slot index written to
    /// <c>this+1664+slot</c>. Evidence: verified.
    ///
    /// </summary>
    /// <param name="slot">Seat index.</param>
    public static byte[] PlayerMapLoaded(int slot)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(slot);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 510 room-to-game stage move. Dispatcher <c>sub_9D9930</c> case 0x1FE
    /// at <c>0x9D9930</c>; record constructor <c>sub_10D1C10</c> at
    /// <c>0x10D1C10</c>; deserializer <c>sub_10D1D70</c> at <c>0x10D1D70</c>;
    /// handler <c>sub_9E5E70</c> at <c>0x9E5E70</c>. Wire order (verified widths
    /// and order, verified destinations):
    /// <list type="table">
    /// <item><term>1-3</term><description>three prefixed wstrings (<c>sub_1119C10</c> at <c>0x1119C10</c>, <c>u32</c> char count capped at 0x4000 plus UTF-16LE bytes); the handler copies them to session+184 / +208 / +232. Which is map / mode / room name is unverified.</description></item>
    /// <item><term>4</term><description><c>i32</c> at record +72; copied to session+12 by the dispatcher and later overwritten with 4 by the S2C 608 handler.</description></item>
    /// <item><term>5</term><description><c>i32</c> at record +76; copied to session+428.</description></item>
    /// <item><term>6</term><description><c>u8</c> at record +80; copied to session+484 only when the session game type is 19.</description></item>
    /// <item><term>7</term><description><c>u8</c> at record +84; bit 2 sets session+1985, bit 3 sets session+1987.</description></item>
    /// <item><term>8</term><description><c>i32</c> at record +88; copied to session+432.</description></item>
    /// <item><term>9</term><description><c>u8</c> at record +92; copied to session+498 and to the <c>sub_95F190</c> global.</description></item>
    /// <item><term>10-11</term><description>two counted lists read by <c>sub_10D2CF0</c> at <c>0x10D2CF0</c> and <c>sub_10D2B40</c> at <c>0x10D2B40</c>: <c>u32</c> count then per entry <c>u8</c>, <c>i32</c>, <c>i32</c>.</description></item>
    /// </list>
    /// Minimum body is 35 bytes (three empty wstrings, all scalars zero, both
    /// lists empty). The handler also moves the stage manager to stage 5 at
    /// <c>sub_9E5E70</c> line 699; that it is what creates the in-play session
    /// <c>dword_18E55A0</c> that S2C 607 / 608 gate on is <c>inferred</c> — the
    /// assignment itself is not pinned in this pass. Evidence: wire order
    /// and widths verified; every field meaning unverified.
    ///
    /// </summary>
    /// <param name="record">The record to serialize.</param>
    /// <returns>The body.</returns>
    public static byte[] EnterGameStage(EnterGameStageRecord record)
    {
        var w = PacketWriter.Create();
        record.Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 602 / 603 / 604 / 605 seat-info record. Reader <c>sub_10D22D0</c> at
    /// <c>0x10D22D0</c>, dispatched by <c>sub_9E1EB0</c> cases 0x25A / 0x25B /
    /// 0x25C / 0x25D and copied verbatim into session+16 / +168 / +320 / +472.
    /// Wire order (verified widths and order): <c>i32</c>, <c>i32</c>, <c>i64</c>
    /// slot mask, then one <c>u8</c> per set bit of the mask in ascending
    /// set-bit order. The client object is 152 bytes (two <c>i32</c> plus 36
    /// <c>i32</c> per-slot words) and the reader walks slots 0..35 only, so
    /// bits 36..63 of the mask are never consumed and are rejected here. Body
    /// length is <c>16 + popcount(slotMask)</c>. Identity, team and spawn are
    /// not proven for any field: <c>unknown0</c>, <c>unknown1</c> and the
    /// per-slot byte have no read site in this pass. Evidence: verified
    /// widths, order and destination offsets; meanings unverified.
    ///
    /// </summary>
    /// <param name="unknown0">First <c>i32</c> (record +0).</param>
    /// <param name="unknown1">Second <c>i32</c> (record +4).</param>
    /// <param name="slotMask">Seat bit mask; only bits 0..35 are consumed by the reader.</param>
    /// <param name="slotValues">One byte per set bit of <paramref name="slotMask"/>, ascending.</param>
    /// <returns>The <c>16 + popcount(slotMask)</c>-byte body.</returns>
    public static byte[] SeatInfo(int unknown0, int unknown1, long slotMask, ReadOnlySpan<byte> slotValues)
    {
        const int SlotCount = 36;
        const long SlotMaskLimit = 1L << SlotCount;
        if (slotMask < 0 || slotMask >= SlotMaskLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotMask),
                $"S2C 602-605 consumes seat bits 0..{SlotCount - 1} only (sub_10D22D0 loop bound); mask 0x{slotMask:X} is out of range.");
        }

        var bits = 0;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            if ((slotMask & (1L << slot)) != 0)
                bits++;
        }

        if (slotValues.Length != bits)
        {
            throw new ArgumentException(
                $"S2C 602-605 needs one byte per set bit of the mask: mask 0x{slotMask:X} sets {bits} bit(s) but {slotValues.Length} value(s) were supplied.",
                nameof(slotValues));
        }

        var w = PacketWriter.Create();
        w.WriteInt32(unknown0);
        w.WriteInt32(unknown1);
        w.WriteInt64(slotMask);
        w.WriteBytes(slotValues);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 585 start flags. <c>sub_9E1EB0</c> case 0x249 at <c>0x9E1EB0</c>:
    /// three <c>i32</c> each consumed as <c>value != 0</c>. Field 0 is stored at
    /// session+1708 and is the C2S-17 release gate the S2C 608 handler checks
    /// (<c>sub_9E7570</c> line 129); field 1 goes to session+1709 and takes part
    /// in the S2C 607 signature condition; field 2 is written through the
    /// <c>sub_95F190</c> global. Mode 1 and mode 8 are byte-identical here.
    /// Evidence: verified body, widths and destinations.
    ///
    /// </summary>
    /// <param name="flag0">Stored at session +1708; must be nonzero to release C2S 17.</param>
    /// <param name="flag1">Stored at session +1709.</param>
    /// <param name="flag2">Written through <c>sub_95F190</c>.</param>
    public static byte[] StartFlags(int flag0, int flag1, int flag2)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(flag0);
        w.WriteInt32(flag1);
        w.WriteInt32(flag2);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 607 round countdown. Handler <c>sub_9E7080</c> at <c>0x9E7080</c>
    /// (<c>sub_9E1EB0</c> case 0x25F): one <c>i32</c> milliseconds value that
    /// arms the countdown block at <c>dword_18E55A0+2128</c>. The handler
    /// early-returns while <c>dword_18E55A0</c> is null, so S2C 510 must have
    /// been delivered first. That the value is a millisecond duration is
    /// <c>inferred</c> from its countdown use; the field itself is verified.
    ///
    /// </summary>
    /// <param name="milliseconds">Timer value.</param>
    public static byte[] StartTimer(int milliseconds)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(milliseconds);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 609 round-clock unbounded setter. Handler <c>sub_9E1EB0</c> case
    /// <c>0x261</c> at <c>0x9E1EB0</c> performs <c>if (dword_18E55A0)
    /// sub_6FEFC0(0x3FFFFFFF)</c> with no stream read at all
    /// (<c>sub_9E1EB0</c>), so the body is empty. The no-limit value is
    /// the client's own sentinel, which <c>sub_6FEFC0</c> also writes as its
    /// second timer's ceiling at <c>sub_6FEFC0</c>. Evidence: verified case
    /// body (empty is proven, not guessed) and reachability.
    ///
    /// </summary>
    public static byte[] RoundClockUnbounded() => [];

    /// <summary>
    /// S2C 608 round initialize. Handler <c>sub_9E7570</c> at <c>0x9E7570</c>
    /// (<c>sub_9E1EB0</c> case 0x260): two <c>i32</c> read at
    /// <c>sub_9E7570</c>. Field 0 is passed to <c>sub_6FEFC0</c> at
    /// <c>:405</c>, which arms the round timer - <c>*(dword_18E55A0+2160)</c>
    /// and the end tick at <c>value + elapsed</c> (<c>sub_6FEFC0</c>) - so
    /// a zero arms a round that is already over. Field 1 is stored at
    /// <c>dword_18E7558</c> and is the only value the client takes from this
    /// body into the GameInfo global (<c>0x1891EC0</c>). The handler
    /// early-returns while <c>dword_18E55A0</c> is null and then writes the
    /// client's own empty C2S 17 when session+1708 is set. Evidence: verified
    /// body, widths, destinations and the timer arm; the millisecond unit is
    /// <c>inferred</c> from the same shape as <see cref="StartTimer"/>.
    ///
    /// </summary>
    /// <param name="unknown0">Round length in milliseconds; passed to <c>sub_6FEFC0</c>. A few seconds ends the round moments after it starts.</param>
    /// <param name="unknown1">Second integer, stored at <c>dword_18E7558</c>.</param>
    public static byte[] RoundInitialize(int unknown0, int unknown1)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(unknown0);
        w.WriteInt32(unknown1);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 612. <c>sub_9E2FE0</c> case 0x264: two <c>i32</c> passed to <c>sub_9E88C0</c>.
    /// Evidence: verified. The option-gated synthesis pair that can follow is
    /// <see cref="ResultBanner"/> (S2C 613, just before the S2C 615 close) and
    /// <see cref="ScoreboardData"/> (S2C 614, just after it), both only behind
    /// <c>send_scoreboard_rows</c>.
    ///
    /// </summary>
    /// <param name="unknown0">First integer.</param>
    /// <param name="unknown1">Second integer.</param>
    public static byte[] ResultOpen(int unknown0, int unknown1)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(unknown0);
        w.WriteInt32(unknown1);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// The banner-family byte <see cref="ResultBanner"/> ships: zero, the neutral draw branch. The
    /// banner image-path selection inside the S2C 613 handler (<c>sub_9E9710</c>, game type 19 behind the
    /// <c>ScoreData+456</c> gate) ends on <c>Draw.png</c> when the resolved winner is the draw slot - the only
    /// branch this emulator can claim, because the winner fields (<c>GameInfo+452/+456</c>) have no recovered
    /// writer and every synthesized body leaves them alone. No win or lose byte exists yet: that selection
    /// needs the winnerSlot outcome work.
    /// </summary>
    public const byte BannerTypeDraw = 0;

    /// <summary>
    /// S2C 613, the post-result banner step. Dispatcher <c>sub_9E2FE0</c> case
    /// <c>0x265</c>; handler <c>sub_9E9710</c>. Body (verified widths and
    /// order, <c>sub_9E2FE0</c>): two <c>i32</c> the handler never reads,
    /// then two <c>u8</c> stored at <c>GameInfo+308</c> and <c>+312</c> -
    /// meanings unverified. The handler then writes the session phase 5 -&gt; 6
    /// and, for game type 19 behind the <c>ScoreData+456</c> gate, builds the
    /// result banner image path from the winner field and the seat's own slot -
    /// draw <c>Draw.png</c>, win, lose, or the unranked WinAlpha/WinBravo pair
    /// (<c>sub_9E9710</c>) - and advances the result-stage object to state
    /// 2, the state <see cref="ResultClose"/> advances to 3. The S2C 614
    /// consumer repeats the phase write, the byte stores and the banner block
    /// verbatim (<c>sub_9E9A60</c>), so this frame ships only together
    /// with <see cref="ScoreboardData"/> behind <c>send_scoreboard_rows</c>,
    /// one step ahead of the S2C 615 (result-object state 1 from the 612, then
    /// 2 here, then 3 at the close), and it never opens the scoreboard - that
    /// is the S2C 612's notify work. Evidence: body, destinations and phase
    /// write verified; field meanings and the banner branch unverified, so
    /// every field ships zero.
    ///
    /// </summary>
    /// <param name="unknown0">First <c>i32</c>; unread by the handler.</param>
    /// <param name="unknown1">Second <c>i32</c>; unread by the handler.</param>
    /// <param name="type">First <c>u8</c>, stored at <c>GameInfo+308</c>; only
    /// <see cref="BannerTypeDraw"/> is proven safe.</param>
    /// <param name="flag">Second <c>u8</c>, stored at <c>GameInfo+312</c>;
    /// unverified, ships zero.</param>
    public static byte[] ResultBanner(int unknown0, int unknown1, byte type = BannerTypeDraw, byte flag = 0)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(unknown0);
        w.WriteInt32(unknown1);
        w.WriteByte(type);
        w.WriteByte(flag);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 615. <c>sub_9E2FE0</c> case 0x267: one <c>u8</c> passed to <c>sub_9E9EF0</c>
    /// as <c>value &amp; 1</c>. Evidence: verified.
    ///
    /// </summary>
    /// <param name="value">Close flag.</param>
    public static byte[] ResultClose(byte value)
    {
        var w = PacketWriter.Create();
        w.WriteByte(value);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 576 relay-state clear. The dispatcher case (<c>9D4790-9D4833</c> in
    /// <c>sub_9D2FF0</c>) never reads the stream: it zeroes the pending-send
    /// container at <c>dword_18E5680+436/+484</c>, sets <c>+448</c> to -1 and
    /// clears the per-player relay state, so the body is empty. Sent after the S2C 615
    /// result close.
    ///
    /// </summary>
    public static byte[] RelayStateClear() => [];

    /// <summary>
    /// S2C 620 round stage reset. The handler <c>sub_9EA690</c> (dispatcher
    /// case <c>0x26C</c>) reads nothing from the stream: it drops the session
    /// phase to 2, notifies the mode manager, tears the round session down and
    /// moves the stage manager with argument 4 - the same slot move the next
    /// S2C 117 install performs later anyway - so the body is empty and the
    /// frame is parity only. Sent after the
    /// S2C 615 result close.
    ///
    /// </summary>
    public static byte[] RoundStageReset() => [];

    /// <summary>
    /// S2C 614 scoreboard-data record. Dispatcher case <c>0x266</c>: the body
    /// is one <c>ScoreData</c> record filled by its own virtual deserializer
    /// <c>sub_429620</c> (RTTI <c>.?AVScoreData@@</c>) - rows counted by a
    /// <c>u64</c> presence mask, all inside this single body; see
    /// <see cref="ResultScoreboard"/> for field order. The
    /// frame ships only behind <c>send_scoreboard_rows</c> because the
    /// client-side rendering of synthesized rows is unverified. Emitted after
    /// the S2C 615 and before the S2C 576; the <see cref="ResultBanner"/> that
    /// shares its option runs one step earlier, before the close, because the
    /// 614 consumer repeats the 613 phase/banner work verbatim
    /// (<c>sub_9E9A60</c>) and the result-object state must stay
    /// monotonic across the 615.
    ///
    /// </summary>
    /// <param name="record">The scoreboard record to serialize.</param>
    public static byte[] ScoreboardData(ResultScoreboard record)
    {
        var w = PacketWriter.Create();
        record.Write(w);
        return WireUtil.Written(w);
    }
}

/// <summary>
/// One entry of a counted list inside the S2C 510 record. Readers
/// <c>sub_10D2CF0</c> at <c>0x10D2CF0</c> (list at record +96) and
/// <c>sub_10D2B40</c> at <c>0x10D2B40</c> (list at record +112) both read a
/// <c>u32</c> count and then, per entry, a <c>u8</c> followed by two
/// <c>i32</c>. Evidence: verified widths and order; no read site ties the
/// key or either integer to a meaning, so all three stay unverified.
///
/// </summary>
/// <param name="Unknown0">Leading <c>u8</c> key.</param>
/// <param name="Unknown1">First <c>i32</c>.</param>
/// <param name="Unknown2">Second <c>i32</c>.</param>
public sealed record StartStageEntry(byte Unknown0, int Unknown1, int Unknown2);

/// <summary>
/// The S2C 510 record deserialized by <c>sub_10D1D70</c> at <c>0x10D1D70</c> and applied by
/// <c>sub_9E5E70</c> at <c>0x9E5E70</c>. The field names below are positional: the client code proves the widths,
/// the order and the session offsets the handler writes to, but not what any field means. See <see
/// cref="GameToClient.EnterGameStage"/> for the full table. </summary> <param name="Unknown0">First prefixed
/// wstring (record +0, session+208).</param> <param name="Unknown1">Second prefixed wstring (record +24,
/// session+232).</param> <param name="Unknown2">Third prefixed wstring (record +48, session+184).</param>
/// <param name="Unknown3"><c>i32</c> at record +72; copied to session+12.</param> <param
/// name="Unknown4"><c>i32</c> at record +76; copied to session+428.</param> <param name="Unknown5"><c>u8</c> at
/// record +80; copied to session+484 for game type 19.</param> <param name="Unknown6"><c>u8</c> at record +84;
/// bits 2 and 3 set session+1985 and +1987.</param> <param name="Unknown7"><c>i32</c> at record +88; copied to
/// session+432.</param> <param name="Unknown8"><c>u8</c> at record +92; copied to session+498 and the
/// <c>sub_95F190</c> global.</param> <param name="Unknown9">Counted <c>u8</c>/<c>i32</c>/<c>i32</c> list at
/// record +96.</param> <param name="Unknown10">Counted <c>i32</c>/<c>i32</c>/<c>i32</c> list at record +104;
/// its reader <c>sub_10D30B0</c> takes no leading byte.</param> <param name="Unknown11">Counted
/// <c>u8</c>/<c>i32</c>/<c>i32</c> list at record +112.</param>
public sealed record EnterGameStageRecord(
    string Unknown0,
    string Unknown1,
    string Unknown2,
    int Unknown3,
    int Unknown4,
    byte Unknown5,
    byte Unknown6,
    int Unknown7,
    byte Unknown8,
    IReadOnlyList<StartStageEntry> Unknown9,
    IReadOnlyList<StartStageTriple> Unknown10,
    IReadOnlyList<StartStageEntry> Unknown11)
{
    /// <summary>
    /// The all-zero form: three empty wstrings, every scalar zero and all three counted lists empty,
    /// which serializes to the 39-byte minimum body. The client allocates three list heads - object +96, +104
    /// and +112 (<c>sub_10D1C10</c>) - and reads all three (<c>sub_10D1D70</c>), so two lists leave
    /// the body four bytes short of the reader's minimum.
    /// </summary>
    public static EnterGameStageRecord Default { get; } = new(
        string.Empty, string.Empty, string.Empty,
        0, 0, 0, 0, 0, 0,
        Array.Empty<StartStageEntry>(), Array.Empty<StartStageTriple>(), Array.Empty<StartStageEntry>());

    /// <summary>
    /// Builds the record for one map: the three map-selection keys all carry <paramref
    /// name="mapName"/>, the order-4 <c>i32</c> carries the room state, and every other scalar and list stays
    /// at the all-zero form. The S2C 510 handler <c>sub_9E5E70</c> copies those three wstrings into
    /// <c>session+184</c> / <c>+208</c> / <c>+232</c> (<c>sub_9E5E70</c> then <c>:180-203</c>), which are
    /// the <c>MapMeta</c> slots <c>unknown44</c> / <c>unknown68</c> / <c>unknown92</c> - so an empty triple
    /// erases whatever map the S2C 117 install wrote. The dispatcher additionally copies the order-4 <c>i32</c>
    /// into <c>session+12</c> (<c>sub_9D9930</c>, <c>v19[18]</c> = record +72), the room state five UI gates
    /// test as equal to four (<c>sub_96F3A0,138</c>; <c>sub_966CF0</c>; <c>sub_966E00</c>;
    /// <c>sub_975D50,204</c>; <c>sub_96DBC0,65</c>), and only S2C 608 restores it afterwards - so a zero
    /// here erases the room state for the whole scene. Evidence: verified copy sites and gates.
    /// </summary>
    /// <param name="mapName">The map record key to carry in all three slots.</param> <param
    /// name="roomState">The room state written to <c>session+12</c>; four is the in-room value.</param>
    public static EnterGameStageRecord ForMap(string mapName, int roomState) => Default with
    {
        Unknown0 = mapName,
        Unknown1 = mapName,
        Unknown2 = mapName,
        Unknown3 = roomState,
    };

    /// <summary>
    /// Builds the record for a mission round (the pirate battle): the mission key in the first and
    /// third slots, the stage it loads in the second, and the mode in <see cref="Unknown3"/>.
    /// <c>sub_9E5E70</c> tests the first slot's <c>MapMeta</c> <c>isHordeModeMap</c> /
    /// <c>isPirateAssault</c> flag to enter the NPC stage; the layout and the mode value
    /// are not yet traced to a reader.
    ///
    /// </summary>
    /// <param name="missionKey">The mission map key, such as <c>npc_Village07_Easy01</c>.</param>
    /// <param name="stageKey">The stage the mission loads, such as <c>rel_Village07_Easy02</c>.</param>
    /// <param name="mode">The room's mode id.</param>
    public static EnterGameStageRecord ForMission(string missionKey, string stageKey, int mode) => Default with
    {
        Unknown0 = missionKey,
        Unknown1 = stageKey,
        Unknown2 = missionKey,
        Unknown3 = mode,
    };

    /// <summary>Writes the record in wire order.</summary>
    /// <param name="w">The destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteWideStringPrefixed(Unknown0);
        w.WriteWideStringPrefixed(Unknown1);
        w.WriteWideStringPrefixed(Unknown2);
        w.WriteInt32(Unknown3);
        w.WriteInt32(Unknown4);
        w.WriteByte(Unknown5);
        w.WriteByte(Unknown6);
        w.WriteInt32(Unknown7);
        w.WriteByte(Unknown8);
        WriteList(w, Unknown9);
        WriteTripleList(w, Unknown10);
        WriteList(w, Unknown11);
    }

    private static void WriteList(PacketWriter w, IReadOnlyList<StartStageEntry> entries)
    {
        w.WriteUInt32((uint)entries.Count);
        foreach (var entry in entries)
        {
            w.WriteByte(entry.Unknown0);
            w.WriteInt32(entry.Unknown1);
            w.WriteInt32(entry.Unknown2);
        }
    }

    private static void WriteTripleList(PacketWriter w, IReadOnlyList<StartStageTriple> entries)
    {
        w.WriteUInt32((uint)entries.Count);
        foreach (var entry in entries)
        {
            w.WriteInt32(entry.Unknown0);
            w.WriteInt32(entry.Unknown1);
            w.WriteInt32(entry.Unknown2);
        }
    }
}

/// <summary>
/// One element of the S2C 510 record's middle counted list (record +104). Its reader
/// <c>sub_10D30B0</c> (reached through <c>sub_10D2220</c> at <c>sub_10D1D70</c>) reads three <c>i32</c> with
/// no leading byte, unlike the two outer lists whose reader <c>sub_10D2CF0</c> reads a <c>u8</c> first. All
/// values are zero because no field has a traced reader. </summary> <param name="Unknown0">First
/// <c>i32</c>.</param> <param name="Unknown1">Second <c>i32</c>.</param> <param name="Unknown2">Third
/// <c>i32</c>.</param>
public sealed record StartStageTriple(int Unknown0, int Unknown1, int Unknown2);
