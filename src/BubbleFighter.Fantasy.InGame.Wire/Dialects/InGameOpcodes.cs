namespace BubbleFighter.Fantasy.InGame.Dialects;

/// <summary>
/// Wire opcodes for in-session dialect 1256 (plaza enter, room, ready, combat, relay).
/// Numbers are client-verified. C2S names without an in-binary <c>gcp*</c> label stay
/// <c>UnknownNNN</c>. Semantic role is in the notes, not the identifier.
///
/// </summary>
public static class InGameOpcodes
{
    /// <summary>C2S 17. Empty body. Built after S2C 608 in <c>kr-1256 sub_9E7570</c> at <c>0x9E7570</c>.</summary>
    public const ushort C2SGameStartReady = 17;

    /// <summary>C2S 103. Thirty <c>i32</c> values. Builder <c>kr-1256 sub_113F230</c> at <c>0x113F230</c>.</summary>
    public const ushort C2SGameResultStats = 103;

    /// <summary>
    /// C2S 106. Official name <c>Unknown106</c>. Empty plaza enter after main menu.
    /// Builder <c>kr-1256 sub_895A70</c> at <c>0x895A70</c>; also inline in <c>kr-1256 sub_9E1EB0</c>.
    ///
    /// </summary>
    public const ushort C2SUnknown106 = 106;

    /// <summary>
    /// C2S 53 plaza channel move. Builder <c>kr-1256 sub_56FB50</c> at
    /// <c>0x56fb50</c>: the opcode stamp at <c>kr-1256 sub_56FB50</c> then one
    /// <c>i32</c> through the stream vtable <c>+48</c> at <c>:17</c>, so the
    /// body is exactly four bytes. The value is a channel id - one of the four
    /// <c>DlgChannelPop</c> rows 20 / 60 / 70 / 80 (<c>kr-1256 sub_56F650</c>, inverse
    /// <c>kr-1256 sub_944F90</c>). Evidence: verified body, widths and value set.
    ///
    /// </summary>
    /// <remarks>
    /// Declared for reference only: the opcode is registered by the lobby row,
    /// which replies with S2C 11 <c>gspRepMoveInto</c> (<c>kr-1256 sub_9A6D30</c>), the
    /// only post-login same-socket writer of the client's channel state
    /// <c>ServerConnection+2508</c>. A second registration for the same opcode
    /// fails activation. The move is a channel change, not a map change: no
    /// stage move and no teardown.
    /// </remarks>
    public const ushort C2SPlazaMapMove = 53;

    /// <summary>
    /// C2S 108 single-player training-ground create. Builder <c>kr-1256 sub_892E10</c> at
    /// <c>0x892E10</c> (the <c>btnGoToTrainingRoom</c> click arm, gated by <c>kr-1256 sub_99D510(3)</c> for a
    /// solo party and <c>kr-1256 sub_939CD0</c>): the 56-byte rule record written as thirteen <c>i32</c> plus
    /// one <c>u8</c>. The body carries no mode id and no map name, so the server synthesizes mode 40 and
    /// <c>rel_trainingRoom01</c>. Evidence: verified wire order and widths.
    /// </summary>
    public const ushort C2STrainingRoom = 108;

    /// <summary>C2S 401. Two <c>u16</c> room-list page request. Builder <c>kr-1256 sub_858E70</c> at <c>0x858E70</c>.</summary>
    public const ushort C2SUnknown401 = 401;

    /// <summary>
    /// C2S 402. Create-room send: <c>SessionSetting</c> writer <c>kr-1256 sub_113BAB0</c> then a
    /// trailing wstring and optional tagged <c>i32</c>. Builder <c>kr-1256 sub_962200</c> at <c>0x962200</c>.
    ///
    /// </summary>
    public const ushort C2SUnknown402 = 402;

    /// <summary>C2S 403. Join send: <c>i32</c> plus prefixed wstring. Builder <c>kr-1256 sub_854C90</c> at <c>0x854C90</c>.</summary>
    public const ushort C2SUnknown403 = 403;

    /// <summary>C2S 404. Empty rooms helper. Builder <c>kr-1256 sub_62A880</c> at <c>0x62A880</c>.</summary>
    public const ushort C2SUnknown404 = 404;

    /// <summary>
    /// C2S 503 create-room request. Builder <c>kr-1256 sub_9D7570</c> at <c>0x9D7570</c>
    /// (create-path callers <c>kr-1256 sub_9A0880</c> / <c>kr-1256 sub_9A0C70</c>): zero-extended
    /// <c>u8</c>, three prefixed wstrings, <c>i32</c>, 56-byte rule record
    /// (<c>kr-1256 sub_10D26C0</c>), trailing <c>i32</c>. See <c>CreateRoomRequest</c>.
    ///
    /// </summary>
    public const ushort C2SCreateRoom = 503;

    /// <summary>
    /// C2S 107 lobby quick-create variant. Builder <c>kr-1256 sub_9D7440</c> at
    /// <c>0x9D7440</c> (caller <c>kr-1256 sub_99F580</c>, guard <c>a2 &lt;= 8</c>):
    /// 56-byte rule record, three prefixed wstrings, trailing <c>i32</c>.
    ///
    /// </summary>
    public const ushort C2SQuickCreateRoom = 107;

    /// <summary>
    /// C2S 517 room join request. Builder <c>kr-1256 sub_708310</c> at <c>0x708310</c>:
    /// one <c>i32</c> room id; the client arms a 20,000 ms timeout.
    ///
    /// </summary>
    public const ushort C2SJoinRoom = 517;

    /// <summary>
    /// C2S 505 waiting-seat status: ready, shop open, inventory open as bool32.
    /// Builder <c>0x9785E0</c>; no native packet label is known.
    ///
    /// </summary>
    public const ushort C2SUnknown505 = 505;

    /// <summary>
    /// C2S 502 room chat / room-name set. Builder <c>kr-1256 sub_4E9070</c> at
    /// <c>0x4E9070</c>: <c>i32</c>, prefixed wstring, prefixed wstring.
    ///
    /// </summary>
    public const ushort C2SRoomChat = 502;

