using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.RoomCore;

/// <summary>One <c>{i32, i32}</c> pair from <c>MapMeta</c> vector <c>kr-1256 sub_113FE90</c>.</summary>
/// <param name="Unknown0">First integer.</param>
/// <param name="Unknown1">Second integer.</param>
public readonly record struct MapMetaPair(int Unknown0, int Unknown1);

/// <summary>One <c>{i32, prefixed wstring}</c> from <c>MapMeta</c> vector <c>kr-1256 sub_10C9350</c>.</summary>
/// <param name="Unknown0">Key integer.</param>
/// <param name="Unknown1">Prefixed wstring.</param>
public readonly record struct MapMetaKeyedString(int Unknown0, string Unknown1);

/// <summary>One <c>{u16, vec&lt;i32&gt;}</c> from <c>MapMeta</c> vector <c>kr-1256 sub_10D2EA0</c>.</summary>
/// <param name="Unknown0">Leading <c>u16</c>.</param>
/// <param name="Unknown1">Counted <c>i32</c> vector (<c>kr-1256 sub_998FB0</c>).</param>
public readonly record struct MapMetaU16Ints(ushort Unknown0, int[] Unknown1);

/// <summary>
/// Nested <c>MapMeta</c> blob. Reader <c>kr-1256 sub_113D030</c> at <c>0x113D030</c>. Always
/// present after <c>SessionSetting.kind</c>. Evidence: verified wire order.
///
/// </summary>
public sealed record MapMetaBlob(
    int Unknown0,
    ushort Unknown4,
    int Unknown8,
    int Unknown20,
    int Unknown24,
    int Unknown28,
    int Unknown32,
    int Unknown36,
    int Unknown40,
    string Unknown44,
    string Unknown68,
    string Unknown92,
    string Unknown116,
    int Unknown140,
    string Unknown144,
    string Unknown168,
    string Unknown192,
    string Unknown216,
    string Unknown240,
    string Unknown264,
    int Unknown288,
    int Unknown292,
    int Unknown296,
    MapMetaPair[] Unknown297,
    int Unknown309,
    MapMetaKeyedString[] Unknown310,
    int[] Unknown322,
    MapMetaU16Ints[] Unknown328,
    int Unknown340,
    int Unknown341,
    byte Unknown344,
    int Unknown348,
    int Unknown352,
    string Unknown356,
    string Unknown380,
    int Unknown404)
{
    /// <summary>
    /// Gets whether every string is empty, every count is zero, and <see cref="Unknown4"/>
    /// is zero. That empty form is valid on the main-menu kind-2 path; create-room must not
    /// use it when the client dialog ships a mode selection.
    ///
    /// </summary>
    public bool IsEmpty =>
        Unknown4 == 0
        && Unknown0 == 0        && string.IsNullOrEmpty(Unknown44)
        && string.IsNullOrEmpty(Unknown68)
        && string.IsNullOrEmpty(Unknown92)
        && string.IsNullOrEmpty(Unknown116)
        && string.IsNullOrEmpty(Unknown144)
        && string.IsNullOrEmpty(Unknown168)
        && string.IsNullOrEmpty(Unknown192)
        && string.IsNullOrEmpty(Unknown216)
        && string.IsNullOrEmpty(Unknown240)
        && string.IsNullOrEmpty(Unknown264)
        && string.IsNullOrEmpty(Unknown356)
        && string.IsNullOrEmpty(Unknown380)
        && Unknown297.Length == 0
        && Unknown310.Length == 0
        && Unknown328.Length == 0;

    /// <summary>Reads one <c>MapMeta</c> from an open reader.</summary>
    /// <param name="r">The reader.</param>
    public static MapMetaBlob Read(ref PacketReader r)
    {
        var unknown0 = r.ReadInt32();
        var unknown4 = r.ReadUInt16();
        var unknown8 = r.ReadInt32();
        var unknown20 = r.ReadInt32();
        var unknown24 = r.ReadInt32();
        var unknown28 = r.ReadInt32();
        var unknown32 = r.ReadInt32();
        var unknown36 = r.ReadInt32();
        var unknown40 = r.ReadInt32();
        var unknown44 = WideShapes.ReadWide(ref r, "MapMeta.unknown44");
        var unknown68 = WideShapes.ReadWide(ref r, "MapMeta.unknown68");
        var unknown92 = WideShapes.ReadWide(ref r, "MapMeta.unknown92");
        var unknown116 = WideShapes.ReadWide(ref r, "MapMeta.unknown116");
        var unknown140 = r.ReadInt32();
        var unknown144 = WideShapes.ReadWide(ref r, "MapMeta.unknown144");
        var unknown168 = WideShapes.ReadWide(ref r, "MapMeta.unknown168");
        var unknown192 = WideShapes.ReadWide(ref r, "MapMeta.unknown192");
        var unknown216 = WideShapes.ReadWide(ref r, "MapMeta.unknown216");
        var unknown240 = WideShapes.ReadWide(ref r, "MapMeta.unknown240");
        var unknown264 = WideShapes.ReadWide(ref r, "MapMeta.unknown264");
        var unknown288 = r.ReadInt32();
        var unknown292 = r.ReadInt32();
        var unknown296 = r.ReadInt32();
        var pairCount = r.ReadUInt32();
        WideShapes.RequireCount(pairCount, "MapMeta.unknown297");
        var pairs = new MapMetaPair[pairCount];
        for (var i = 0; i < pairs.Length; i++)
            pairs[i] = new MapMetaPair(r.ReadInt32(), r.ReadInt32());
        var unknown309 = r.ReadInt32();
        var keyedCount = r.ReadUInt32();
        WideShapes.RequireCount(keyedCount, "MapMeta.unknown310");
        var keyed = new MapMetaKeyedString[keyedCount];
        for (var i = 0; i < keyed.Length; i++)
            keyed[i] = new MapMetaKeyedString(r.ReadInt32(), WideShapes.ReadWide(ref r, "MapMeta.unknown310.string"));
        var flags = new int[5];
        for (var i = 0; i < flags.Length; i++)
            flags[i] = r.ReadInt32();
        var u16Count = r.ReadUInt32();
        WideShapes.RequireCount(u16Count, "MapMeta.unknown328");
        var u16Rows = new MapMetaU16Ints[u16Count];
        for (var i = 0; i < u16Rows.Length; i++)
            u16Rows[i] = new MapMetaU16Ints(r.ReadUInt16(), WideShapes.ReadInt32Vector(ref r, "MapMeta.unknown328.vec"));
        var unknown340 = r.ReadInt32();
        var unknown341 = r.ReadInt32();
        var unknown344 = r.ReadByte();
        var unknown348 = r.ReadInt32();
        var unknown352 = r.ReadInt32();
        var unknown356 = WideShapes.ReadWide(ref r, "MapMeta.unknown356");
        var unknown380 = WideShapes.ReadWide(ref r, "MapMeta.unknown380");
        var unknown404 = r.ReadInt32();
        return new MapMetaBlob(
            unknown0, unknown4, unknown8, unknown20, unknown24, unknown28, unknown32, unknown36, unknown40,
            unknown44, unknown68, unknown92, unknown116, unknown140,
            unknown144, unknown168, unknown192, unknown216, unknown240, unknown264,
            unknown288, unknown292, unknown296, pairs, unknown309, keyed, flags, u16Rows,
            unknown340, unknown341, unknown344, unknown348, unknown352, unknown356, unknown380, unknown404);
    }

    /// <summary>Writes this blob in <c>kr-1256 sub_113CCE0</c> order.</summary>
    /// <param name="w">Destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteInt32(Unknown0);
        w.WriteUInt16(Unknown4);
        w.WriteInt32(Unknown8);
        w.WriteInt32(Unknown20);
        w.WriteInt32(Unknown24);
        w.WriteInt32(Unknown28);
        w.WriteInt32(Unknown32);
        w.WriteInt32(Unknown36);
        w.WriteInt32(Unknown40);
        w.WriteWideStringPrefixed(Unknown44);
        w.WriteWideStringPrefixed(Unknown68);
        w.WriteWideStringPrefixed(Unknown92);
        w.WriteWideStringPrefixed(Unknown116);
        w.WriteInt32(Unknown140);
        w.WriteWideStringPrefixed(Unknown144);
        w.WriteWideStringPrefixed(Unknown168);
        w.WriteWideStringPrefixed(Unknown192);
        w.WriteWideStringPrefixed(Unknown216);
        w.WriteWideStringPrefixed(Unknown240);
        w.WriteWideStringPrefixed(Unknown264);
        w.WriteInt32(Unknown288);
        w.WriteInt32(Unknown292);
        w.WriteInt32(Unknown296);
        w.WriteUInt32((uint)Unknown297.Length);
        foreach (var pair in Unknown297)
        {
            w.WriteInt32(pair.Unknown0);
            w.WriteInt32(pair.Unknown1);
        }

        w.WriteInt32(Unknown309);
        w.WriteUInt32((uint)Unknown310.Length);
        foreach (var row in Unknown310)
        {
            w.WriteInt32(row.Unknown0);
            w.WriteWideStringPrefixed(row.Unknown1);
        }

        if (Unknown322.Length != 5)
            throw new InvalidOperationException("MapMeta.unknown322 must be five i32-as-bool values.");
        foreach (var flag in Unknown322)
            w.WriteInt32(flag);
        w.WriteUInt32((uint)Unknown328.Length);
        foreach (var row in Unknown328)
        {
            w.WriteUInt16(row.Unknown0);
            WideShapes.WriteInt32Vector(w, row.Unknown1);
        }

        w.WriteInt32(Unknown340);
        w.WriteInt32(Unknown341);
        w.WriteByte(Unknown344);
        w.WriteInt32(Unknown348);
        w.WriteInt32(Unknown352);
        w.WriteWideStringPrefixed(Unknown356);
        w.WriteWideStringPrefixed(Unknown380);
        w.WriteInt32(Unknown404);
    }

    /// <summary>Builds the all-zero / all-empty form that the main-menu writer uses.</summary>
    public static MapMetaBlob Empty() => new(
        0, 0, 0, 0, 0, 0, 0, 0, 0,
        string.Empty, string.Empty, string.Empty, string.Empty, 0,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        0, 0, 0, [], 0, [], [0, 0, 0, 0, 0], [],
        0, 0, 0, 0, 0, string.Empty, string.Empty, 0);

    /// <summary>
    /// Builds the mode-40 single-player training-room form of the
    /// <c>MapMeta</c>: the all-zero / all-empty form with the mode in
    /// <see cref="Unknown4"/> and the training map repeated in the four
    /// map-selection strings (orders 10-13: the selected, current, difficulty
    /// and scene map). Repeating the key keeps the later scene-bootstrap
    /// lookup in the same map registry. The four assignments are existing
    /// compatibility values, not independently verified field meanings. Only
    /// <see cref="Unknown0"/> and <see cref="Unknown20"/> stay cleared because
    /// <c>kr-1256 sub_97C610</c> and <c>kr-1256 sub_9201D0</c> gate on them.
    ///
    /// </summary>
    /// <param name="mode">The mode id the kind-2 branch compares against 40.</param>
    /// <param name="mapName">The training map record key.</param>
    public static MapMetaBlob ForTrainingRoom(ushort mode, string mapName) => Empty() with
    {
        Unknown4 = mode,
        Unknown44 = mapName,
        Unknown68 = mapName,
        Unknown92 = mapName,
        Unknown116 = mapName,
    };

    /// <summary>
    /// Builds the room-session form of a client-supplied <c>MapMeta</c>: the mode (<see
    /// cref="Unknown4"/>) and the map-selection strings are kept, while the three fields the room-stage gates
    /// read are forced to their required values. </summary> <param name="source">The <c>MapMeta</c> the client
    /// sent on C2S 402.</param> <remarks> The <c>MapMeta</c> constructor plants two self-pointers
    /// (<c>kr-1256 sub_113C3E0</c>): <c>*(a1+12) = a1+4</c> and <c>*(a1+16) = a1</c>. Because the blob is embedded
    /// at <c>SessionSetting+132</c>, those make <c>session+152</c> resolve to <c>MapMeta+4</c> (the mode) and
    /// <c>session+156</c> to <c>MapMeta+0</c> — the flags word. So <see cref="Unknown0"/> is the flags dword
    /// <c>kr-1256 sub_97C610</c> and <c>kr-1256 sub_9201D0</c> both gate on (bits <c>0x2, 0x8, 0x10, 0x40, 0x400, 0x800,
    /// 0x1000, 0x2000, 0x8000, 0x20000</c> must be clear), and <see cref="Unknown20"/> is the dword both
    /// require to be zero. Clearing them here is what lets the member panel be constructed and the seat avatar
    /// spawn. <para> <see cref="Unknown8"/> (wire order 3, the <c>i32</c>-as-bool at <c>MapMeta+8</c> =
    /// <c>session+148</c>) is the third gate input. It is the outer condition of the room-state chrome refresh
    /// <c>kr-1256 sub_96F3A0</c>: when it is nonzero and the local seat index read through
    /// <c>kr-1256 sub_9E1580</c> equals the distinguished seat the seat holder at <c>session+576</c> carries, that
    /// refresh clears the enabled byte at <c>+908</c> on <c>btnRoomChangeTeam_Blue</c>
    /// (<c>roomstage+0x638</c>), <c>btnRoomChangeTeam_Red</c> (<c>roomstage+0x63C</c>) and <c>btnRoomReady</c>
    /// (<c>roomstage+0x644</c>). The same dword also selects <c>pnRefereeMark</c> over <c>pnMasterMark</c> for
    /// the host seat (<c>kr-1256 sub_980730</c>) and takes the referee branch of the spawner
    /// (<c>kr-1256 sub_9201D0</c>). A waiting room created from the client dialog carries the ordinary value 0, so
    /// clearing it keeps the reply inside the one shape the client code proves for a normal room. Evidence: verified
    /// read sites; the semantics of the bit itself are unverified, so 0 - the value a live client sends on C2S
    /// 402 - is the only safe emission. </para> </remarks>
    public static MapMetaBlob ForRoom(MapMetaBlob source) => source with
    {
        Unknown0 = 0,
        Unknown8 = 0,
        Unknown20 = 0,
    };

    /// <summary>
    /// Builds the room-list-row form of this <c>MapMeta</c> for the S2C 101 snapshot. The
    /// compatibility form uses a zero flags word, the create mode in the <c>u16</c> slot, and the channel key.
    /// The order-21 <c>i32</c> (<see cref="Unknown288"/>) stays zero
    /// and the client reads the slot only on hoisted rows, which this server never sends. Every other slot is
    /// the all-zero / all-empty form. </summary> <param name="mode">The create mode id the row label
    /// reads.</param> <param name="channel">The channel / group match key.</param>
    public static MapMetaBlob ForRoomListRow(ushort mode, int channel) => new(
        0, mode, 0, channel, 0, 0, 0, 0, 0,
        string.Empty, string.Empty, string.Empty, string.Empty, 0,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        0, 0, 0, [], 0, [], [0, 0, 0, 0, 0], [],
        0, 0, 0, 0, 0, string.Empty, string.Empty, 0);
}