    /// <summary>
    /// C2S 515 room team-change send, one of three builders that stamp the same
    /// opcode. The team-change arm is <c>kr-1256 sub_4B3AC0</c> at <c>0x4B3AC0</c>,
    /// reached only from <c>kr-1256 sub_971140</c> - the <c>RoomStage8vs8</c> click
    /// dispatcher's arm for <c>btnRoomChangeTeam_Blue</c>
    /// (<c>roomstage+0x638</c>) and <c>btnRoomChangeTeam_Red</c>
    /// (<c>roomstage+0x63C</c>) at <c>kr-1256 sub_969730</c>. Its body is a
    /// two-level tagged optional: a <c>u8</c> tag (58 when the following block
    /// is absent, 186 when present), an <c>i32</c> only when the tag is 186,
    /// then the same pair again through <c>kr-1256 sub_10C3E90</c>
    /// (<c>kr-1256 sub_4B3AC0</c>, <c>kr-1256 sub_10C3E90</c>).
    ///
    /// </summary>
    /// <remarks>
    /// The opcode is shared: <c>kr-1256 sub_4A1EB0</c> (<c>0x4A1EB0</c>) and
    /// <c>kr-1256 sub_ADEF30</c> (<c>0xADEF30</c>) also write 515 with different
    /// bodies, so a single grammar cannot be pinned for the whole opcode and
    /// the handler logs rather than parses. Evidence: verified builder, call
    /// chain and tag grammar; the field meanings are unverified.
    /// </remarks>
    public const ushort C2SChangeTeam = 515;

    /// <summary>
    /// C2S 513, the room map-list report. Dispatcher <c>kr-1256 sub_9D9930</c> calls
    /// <c>kr-1256 sub_9DD000</c>, which parses the body with <c>kr-1256 sub_9D8C00</c>: a
    /// prefixed wstring, a second prefixed wstring, and one <c>i32</c>
    /// (<c>kr-1256 sub_9D8C00</c>, <c>:86</c>, <c>:126</c>).
    ///
    /// </summary>
    /// <remarks>
    /// The first wstring is a display name and the second is a map record key
    /// (`rel_S315`, `rel_Village07_Easy02`, or empty for a <c>Random</c>
    /// selection); the trailing <c>i32</c> is the second string's length on the
    /// frames observed. Every frame carries more bytes after the three
    /// fields and no reader in the client code consumes them, so the handler logs the
    /// three and stops. See <c>src/BubbleFighter.Fantasy.InGame.Wire/Wire/MapListRecord.cs</c>.
    /// </remarks>
    public const ushort C2SMapListReport = 513;

    /// <summary>
    /// C2S 520 room chat (typed variants). Builder <c>kr-1256 sub_9D7350</c> at
    /// <c>0x9D7350</c>: <c>i32</c>, prefixed wstring, <c>i32</c>, <c>i32</c>.
    ///
    /// </summary>
    public const ushort C2SRoomChatTyped = 520;

    /// <summary>
    /// C2S 511 in-room state relay. Builder <c>kr-1256 sub_913640</c> at <c>0x913640</c>: four
    /// <c>i32</c>, two <c>u8</c>, one <c>i32</c>. The walk proves this is the periodic in-room character /
    /// input relay, not a start command, so it is logged only.
    /// </summary>
    public const ushort C2SRoomStateCommand = 511;

    /// <summary>
    /// S2C 503 create-room reply (separate numeric space from C2S 503). Handler <c>kr-1256
    /// sub_9DA720</c> via <c>kr-1256 sub_9D9930</c> case 0x1F7: <c>i32 slotIndex</c>, one
    /// <c>RoomPlayerInfo</c>, two keyed maps, one <c>ServerTeamInfo</c>. See <c>CreateRoomReply</c>.
    /// </summary>
    public const ushort S2CCreateRoomReply = 503;

    /// <summary>
    /// S2C 504 seat team-label update. Handler <c>kr-1256 sub_9DAE50</c> (<c>kr-1256 sub_9D9930</c>
    /// case 0x1F8): <c>i32 seatIndex</c>, <c>u8 label</c>, then the optional tail <c>kr-1256 sub_10C3F00</c>
    /// reads as a tag plus two <c>i32</c> - six bytes with the tag absent (<c>kr-1256 sub_9DAE50</c>,
    /// <c>kr-1256 sub_10C3F00</c>). The label is written to <c>seat+4</c> through <c>kr-1256 sub_9E4C80</c>
    /// (<c>kr-1256 sub_9DAE50</c>, writer <c>kr-1256 sub_9E4C80</c>) - exactly the byte <c>kr-1256
    /// sub_980730</c> switches the team board on - value 36 short-circuits at
    /// <c>kr-1256 sub_9DAE50</c>, and every slot is refreshed there. Unlike the
    /// S2C 117 it never calls the stage setter, so this is the packet for a team change: the label reaches the
    /// board without the client tearing the room down.
    /// </summary>
    public const ushort S2CSeatTeamLabel = 504;

    /// <summary>
    /// S2C 501 room-info snapshot. Handler <c>kr-1256 sub_113B7B0</c> / deserializer <c>kr-1256
    /// sub_113BD20</c> (<c>kr-1256 sub_9D9930</c> case 0x1F5): exactly one <c>SessionSetting</c>. The client
    /// applies it to the room stage when <c>kind</c> (object +64, <c>*(session+72)</c>) is 2. See
    /// <c>RoomInfoSnapshot</c>.
    /// </summary>
    public const ushort S2CRoomInfoSnapshot = 501;

    /// <summary>
    /// S2C 505, the in-place map-selection update: exactly one <c>MapMeta</c> (dispatcher <c>kr-1256
    /// sub_9D9930</c> case 0x1F9), deep-copied into <c>session+140</c> by
    /// the handler, which then redraws the mode/map board - no stage change, no <c>session+12</c> write, no
    /// rebuild. This is the frame a map change must travel on.
    /// </summary>
    public const ushort S2CRoomMapInfo = 505;

    /// <summary>
    /// S2C 201 <c>gspUpdateParty</c>. Handler <c>kr-1256 sub_9064B0</c>: it writes a
    /// profile-map record per party member (<c>kr-1256 sub_9064B0</c>) and then
    /// spawns and dresses a Character per occupied seat through
    /// <c>kr-1256 sub_920940</c> (<c>kr-1256 sub_9064B0</c>, gated on the stage not being
    /// 5). The login burst already sends the solo form; the room-entry burst
    /// sends one per seat.
    ///
    /// </summary>
    public const ushort S2CUpdateParty = 201;

    /// <summary>
    /// S2C 517 join handshake. Handler <c>kr-1256 sub_9DF920</c> at <c>0x9DF920</c> (<c>kr-1256
    /// sub_9D9930</c> case 0x1F5-line 220): one <c>i32</c> token read from the stream. The handler immediately
    /// echoes it back as the first field of the S2C 518 reply. See <c>JoinHandshake</c>.
    /// </summary>
    public const ushort S2CJoinHandshake = 517;

    /// <summary>
    /// S2C 518 join slot update. Inline in the same dispatcher (case line 223):
    /// four <c>i32</c> written as the echoed token, 0, <c>dword_18E8F00</c>
    /// and <c>*(MainApp+72)</c>; on a room stage it calls
    /// <c>kr-1256 sub_978F20(roomstage+516, i32, i32, i32)</c>. See <c>JoinSlotUpdate</c>.
    ///
    /// </summary>
    public const ushort S2CJoinSlotUpdate = 518;

    /// <summary>
    /// C2S 301 character position report (plaza). Builder <c>kr-1256 sub_913640</c> at
    /// <c>0x913640</c> while no room session exists: the 22-byte body
    /// <c>CharacterPositionRequest</c>.
    ///
    /// </summary>
    public const ushort C2SCharacterPositionPlaza = 301;

    /// <summary>
    /// C2S 511 character position report (in-room). Same builder and body as
    /// C2S 301, emitted while a room session exists.
    ///
    /// </summary>
    public const ushort C2SCharacterPositionRoom = 511;

    /// <summary>
    /// S2C 301 <c>gspReceivePartyChrPos</c> (plaza remote position). Inline
    /// dispatch in <c>kr-1256 sub_9D2FF0</c> (case <c>0x9D4401</c>, label
    /// <c>0x15B7E30</c>) applying through <c>kr-1256 sub_91FB80</c>: 26 bytes = the
    /// sender identity plus the 22-byte C2S body.
    ///
    /// </summary>
    public const ushort S2CCharacterPositionPlaza = 301;

    /// <summary>
    /// S2C 514 remote position inside a room. <c>kr-1256 sub_9D9930</c> case
    /// <c>0x202</c>, same 26-byte body as S2C 301, <c>RoomStage8vs8</c>-gated.
    ///
    /// </summary>
    public const ushort S2CCharacterPositionRoom = 514;

    /// <summary>
    /// S2C 511 per-seat room record. Handler <c>kr-1256 sub_9DB1E0</c> at <c>0x9DB1E0</c> (<c>kr-1256
    /// sub_9D9930</c> case 0x1FF): <c>i32 slotIndex</c>, one <c>RoomPlayerInfo</c>, one <c>ServerTeamInfo</c>.
    /// The room-UI walk grades this as the only packet in the room reply set that writes a seat with a full
    /// record and reaches the room-stage spawn path.
    /// </summary>
    public const ushort S2CSeatRecord = 511;

    /// <summary>S2C 502, the new room master's seat index. Handler <c>kr-1256 sub_9DA3C0</c> (case 0x1F6).</summary>
    public const ushort S2CMasterChanged = 502;

    /// <summary>
    /// C2S 105, empty: a plaza player returns to the main menu. Answered with the S2C 117
    /// kind-1 install.
    ///
    /// </summary>
    public const ushort C2SReturnToMenu = 105;

    /// <summary>
    /// C2S 509, 8 bytes from <c>kr-1256 sub_97CFD0</c>: <c>i32</c> seat index, <c>i32</c> requested closed
    /// state. <c>kr-1256 sub_980300</c> sends it when the room master clicks an empty seat, asking for the
    /// inverse of the seat's current closed byte (<c>RoomPlayerInfo</c> +1). S2C 509 is a
    /// different packet in its own numeric space.
    ///
    /// </summary>
    public const ushort C2SUnknown509 = 509;

    /// <summary>S2C 103, the room-join failure result. Read by <c>kr-1256 sub_853E90</c> case 0x67.</summary>
    public const ushort S2CJoinResult = 103;

    /// <summary>
    /// S2C 508 per-seat room record. Parser <c>kr-1256 sub_10C47E0</c>: <c>u16</c>
    /// seat index, <c>i32</c>, two <c>wstring</c>s, <c>i32</c>, <c>i32</c>,
    /// <c>wstring</c>, <c>i32</c>. The fourth field renders as a chat line
    /// (<c>kr-1256 sub_4D6020</c>); the seat index selects the displayed nickname.
    /// Used for room chat and per-seat notices. Evidence: verified field
    /// table and render path.
    ///
    /// </summary>
    public const ushort S2CSeatChat = 508;

    /// <summary>
    /// S2C 509, the chat render frame (bubble + chat-panel line). Parser <c>kr-1256
    /// sub_10C4A50</c>, handler <c>kr-1256 sub_9DE360</c> - the family's only chat-panel writer
    /// (<c>kr-1256 sub_4DBBD0</c>).
    /// </summary>
    public const ushort S2CChatPanel = 509;