/// <summary>
/// Leading <c>SessionSetting</c> of S2C 117 and C2S 402. Reader <c>kr-1256 sub_113BD20</c> at
/// <c>0x113BD20</c>; writer twin <c>kr-1256 sub_113BAB0</c> at <c>0x113BAB0</c>. <c>MapMeta</c> sits on the wire
/// immediately after <see cref="Kind"/>. Evidence: verified. </summary> <param name="Unknown0">Leading
/// <c>u16</c>. Rooms carry the room id here: the client returns a list row's value as the C2S 403 room id
/// (<c>kr-1256 sub_854C90</c>), and the room install carries the room number as
/// well.</param> <param name="Unknown4">Following <c>i32</c>: the session / room state at object +4
/// (<c>session+12</c>). Room sessions carry <see cref="SessionSettingBlob.RoomStateInRoom"/>; the room-list row
/// renderer maps the state enum.</param> <param name="Unknown8">Prefixed wstring.</param> <param
/// name="Unknown32">First of three <c>u16</c> slots.</param> <param name="Unknown34">Second <c>u16</c>.</param>
/// <param name="Unknown36">Third <c>u16</c>.</param> <param name="Unknown38"><c>i32</c>-as-bool.</param> <param
/// name="Unknown40">Prefixed wstring.</param> <param name="Kind">Session kind at object +64. Kind 2 is the
/// main-menu character session.</param> <param name="Map"><c>MapMeta</c> blob.</param> <param
/// name="Unknown68"><c>i32</c> after the map.</param> <param name="Unknown72">Packed three-bit
/// <c>i32</c>.</param> <param name="Unknown75">Packed three-bit <c>i32</c>.</param> <param
/// name="Unknown80"><c>i64</c>.</param> <param name="Unknown88">Prefixed wstring.</param> <param
/// name="Unknown112"><c>i32</c>-as-bool.</param> <param name="Unknown116"><c>i32</c>.</param> <param
/// name="Unknown120"><c>i32</c>.</param> <param name="Unknown124"><c>i32</c>.</param> <param
/// name="Unknown128">Trailing <c>u16</c>.</param>
public sealed record SessionSettingBlob(
    ushort Unknown0,
    int Unknown4,
    string Unknown8,
    ushort Unknown32,
    ushort Unknown34,
    ushort Unknown36,
    int Unknown38,
    string Unknown40,
    int Kind,
    MapMetaBlob Map,
    int Unknown68,
    int Unknown72,
    int Unknown75,
    long Unknown80,
    string Unknown88,
    int Unknown112,
    int Unknown116,
    int Unknown120,
    int Unknown124,
    ushort Unknown128)
{
    /// <summary>Main-menu character session kind. Lobby owns this value on the login 117.</summary>
    public const int MainMenuKind = 2;

    /// <summary>
    /// Kind of the S2C 117 that returns a plaza player to the main menu after C2S 105: case 1 of
    /// the <c>kr-1256 sub_9AC490</c> kind switch (<c>0x9AC9E4</c>). Distinct from the login-time
    /// <see cref="MainMenuKind"/>. The return to the main menu carries kind 1.
    ///
    /// </summary>
    public const int MenuReturnKind = 1;

    /// <summary>
    /// Plaza session kind. The S2C 117 handler's kind switch
    /// (<c>jpt_9AC9DD</c> in <c>kr-1256 sub_9AC490</c>, case 3 at
    /// <c>kr-1256 sub_9AC490</c> <c>0x9ACA19</c>) pushes stage id 3, which the stage
    /// table <c>kr-1256 sub_87C990</c> case 3 resolves to <c>kr-1256 sub_84F820</c> ->
    /// <c>LobbyStage</c> ctor <c>kr-1256 sub_84F8D0</c> — the same move S2C 116
    /// <c>gspGotoLobby</c> (<c>kr-1256 sub_9AC2C0</c>) requests. Evidence: verified
    /// instruction pair at <c>0x9ACA52</c>-<c>0x9ACA58</c> plus the stage-table
    /// case 3 (<c>kr-1256 sub_87C990</c>).
    ///
    /// </summary>
    public const int PlazaKind = 3;

    /// <summary>
    /// Room state 4: the value at <see cref="Unknown4"/> (wire order 2, object +4, <c>session+12</c>)
    /// that makes the S2C 117 handler's kind-2 branch jump straight to the stage-4 move. <c>kr-1256 sub_9AC490</c>
    /// tests it at <c>kr-1256 sub_9AC490</c> <c>0x9ACAB4</c> (<c>cmp dword ptr [eax+0Ch],4</c>) and branches to
    /// <c>0x9ACB70</c> (<c>push 4; call [eax+1Ch]</c> = <c>SetStage(4)</c>) at <c>0x9ACAB8</c>. Any other value
    /// leaves the move at the mercy of the client's mode: <c>kr-1256 sub_11511C0</c> returns true
    /// for modes 15 / 103 / 36 and <c>0x9ACB62</c> compares the mode against 40, and both paths skip the stage
    /// move entirely. Evidence: verified instruction pair; the value 4 is a live member of the state enum (the
    /// S2C 608 handler overwrites <c>session+12</c> with 4 during the start chain), its exact label is
    /// unverified.
    /// </summary>
    public const int RoomStateInRoom = 4;

    /// <summary>
    /// The room state a waiting room carries. The room-list row renderer maps <c>2</c>/<c>3</c> to
    /// <c>aD_1</c> and gives <c>2</c> the normal row colour while anything else greys the row, and
    /// <c>kr-1256 sub_975D50,204</c> enables the room-stage controls only when the state is <c>!= 4</c>. State 4
    /// is the in-match state, so a room that carries it has its team-change and start controls disabled.
    ///
    /// </summary>
    public const int RoomStateWaiting = 2;

    /// <summary>
    /// Builds one S2C 101 room-list row for a live room. The row carries the row state 2 (waiting -
    /// the join gate and row colour key on exactly 2, so it stays even though the official rows carry 3), the
    /// room name in the third wstring, the occupant count in the first <c>u16</c> slot, zero in the second (the
    /// official rows carry 0), the seat capacity in the third, the master nickname in the eighth wstring, and
    /// <c>kind</c> 2. The client count widget renders <c>(+32 - +34) / +36</c> for every mode other than 34/36,
    /// so only this order shows the real occupancy over capacity. The nested <c>MapMeta</c> carries a zero
    /// flags word, the create mode in its <c>u16</c> slot, and the channel key. </summary> <param
    /// name="roomName">The room name the host typed on create.</param> <param name="masterNickname">Current
    /// room master nickname; 0x63C240 binds this field to lbRoomMaster.</param> <param name="mode">The create
    /// mode id (MapMeta order 2).</param> <param name="channel">The channel / group key (MapMeta order
    /// 4).</param> <param name="occupants">Players currently seated.</param> <param name="capacity">Seat
    /// capacity for the mode.</param> <param name="roomId"> The room id echoed back as <see cref="Unknown0"/>
    /// on a row click: the client returns C2S 403 field 1 from this slot (<c>kr-1256 sub_854C90</c> reads it at
    /// <c>kr-1256 sub_854C90</c> and writes it at <c>:319-320</c>), so a zero there leaves the server unable to
    /// resolve which room was clicked. Nothing else on the list path reads <c>+0</c>, so the room id is
    /// side-effect free. </param> <param name="hasPassword"> Whether the room was created with a password.
    /// Written to <see cref="Unknown38"/>, the flag <c>kr-1256 sub_962200</c> sets for a non-empty password
    /// (<c>kr-1256 sub_962200</c>); the row click opens the password prompt only when it is set
    /// (<c>kr-1256 sub_854C90</c>). </param> <param name="matchInProgress">Whether to show an active match instead of
    /// a joinable waiting room.</param>
    public static SessionSettingBlob ForRoomListRow(
        string roomName,
        string masterNickname,
        ushort mode,
        int channel,
        int occupants,
        int capacity,
        ushort roomId,
        bool hasPassword = false,
        bool matchInProgress = false) => new(
             roomId,
             matchInProgress ? 4 : 2,
            roomName,
            checked((ushort)occupants),
            0,
            checked((ushort)capacity),
            hasPassword ? 1 : 0,
            masterNickname,
            MainMenuKind,
            MapMetaBlob.ForRoomListRow(mode, channel),
            0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0);

    /// <summary>
    /// Builds the room-session form of a client-supplied <see cref="SessionSettingBlob"/>:
    /// <see cref="Kind"/> normalized to the room-stage gate, <see cref="Unknown4"/> set to
    /// <see cref="RoomStateInRoom"/>, and the three <see cref="Map"/> fields the room-stage
    /// and member-panel gates read cleared. Every value is a client-proven requirement
    /// of the S2C 117 kind-2 branch, not a tunable.
    ///
    /// </summary>
    /// <param name="source">The client's setting, echoed as the room session.</param>
    /// <param name="roomState">
    /// The state at <see cref="Unknown4"/>, i.e. <c>session+12</c>. Defaults to
    /// <see cref="RoomStateInRoom"/> because the kind-2 branch compares it
    /// against 4 at <c>kr-1256 sub_9AC490</c>. <see cref="RoomStateWaiting"/> is the
    /// value a waiting room carries and the only one under which
    /// <c>kr-1256 sub_975D50</c> enables the team-change and start controls, so it is
    /// selectable - but it takes the other branch of the kind-2 switch, and
    /// whether the member panel still constructs there is untested.
    /// </param>
    /// <remarks>
    /// <see cref="RoomStateInRoom"/> is what makes the kind-2 branch take the
    /// <c>SetStage(4)</c> path unconditionally (<c>kr-1256 sub_9AC490</c>
    /// <c>0x9ACAB4</c>); <see cref="MapMetaBlob.ForRoom"/> clears the flags
    /// word, the order-3 dword and the order-4 dword that <c>kr-1256 sub_97C610</c>,
    /// <c>kr-1256 sub_9201D0</c> and <c>kr-1256 sub_96F3A0</c> all gate on. Without the state
    /// the member panel is never constructed and the S2C 118 rows are dropped
    /// by <c>kr-1256 sub_9ACD00</c>'s <c>RoomStage8vs8</c> cast.
    /// </remarks>
    public static SessionSettingBlob ForRoom(SessionSettingBlob source, int roomState = RoomStateInRoom) => source with
    {
        Kind = MainMenuKind,
        Unknown4 = roomState,
        Map = MapMetaBlob.ForRoom(source.Map),
    };

    /// <summary>
    /// Builds the plaza-session form of a <see cref="SessionSettingBlob"/>:
    /// <see cref="PlazaKind"/>, an empty <c>MapMeta</c>, and zero everywhere
    /// else. The kind-3 branch of the S2C 117 handler reads only <c>kind</c>
    /// (the stage switch at <c>kr-1256 sub_9AC490</c> <c>0x9ACA52</c>) and the third
    /// wstring (copied into the live stage object's string slot at
    /// <c>0x9ACA22</c>); <c>SessionSetting.unknown4</c> is read only by the
    /// kind-2 branch (<c>0x9ACAB4</c>) and the <c>MapMeta</c> flags word only
    /// inside that same branch, while <c>kr-1256 sub_9D9250</c> fixes the seat count at
    /// 36 for kind 3 without consulting the map. C2S 106 carries an empty body,
    /// so there is no client plaza setting to echo and the empty form is the
    /// only honest value. Evidence: verified reads of <c>kr-1256 sub_9AC490</c> and
    /// <c>kr-1256 sub_9D9250</c>; what the client does with the copied string is
    /// unverified.
    ///
    /// </summary>
    public static SessionSettingBlob ForPlaza() => new(
        0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
        PlazaKind, MapMetaBlob.Empty(),
        0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0);

    /// <summary>Reads one <c>SessionSetting</c> plus nested <c>MapMeta</c>.</summary>
    /// <param name="r">The reader.</param>
    public static SessionSettingBlob Read(ref PacketReader r)
    {
        var unknown0 = r.ReadUInt16();
        var unknown4 = r.ReadInt32();
        var unknown8 = WideShapes.ReadWide(ref r, "SessionSetting.unknown8");
        var unknown32 = r.ReadUInt16();
        var unknown34 = r.ReadUInt16();
        var unknown36 = r.ReadUInt16();
        var unknown38 = r.ReadInt32();
        var unknown40 = WideShapes.ReadWide(ref r, "SessionSetting.unknown40");
        var kind = r.ReadInt32();
        var map = MapMetaBlob.Read(ref r);
        var unknown68 = r.ReadInt32();
        var unknown72 = r.ReadInt32();
        var unknown75 = r.ReadInt32();
        var unknown80 = r.ReadInt64();
        var unknown88 = WideShapes.ReadWide(ref r, "SessionSetting.unknown88");
        var unknown112 = r.ReadInt32();
        var unknown116 = r.ReadInt32();
        var unknown120 = r.ReadInt32();
        var unknown124 = r.ReadInt32();
        var unknown128 = r.ReadUInt16();
        return new SessionSettingBlob(
            unknown0, unknown4, unknown8, unknown32, unknown34, unknown36, unknown38, unknown40,
            kind, map, unknown68, unknown72, unknown75, unknown80, unknown88, unknown112,
            unknown116, unknown120, unknown124, unknown128);
    }

    /// <summary>Writes this blob in <c>kr-1256 sub_113BAB0</c> order.</summary>
    /// <param name="w">Destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteUInt16(Unknown0);
        w.WriteInt32(Unknown4);
        w.WriteWideStringPrefixed(Unknown8);
        w.WriteUInt16(Unknown32);
        w.WriteUInt16(Unknown34);
        w.WriteUInt16(Unknown36);
        w.WriteInt32(Unknown38);
        w.WriteWideStringPrefixed(Unknown40);
        w.WriteInt32(Kind);
        Map.Write(w);
        w.WriteInt32(Unknown68);
        w.WriteInt32(Unknown72);
        w.WriteInt32(Unknown75);
        w.WriteInt64(Unknown80);
        w.WriteWideStringPrefixed(Unknown88);
        w.WriteInt32(Unknown112);
        w.WriteInt32(Unknown116);
        w.WriteInt32(Unknown120);
        w.WriteInt32(Unknown124);
        w.WriteUInt16(Unknown128);
    }
}