    /// <summary>
    /// S2C 101 room-list snapshot. Dispatched on the LobbyStage virtual
    /// <c>kr-1256 sub_853CF0</c> at <c>0x853CF0</c> (<c>kr-1256 sub_853E90</c> case <c>0x65</c>,
    /// label <c>0x159CE18</c>) through <c>kr-1256 sub_84F550</c> and the
    /// <c>SessionSetting</c> reader <c>kr-1256 sub_113BD20</c>, then rendered by
    /// <c>kr-1256 sub_856CD0</c> -&gt; <c>kr-1256 sub_63CF90</c> (the <c>DlgRoomListPop</c>
    /// rows). Body: three <c>i32</c> headers, a <c>u32</c> count, then that
    /// many <c>SessionSetting</c> records. Evidence: verified dispatch chain
    /// and body shape; the C2S 401 pairing is inferred.
    ///
    /// </summary>
    public const ushort S2CRoomList = 101;

    /// <summary>C2S 506. Empty leave-game. Builder <c>kr-1256 sub_75F3F0</c> at <c>0x75F3F0</c>.</summary>
    public const ushort C2SLeaveGame = 506;

    /// <summary>C2S 571. One <c>i32</c> relay-connect result. Inferred from S2C 571 consumption.</summary>
    public const ushort C2SRelayConnectResult = 571;

    /// <summary>C2S 577. Empty ack when S2C 581 is handled and the relay is live. Builder site <c>kr-1256 sub_9E1EB0</c> case 0x245 writes 577.</summary>
    public const ushort C2SRelayAck = 577;

    /// <summary>C2S 578. Four <c>i32</c>. Builder <c>kr-1256 sub_6DC970</c> at <c>0x6DC970</c>.</summary>
    public const ushort C2SRelayEndpoint = 578;

    /// <summary>
    /// C2S 3 <c>gspRepLogin</c>. Dispatcher <c>kr-1256 sub_9D2FF0</c> case, handler
    /// <c>kr-1256 sub_9A2770</c>.
    ///
    /// </summary>
    /// <remarks>
    /// The lobby's login request. It also carries the relay authentication on a
    /// client's second match connection - a live session sent the same 43-byte body
    /// on this opcode that its first connection sent on opcode 1 - so the lobby's
    /// login handler asks the published match-connection tracker first and leaves a
    /// bound match connection's frame to the in-game row.
    /// </remarks>
    public const ushort C2SLogin = 3;

    /// <summary>
    /// C2S 607. One <c>i32</c> scene-object id. Builder <c>kr-1256 sub_6DDE20</c> at <c>0x6DDE20</c>.
    ///
    /// </summary>
    public const ushort C2SSceneObjectReady = 607;

    /// <summary>
    /// In-session opcodes whose complete layout or response policy remains unverified.
    /// Recovered actions are registered separately by the 1256 dialect;
    /// an absent native label alone does not require a log-only handler.
    ///
    /// </summary>
    public static readonly ushort[] UnlabeledInSession =
    [
        33290, 33292,
    ];

    /// <summary>
    /// The eight damager-spawn opcodes (32772, 32877, 32881, 32898, 32899,
    /// 32900, 32910, 33278): each mints a combat damager key on receipt as
    /// <c>100000*ownerSlot + n</c> per-slot counters. Relaying them byte-identically and in order is
    /// what makes a relayed 38283 hit resolve on the receiving client -
    /// without the spawns, its own counters never mint the key and the hit
    /// drops silently.
    ///
    /// </summary>
    public static readonly ushort[] DamagerSpawns =
    [
        32772, 32877, 32881, 32898, 32899, 32900, 32910, 33278,
    ];

    /// <summary>
    /// S2C 38281: i32 actor. 0x6D88D9 resolves Character and calls virtual +0x228;
    /// 0x4A6F90 enters cssLockedIn (301). This is a bubble acknowledgement, not scene registration.
    ///
    /// </summary>
    public const ushort S2CUnknown38281 = 38281;

    /// <summary>
    /// S2C 128, the waiting dialog ("waiting for players" wall). Dispatcher
    /// <c>kr-1256 sub_853E90</c> case 0x80 -> parser <c>kr-1256 sub_84F6C0</c>
    /// -> <c>kr-1256 sub_858250</c>. Body is exactly two <c>i32</c>: state (0 opens the
    /// wall as UI resource 1760; 0x14 dismisses it and the first seat's client
    /// re-sends C2S 402, <c>kr-1256 sub_858250</c>) and an unverified argument.
    ///
    /// </summary>
    public const ushort S2CWaitingDialog = 128;

    /// <summary>
    /// S2C 4, the arena zone-mask pair: one <c>i32</c> actor id, then two
    /// count-prefixed dword vectors (reader <c>kr-1256 sub_10C7280</c>, framed by
    /// <c>kr-1256 sub_6CD590</c>), each unpacked by <c>kr-1256 sub_10F48B0</c>
    /// (32 dwords = 128 bytes, zero-padded) into 1000 bools per group at
    /// <c>this+4604</c> (<c>kr-1256 sub_4832D0</c>).
    ///
    /// </summary>
    public const ushort S2CArenaZoneMasks = 4;

    /// <summary>
    /// S2C 5, the arena cell grid: one <c>i32</c> actor id, one <c>i32</c>
    /// count of 1000, then 1000 bytes (<c>kr-1256 sub_10BE840</c>) stored at
    /// <c>this+10628</c> (<c>kr-1256 sub_483420</c>) and hashed-and-reported back
    /// by the client (<c>kr-1256 sub_483420</c>).
    ///
    /// </summary>
    public const ushort S2CArenaCellGrid = 5;

    /// <summary>
    /// S2C 6, the third arena mask: one <c>i32</c> actor id and one
    /// count-prefixed dword vector (<c>kr-1256 sub_6CDA20</c>) unpacked into
    /// the mask at <c>this+11640</c> (<c>kr-1256 sub_483540</c>).
    ///
    /// </summary>
    public const ushort S2CArenaThirdMask = 6;

    /// <summary>
    /// S2C 7, the altitude float: one <c>u32</c> slot (read then discarded) and
    /// one <c>f32</c> stored at <c>game+0xF20</c> only when
    /// <c>game+0xF54</c> is set (<c>kr-1256 sub_9601E0</c>). The float's
    /// consumer was not found, so the meaning is unproven; the value sent is
    /// 110.0f, which does not establish its meaning.
    ///
    /// </summary>
    public const ushort S2CArenaAltitude = 7;

    /// <summary>
    /// S2C 38278, the in-session spawn record: one <c>i32</c> entity id and one
    /// <c>i32</c> argument. Dispatcher <c>kr-1256 sub_6D55D0</c> case at
    /// <c>kr-1256 sub_6D55D0:0x6D8952</c>, handler <c>kr-1256 sub_48D3E0</c> - the only
    /// packet-reachable caller of the respawn <c>kr-1256 sub_49C870</c> that writes
    /// full HP through SetHp. Body and evidence in
    /// <c>packets/respawn.yaml</c>.
    ///
    /// </summary>
    public const ushort S2CInSessionSpawn = 38278;

    /// <summary>
    /// C2S 579. Builder <c>kr-1256 sub_6DA5D0</c> at <c>0x6DA5D0</c>, whose payload is
    /// read by <c>kr-1256 sub_10D14C0</c> at <c>0x10D14C0</c>. The client sends it
    /// back-to-back with C2S 607 in the same millisecond under one gate
    /// (<c>kr-1256 sub_755330</c>: <c>!a3 &amp;&amp; dword_18E55A0 &amp;&amp;
    /// dword_18E55AC</c>) - it is the client's own "I consumed the stage move"
    /// report, so it is the moment a pending start chain should be released
    /// rather than waiting for C2S 571 or a position report.
    ///
    /// </summary>
    /// <remarks>
    /// The body is not parsed: <c>kr-1256 sub_10D14C0</c> walks three nested counted
    /// vectors and eight sub-records whose element readers
    /// (<c>kr-1256 sub_10BE6F0</c>, <c>kr-1256 sub_10D2170</c>, <c>kr-1256 sub_429D10</c>) are shared
    /// helpers, so no field layout is recovered. The handler logs the length and
    /// acts on the packet's meaning, not its bytes.
    /// </remarks>
    public const ushort C2SStageMoveConsumed = 579;

    /// <summary>C2S 608. Gameplay action. Builder <c>kr-1256 sub_6DDBA0</c> at <c>0x6DDBA0</c>.</summary>
    public const ushort C2SGameplayAction = 608;

    /// <summary>
    /// C2S 518, the in-play latency sample. Dispatcher <c>kr-1256 sub_9D9930</c> calls
    /// <c>kr-1256 sub_978F20</c>. The body is 16 bytes: two reserved <c>i32</c>, the seat
    /// index, and a timestamp.
    ///
    /// </summary>
    /// <remarks>
    /// The handler pushes the sample into a per-seat ring buffer of at most five
    /// entries and stores the running mean at <c>session+144+4*seat</c>, which is
    /// what the ping glyphs beside a character read. The sample's latency itself
    /// is a client-local clock difference, so the server can accept the frame but
    /// cannot supply its content. The handler here is log-only and invents no
    /// reply.
    /// </remarks>
    public const ushort C2SPingSample = 518;

    /// <summary>
    /// C2S 32781: i32 actor, i32 unknown0, bool32 unknown1. The fallback
    /// dispatcher 0x6D8E90 applies Character virtual +0x1E4/+0x1E8.
    /// The verified match-action route forwards this body to initialized peers.
    ///
    /// </summary>
    public const ushort C2SInPlayReport = 32781;

    /// <summary>
    /// C2S 32885, the in-play movement report on the game connection. Dispatcher
    /// <c>kr-1256 sub_6D55D0</c> case <c>0x6D7E37</c> builds a 20-byte object with
    /// <c>kr-1256 sub_6CAA90</c> and parses it with <c>kr-1256 sub_703270</c>. The body is 21
    /// bytes: <c>i32</c> object id, <c>i32</c> sequence, two <c>f32</c>
    /// coordinates, three <c>u8</c> flag bytes and one trailing byte.
    ///
    /// </summary>
    /// <remarks>
    /// This is the most frequent opcode on the game connection that the emulator
    /// does not handle. The same opcode is
    /// registered on the relay listener as <see cref="RelayMotion"/> (20003),
    /// which is where the movement stream reaches peers. The handler here is
    /// log-only and invents no reply, because no client path expects one. See <c>src/BubbleFighter.Fantasy.InGame.Wire/Wire/MovementReport.cs</c>.
    /// </remarks>
    public const ushort C2SMovementReport = 32885;

    /// <summary>
    /// C2S 38283, the in-session combat hit report. Dispatcher <c>kr-1256 sub_6D55D0</c> case
    /// <c>0x6D8679-0x6D873E</c> reads exactly 19 bytes: <c>i32</c> damager key, <c>i32</c> target object id,
    /// three binary16 <c>u16</c> hit coordinates, one <c>u8</c> mode and one <c>i32</c> collapsed to a bool.
    /// The damager keys are allocated by the client's own simulation, so the server cannot mint one; a key its
    /// resolver misses is dropped silently, which makes the verbatim relay the documented safe behaviour. See
    /// <see cref="S2CCombatHitReport"/> and <c>CombatHitReport</c>.
    /// </summary>
    public const ushort C2SCombatHitReport = 38283;

    /// <summary>
    /// S2C 32885 <c>Unknown32885</c>. The 21-byte movement body the reference's
    /// server answers a client's C2S 32885 with - 63,792 frames against the
    /// client's 2,530 on one connection. Same field widths as
    /// <see cref="C2SMovementReport"/>: <c>i32</c> object id, <c>i32</c> sequence,
    /// two <c>i32</c> absolute coordinates, three <c>u8</c> state bytes and two
    /// <c>u8</c> trailer bytes. <c>kr-1256 sub_703270</c>'s float reads at <c>+4</c>,
    /// <c>+8</c> and <c>+0xC</c> are a record-to-record transform, not a second
    /// wire shape. Handler <c>kr-1256 sub_6CAA90</c> on <c>kr-1256 sub_6D55D0</c>.
    ///
    /// </summary>
    public const ushort S2CMovementReport = 32885;

    /// <summary>
    /// S2C 38283, the relayed combat hit frame. The relay forwards the received
    /// 19-byte C2S payload unchanged under this opcode. The paired payload
    /// observation establishes byte parity, not authoritative hit validation. Separate numeric space
    /// from <see cref="C2SCombatHitReport"/>; the values coincide.
    ///
    /// </summary>
    public const ushort S2CCombatHitReport = 38283;

    /// <summary>
    /// C2S 32923 <c>Unknown32923</c>. Dispatcher <c>kr-1256 sub_6CAFD0</c> on
    /// <c>kr-1256 sub_6D55D0</c> (<c>0x6D7E6A</c>).
    ///
    /// </summary>
    /// <remarks>
    /// The dominant client opcode on the in-game connection. The same surface is registered on the relay listener as
    /// <see cref="RelayWander"/> (20004), the UDP remapping, and a sweep of all
    /// thirteen in-game flows finds 20004 nowhere in either direction - the connection
    /// carries this raw opcode instead. See <c>src/BubbleFighter.Fantasy.InGame.Wire/Wire/MovementSample.cs</c>.
    /// </remarks>
    public const ushort C2SWanderReport = 32923;

    /// <summary>
    /// C2S 25 on the match (relay) connection, the weapon-preset post-swap report
    /// (5 bytes: i32 actor id, u8 slot), built inline by <c>kr-1256 sub_6DA420</c> after a
    /// status-zero S2C 836 apply and sent through <c>dword_18E7DAC</c>. Log-only.
    ///
    /// </summary>
    public const ushort C2SPostSwapReport = 25;

    /// <summary>
    /// C2S 941, the one-shot resolved-map-key report: <c>i32</c> wchar count + 2*N UTF-16LE bytes
    /// carrying the resolved <c>rel_*</c> scene key. One-shot per round entry (latch
    /// <c>dword_18E55A0+3940</c>), no consumer in any dispatch surface. Logged for the key echo confirmation;
    /// never answered.
    /// </summary>
    public const ushort C2SMapKeyReport = 941;

    /// <summary>
    /// C2S 602 native character reset: actor, two tagged optional i32 values and
    /// bool32 item reset, totaling 10, 14 or 18 bytes (writer 0x6E3730).
    /// Ordinary timed-score matches confirm it with S2C 632 after death cleanup.
    ///
    /// </summary>
    public const ushort C2SRoundMarker = 602;

    /// <summary>C2S 41, empty fire-and-forget chatter (structured negative:
    /// no builder, no dispatcher case, log only).</summary>
    public const ushort C2SUnknown41 = 41;

    /// <summary>C2S 911, the empty "room/seat object missing" re-ask
    /// (2000 ms throttle, sub_933DC0). The fix is the room install, not a
    /// reply.</summary>
    public const ushort C2SRoomReAsk = 911;

    /// <summary>C2S 625. Empty game-over. Semantic name; official symbol absent. Evidence inferred from result-path callers.</summary>
    public const ushort C2SGameOver = 625;

    /// <summary>
    /// C2S 624, the death report: two <c>i32</c> fields, 8 bytes, written by <c>kr-1256
    /// sub_708B60</c> via the wrapper <c>kr-1256 sub_6E5840</c>. The dying client's path
    /// <c>kr-1256 sub_B38850</c> stamps the empty C2S 625 and then sends this as <c>{1, 0}</c>; the timer paths
    /// send <c>{0, 0}</c>. The S2C census has no 624 row, so no others-facing death surface is proven: the
    /// frame is pinned and logged, never relayed or answered.
    /// </summary>
    public const ushort C2SDeathReport = 624;

    /// <summary>C2S 2029. Alternate action frame written by <c>kr-1256 sub_708660</c> at <c>0x708660</c>.</summary>
    public const ushort C2SActionAlternate = 2029;

    /// <summary>S2C 115 <c>gspGotoMenu</c>. Handler <c>kr-1256 sub_9AC0F0</c> at <c>0x9AC0F0</c>. Label <c>0x15B6F7C</c>.</summary>
    public const ushort S2CGotoMenu = 115;

    /// <summary>S2C 116 <c>gspGotoLobby</c>. Handler <c>kr-1256 sub_9AC2C0</c> at <c>0x9AC2C0</c>. Label <c>0x15B70E4</c>.</summary>
    public const ushort S2CGotoLobby = 116;

    /// <summary>S2C 117 <c>gspGotoRoom</c>. Handler <c>kr-1256 sub_9AC490</c> at <c>0x9AC490</c>. Parser <c>kr-1256 sub_10D4520</c>. Label <c>0x15B7100</c>.</summary>
    public const ushort S2CGotoRoom = 117;

    /// <summary>S2C 118 <c>gspRepWaitUserList</c>. Handler <c>kr-1256 sub_9ACD00</c> at <c>0x9ACD00</c>. Parser <c>kr-1256 sub_10D4B50</c>. Label <c>0x15B7118</c>.</summary>
    public const ushort S2CWaitUserList = 118;

    /// <summary>S2C 571 <c>gspConnectToRelayServer</c>. Handler <c>kr-1256 sub_9A7250</c> at <c>0x9A7250</c>. Label <c>0x15B6E0C</c>.</summary>
    public const ushort S2CConnectRelay = 571;

    /// <summary>
    /// S2C 510 room-to-game stage move. Dispatched by <c>kr-1256 sub_9D9930</c> case 0x1FE at
    /// <c>0x9D9930</c>: the record is built by <c>kr-1256 sub_10D1C10</c> at <c>0x10D1C10</c>, deserialized by
    /// <c>kr-1256 sub_10D1D70</c> at <c>0x10D1D70</c>, its field 18 is copied to session+12, the stage is
    /// re-labelled by <c>kr-1256 sub_94D100</c> and the handler <c>kr-1256 sub_9E5E70</c> at <c>0x9E5E70</c>
    /// runs, ending in the stage-5 (in-game) move. No <c>gcp*</c> label was recovered for it, so the name is
    /// ours. See <c>EnterGameStage</c>.
    /// </summary>
    public const ushort S2CEnterGameStage = 510;

    /// <summary>S2C 584. One <c>i32</c> slot. <c>kr-1256 sub_9E1EB0</c> case 0x248 at <c>0x9E1EB0</c>.</summary>
    public const ushort S2CPlayerMapLoaded = 584;

    /// <summary>S2C 585. Three <c>i32</c>-as-bool. <c>kr-1256 sub_9E1EB0</c> case 0x249 at <c>0x9E1EB0</c>.</summary>
    public const ushort S2CStartFlags = 585;

    /// <summary>
    /// S2C 602 seat-info record. <c>kr-1256 sub_9E1EB0</c> case 0x25A at <c>0x9E1EB0</c>: one
    /// 152-byte record read by <c>kr-1256 sub_10D22D0</c> at <c>0x10D22D0</c> and copied to session+16
    /// (<c>a1+4</c>). See <c>SeatInfo</c>.
    /// </summary>
    public const ushort S2CSeatInfoA = 602;

    /// <summary>
    /// S2C 603 seat-info record. <c>kr-1256 sub_9E1EB0</c> case 0x25B: the same
    /// 152-byte <c>kr-1256 sub_10D22D0</c> record copied to session+168 (<c>a1+42</c>).
    ///
    /// </summary>
    public const ushort S2CSeatInfoB = 603;

    /// <summary>
    /// S2C 604 seat-info record. <c>kr-1256 sub_9E1EB0</c> case 0x25C: the same
    /// 152-byte <c>kr-1256 sub_10D22D0</c> record copied to session+320 (<c>a1+80</c>).
    ///
    /// </summary>
    public const ushort S2CSeatInfoC = 604;

    /// <summary>
    /// S2C 605 seat-info record. <c>kr-1256 sub_9E1EB0</c> case 0x25D: the same
    /// 152-byte <c>kr-1256 sub_10D22D0</c> record copied to session+472 (<c>a1+118</c>).
    ///
    /// </summary>
    public const ushort S2CSeatInfoD = 605;

    /// <summary>S2C 607. One <c>i32</c> timer. Handler <c>kr-1256 sub_9E7080</c> via <c>kr-1256 sub_9E1EB0</c> case 0x25F.</summary>
    public const ushort S2CStartTimer = 607;

    /// <summary>S2C 608. Two <c>i32</c>. Handler <c>kr-1256 sub_9E7570</c> via <c>kr-1256 sub_9E1EB0</c> case 0x260. Client then emits C2S 17.</summary>
    public const ushort S2CRoundInitialize = 608;

    /// <summary>
    /// S2C 609 round-clock unbounded setter. Handler <c>kr-1256 sub_9E1EB0</c> case <c>0x261</c>:
    /// <c>if (dword_18E55A0) sub_6FEFC0(0x3FFFFFFF)</c> with no stream read at all (<c>kr-1256
    /// sub_9E1EB0</c>), so the body is empty and the no-limit value is the client's own sentinel rather
    /// than one this emulator chooses. The case is reachable: <c>kr-1256 sub_6D55D0</c>'s jump table sends 609
    /// to its default entry, whose handler chain returns 0 for a mode-40 session. Evidence: verified case body
    /// and reachability.
    /// </summary>
    public const ushort S2CRoundClockUnbounded = 609;

    /// <summary>S2C 612. Two <c>i32</c>. <c>kr-1256 sub_9E2FE0</c> case 0x264 at <c>0x9E2FE0</c>.</summary>
    public const ushort S2CResultOpen = 612;

    /// <summary>
    /// S2C 613, the post-result banner step. Dispatcher <c>kr-1256 sub_9E2FE0</c> case
    /// <c>0x265</c>: two <c>i32</c> the handler never reads, then two <c>u8</c>
    /// stored at <c>GameInfo+308/+312</c> before the phase 5 -&gt; 6 write and
    /// the gated banner block (handler <c>kr-1256 sub_9E9710</c>; see
    /// <see cref="BubbleFighter.Fantasy.InGame.Wire.GameToClient.ResultBanner"/>).
    /// Emitted only together with <see cref="S2CScoreboardData"/>, behind
    /// the <c>send_scoreboard_rows</c> option of the in-game row.
    ///
    /// </summary>
    public const ushort S2CResultBanner = 613;

    /// <summary>S2C 615. One <c>u8</c>. <c>kr-1256 sub_9E2FE0</c> case 0x267.</summary>
    public const ushort S2CResultClose = 615;

    /// <summary>
    /// S2C 614, the scoreboard-data record. Dispatcher <c>kr-1256 sub_9D2FF0</c> case
    /// <c>0x266</c>: builder <c>kr-1256 sub_10D16A0</c> at <c>0x10D16A0</c> constructs
    /// the <c>ScoreData</c> record through the ctor <c>kr-1256 sub_4289F0</c> and fills
    /// it with the record's own virtual deserializer <c>kr-1256 sub_429620</c>
    /// (<c>ScoreData::Deserialize</c>, vftable <c>0x154BE48</c> slot +4, RTTI
    /// <c>.?AVScoreData@@</c>); the consumer <c>kr-1256 sub_9E9A60</c> swaps the record
    /// into session <c>+1720</c>. One variable-length body - rows are counted
    /// by a <c>u64</c> presence mask, not fixed. Emitted only behind
    /// the <c>send_scoreboard_rows</c> option of the in-game row; see
    /// <see cref="BubbleFighter.Fantasy.InGame.Wire.ResultScoreboard"/>.
    ///
    /// </summary>
    public const ushort S2CScoreboardData = 614;

    /// <summary>
    /// S2C 576, the relay-state clear emitted after result close. Dispatcher <c>kr-1256 sub_9D2FF0</c> case at <c>9D4790-9D4833</c> never
    /// reads the stream: it zeroes the pending-send container at
    /// <c>dword_18E5680+436/+484</c> and the per-player relay state
    /// so the body is empty. Verified-safe
    /// parity frame; not a room-state reset.
    ///
    /// </summary>
    public const ushort S2CRelayStateClear = 576;

    /// <summary>
    /// S2C 620, the round exit emitted after result close.
    /// Handler <c>kr-1256 sub_9EA690</c> (dispatcher case <c>0x26C</c>) reads nothing:
    /// it drops the session phase to 2, notifies the mode manager and moves the
    /// stage manager with argument 4 - the same slot move the next S2C 117
    /// install performs anyway - so the body
    /// is empty and the frame is parity only.
    ///
    /// </summary>
    public const ushort S2CRoundStageReset = 620;

    /// <summary>Relay C2S 1. <c>RelaySessionKey</c> plus identity. Builder <c>kr-1256 sub_95FD30</c> at <c>0x95FD30</c>.</summary>
    public const ushort RelayC2SAuthenticate = 1;

    /// <summary>Relay C2S/S2C 20003. Optimized movement. Writer <c>kr-1256 sub_95FAE0</c> at <c>0x95FAE0</c> (UDP 32885 remapped).</summary>
    public const ushort RelayMotion = 20003;

    /// <summary>Relay C2S/S2C 20004. Optimized wander. Writer <c>kr-1256 sub_95FAE0</c> (UDP 32923 remapped).</summary>
    public const ushort RelayWander = 20004;

    /// <summary>C2S 32779: i32 actor, bool32 unknown0; 0x6D6765 calls Character virtual +0x1DC/+0x1E0.</summary>
    public const ushort C2SUnknown32779 = 32779;

    /// <summary>C2S 32780: i32 actor, i32 unknown0, u8 unknown1, u8 unknown2; 0x6D67D8 calls virtual +0x1D0.</summary>
    public const ushort C2SUnknown32780 = 32780;

    /// <summary>C2S 32813: i32 actor; 0x6D6E22 calls Character virtual +0x1CC.</summary>
    public const ushort C2SUnknown32813 = 32813;

    /// <summary>
    /// C2S 18 <c>Unknown18</c>, a mid-round counted-record report on the match
    /// connection (sent through <c>dword_18E7DAC</c>). Builder
    /// <c>kr-1256 sub_6CDCB0</c> stamps the opcode, writes the
    /// element count as one <c>i32</c> from a 20-byte stride
    /// (<c>(v1 - v0) / 20</c>), and lets each element serialise itself through
    /// its own vtable slot; the caller is <c>kr-1256 sub_9601E0</c> reading the round
    /// object's vector at <c>dword_18E55A0 + 3928</c>. A live four-byte body
    /// carrying count 1 with a record whose serialiser emits nothing is
    /// self-consistent. The per-record wire is not pinned (it lives behind a
    /// vtable), so the handler is log-only and invents no reply. S2C 18
    /// <c>gspRepUniquenessCheck</c> is a separate numeric space.
    ///
    /// </summary>
    public const ushort Unknown18 = 18;

    /// <summary>
    /// C2S 32810: i32 unknown0, i32 unknown1, i32 actor, u32 unknown2,
    /// bool32 unknown3, bool32 unknown4 (24 bytes). The paired S2C reader
    /// 0x6F65A0 resolves the actor at offset 8; the match-action route preserves
    /// all fields while forwarding to initialized peers.
    ///
    /// </summary>
    public const ushort Unknown32810 = 32810;

    /// <summary>
    /// C2S 832, the in-round weapon-preset swap request. No client label; the semantic name comes
    /// from the proven swap path. Builder <c>kr-1256 sub_808F10</c> at <c>0x808F10</c>: the opcode stamp at
    /// <c>kr-1256 sub_808F10</c> then one <c>i32</c> and one <c>u8</c> through the stream vtable slot
    /// <c>+48</c> (<c>kr-1256 sub_808F10</c>), so the body is exactly five bytes. The <c>u8</c> is the
    /// preset slot 0..4 (the builder returns for a slot above 4 at <c>kr-1256 sub_808F10</c>; hotkeys
    /// <c>kr-1256 sub_B033E0</c> map to slots 0..4). The <c>i32</c> is 0 on the
    /// UI paths (<c>kr-1256 sub_9753A0</c>, <c>kr-1256 sub_5FB6D0</c>) and the
    /// actor +448 dword on the hotkey path (<c>kr-1256 sub_B033E0</c>); no recovered reader consumes it, so it
    /// rides as <c>Unknown1</c> in <c>WeaponPresetSwap</c>. See <see cref="S2CWeaponPresetSwap"/> for the reply
    /// the client waits for.
    /// </summary>
    public const ushort C2SWeaponPresetSwap = 832;

    /// <summary>
    /// S2C 836 <c>gspReqInventoryChangeWeaponPresetIdx</c>, the swap-apply
    /// reply (separate numeric space from C2S 832). Dispatcher <c>kr-1256 sub_9D2FF0</c>
    /// jumptable <c>jpt_009D48B5</c> case 836 at <c>0x9D5035</c>, reader
    /// <c>kr-1256 sub_429DD0</c>: one <c>u8</c> preset index, then one <c>i32</c>
    /// status through the same stream vtable slot
    /// (<c>kr-1256 sub_9D2FF0:9D5043-9D5067</c>) - five bytes. The case stores the
    /// index at profile +28, clears the C2S 832 in-flight latch at
    /// <c>dword_18E55D4+208</c> (<c>kr-1256 sub_9D2FF0:9D506A-9D5072</c>, the byte
    /// <c>kr-1256 sub_808F10</c> sets after sending) and swaps the in-session
    /// weapon model only when the status is zero
    /// (<c>kr-1256 sub_9D2FF0:9D5079-9D5110</c>). Without the reply the latch blocks
    /// every later swap at <c>kr-1256 sub_808F10</c>, so the handler answers every
    /// parsed C2S 832 with status 0. See <c>WeaponPresetSwapReply</c>.
    ///
    /// </summary>
    public const ushort S2CWeaponPresetSwap = 836;
}
