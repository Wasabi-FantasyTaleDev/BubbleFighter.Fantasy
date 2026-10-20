using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>One seat identity in a room user list.</summary>
/// <param name="UserNum">Identity written into <c>RoomPlayerInfo</c> +8.</param>
/// <param name="Nickname">Display name shown on the seat's member row.</param>
/// <param name="Slot">Seat index used by S2C 584 and the occupancy dword.</param>
/// <param name="Level">Player level. The S2C 118 row's second field is <c>level</c> and <c>sub_EE9550</c> builds the row icon from <c>level - 1</c>, so it must be at least 1.</param>
/// <param name="Team">Current team selector.</param>
/// <param name="Ready">Current ready toggle, as reported by C2S 505.</param>
/// <param name="InShop">Whether the player's shop dialog is open.</param>
/// <param name="InInventory">Whether the player's inventory dialog is open.</param>
public readonly record struct RoomPlayerIdentity(int UserNum, string Nickname, byte Slot, int Level = 1,
    byte Team = 0, bool Ready = false, bool InShop = false, bool InInventory = false)
{
    /// <summary>Builds the same identity, team and status record for both full installs and seat updates.</summary>
    public RoomPlayerInfo ToPlayerInfo() => RoomPlayerInfo.ForSeat(UserNum, Nickname, Slot, Level, Team) with
    {
        Unknown96 = Ready ? 1 : 0,
        Unknown97 = InShop ? 1 : 0,
        Unknown98 = InInventory ? 1 : 0,
    };
}

/// <summary>
/// S2C builders for plaza / waiting-room snapshots owned by this plugin after main menu.
/// Layouts come from <c>sub_9AC2C0</c>, <c>sub_10D4520</c>, and <c>sub_10D4B50</c>.
/// The login-burst kind-2 form is owned by the lobby plugin; this builder writes the
/// room-session kind-2 form produced by <see cref="SessionSettingBlob.ForRoom"/> and the
/// plaza-session kind-3 form produced by <see cref="SessionSettingBlob.ForPlaza"/>.
///
/// </summary>
public static class RoomSessionBlob
{
    /// <summary>Seat count <c>sub_9D9250</c> derives for kind 2 (a room).</summary>
    public const int RoomSeatCount = 16;

    /// <summary>Seat count <c>sub_9D9250</c> derives for kind 3 (the plaza).</summary>
    public const int PlazaSeatCount = 36;

    /// <summary>
    /// S2C 116 <c>gspGotoLobby</c>. Handler <c>sub_9AC2C0</c>: one <c>i32 result</c>.
    /// Zero skips the cannot-move notice. Evidence: verified.
    ///
    /// </summary>
    /// <param name="result">Client result integer.</param>
    public static byte[] GotoLobby(int result)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(result);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 117 <c>gspGotoRoom</c> for a waiting-room or plaza session. Parser <c>sub_10D4520</c>.
    /// Echoes the session <c>SessionSetting</c> and the player list. This is the only server packet that
    /// installs the session object the S2C 503 create reply writes its seat record into, so a room create /
    /// join must send it; the same packet is also the plaza's multiplayer surface, because a kind-3 body both
    /// enters the plaza and seeds every listed player's profile record. </summary> <remarks> <para> The
    /// handler's kind switch (<c>jpt_9AC9DD</c>, <c>sub_9AC490</c> <c>0x9AC9DD</c>, six cases indexed by
    /// <c>kind - 1</c>) routes kind 2 to stage 4 (<c>RoomStage8vs8</c>), kind 3 to stage 3 (<c>LobbyStage</c>,
    /// the plaza, at <c>0x9ACA52</c>), kind 1 to stage 2 and kind 6 to stage 15; kinds 4, 5 and anything above
    /// 6 fall to the default case, which moves no stage. Kind 0 is the raw client create value the registry
    /// normalizes away, so this writer accepts 0, 2 and 3 and refuses the rest. </para> <para> The
    /// <c>map564</c> apply (<c>sub_81D060</c> at <c>0x9AC964</c>) and the <c>map588</c> apply (the
    /// <c>sub_81CCE0</c> loop at <c>0x9AC984</c>-<c>0x9AC990</c>) sit after the kind-independent prologue and
    /// before the switch, so they run for every kind, kind 3 included — that is what writes
    /// <c>dword_18E55D8</c>, the profile map the avatar gate reads. The handler calls neither the spawner
    /// <c>sub_9201D0</c> nor the dresser <c>sub_91F720</c> for any kind: neither address appears in its callee
    /// list, and the spawner's caller set is <c>sub_937310</c>, <c>sub_9DA720</c> and <c>sub_9DB1E0</c>. Every
    /// kind that moves a stage also tears down the live characters through <c>sub_91EDD0</c>. </para>
    /// </remarks> <param name="setting">Session setting; kind 2 is the room stage, kind 3 the plaza. <see
    /// cref="SessionSettingBlob.ForRoom"/> is the documented way to build the room-session form, because the
    /// kind-2 branch of the client handler gates the stage-4 move on <see cref="SessionSettingBlob.Unknown4"/>;
    /// <see cref="SessionSettingBlob.ForPlaza"/> builds the plaza form.</param> <param name="players">Live
    /// seats to list. Records are ordered by slot and gaps are written empty because <c>0x9D9250</c> copies
    /// vector record <c>i</c> into seat <c>i</c>.</param> <param name="profileEntries">Profile-map entries for
    /// <c>map564</c>; pass <see langword="null"/> for the empty form. Each entry is keyed by that player's
    /// <c>UserNum</c> and carries the equipped character id in 300000..301999, which is what the avatar gate
    /// requires.</param> <param name="team">Team panel to write in the <c>ServerTeamInfo</c> slot; pass <see
    /// langword="null"/> for the empty 12-byte form. The direct-start training room needs a populated panel so
    /// the native room transition never observes an empty team.</param> <param name="masterSlot">Room master
    /// index at parsed +544, installed at session +0x228 by <c>0x9D9448</c>.</param> <param
    /// name="localSlot">Recipient index at parsed +728, installed at session +0x244 by <c>0x9D93C2</c>.</param>
    public static byte[] GotoRoom(
        SessionSettingBlob setting,
        IReadOnlyList<RoomPlayerIdentity> players,
        IReadOnlyList<ProfileMapEntry>? profileEntries = null,
        ServerTeamInfoBlob? team = null,
        int masterSlot = 0,
        int localSlot = 0)
    {
        if (setting.Kind is not (0 or SessionSettingBlob.MenuReturnKind or SessionSettingBlob.MainMenuKind or SessionSettingBlob.PlazaKind))
        {
            throw new InvalidOperationException(
                $"S2C 117 kind {setting.Kind} is not a room or plaza session. Kind 2 routes to RoomStage8vs8, kind 3 to LobbyStage; kind 0 is the raw client create value the registry normalizes.");
        }

        var w = PacketWriter.Create();
        setting.Write(w);
        w.WriteInt32(masterSlot); // parsed +544
        w.WriteInt32(0); // unknown548
        // 0x9D9250 installs by vector position, not the record's slot byte.
        if (players.Any(player => player.Slot >= PlazaSeatCount)
            || players.Select(player => player.Slot).Distinct().Count() != players.Count)
            throw new InvalidOperationException("Room install requires unique seat slots below 36.");
        var seatCount = players.Count == 0 ? 0 : players.Max(player => player.Slot) + 1;
        w.WriteUInt32((uint)seatCount);
        for (var slot = 0; slot < seatCount; slot++)
        {
            var found = false;
            foreach (var player in players)
            {
                if (player.Slot != slot) continue;
                WriteRoomPlayerInfo(w, player);
                found = true;
                break;
            }
            if (!found)
                (RoomPlayerInfo.ForSeat(0, string.Empty, checked((byte)slot)) with { Unknown0 = 0 }).Write(w);
        }
        if (profileEntries is null)
        {
            WireShapes.WriteEmptyVector(w); // map564
            WireShapes.WriteEmptyVector(w); // map588
        }
        else
        {
            w.WriteUInt32((uint)profileEntries.Count);
            foreach (var entry in profileEntries)
                entry.Write(w);
            WireShapes.WriteEmptyVector(w); // map588
        }

        if (team is null)
            WriteEmptyServerTeamInfo(w);
        else
            team.Write(w);
        WireShapes.WriteOptionalAbsent(w); // opt648
        w.WriteInt32(localSlot); // parsed +728
        w.WriteBool32(false); // unknown732
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 501 room-info snapshot. Handler <c>sub_113B7B0</c> + deserializer <c>sub_113BD20</c>
    /// (dispatcher <c>sub_9D9930</c> case 0x1F5): the body is exactly one <c>SessionSetting</c> — no player
    /// list, no team panel, nothing after the blob. The client applies it to the room stage when
    /// <c>*(session+72) == 2</c>, which is <c>kind</c> at object +64, so the room-stage gate kind travels in
    /// this body. Evidence: verified body shape and read order; the wire order matches
    /// <c>SessionSettingBlob.Write</c> row for row. </summary> <param name="setting">The room session setting;
    /// <c>kind</c> 2 is the room-stage gate.</param>
    public static byte[] RoomInfoSnapshot(SessionSettingBlob setting)
    {
        var w = PacketWriter.Create();
        setting.Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 505, the in-place map update: exactly one <c>MapMeta</c>, the record the dispatcher case
    /// at <c>sub_9D9930</c> deep-copies into <c>session+140</c> before redrawing the mode/map board. A
    /// map change travels on this frame - never on an S2C 117, whose kind-2 install deletes and rebuilds the
    /// room. </summary> <param name="map">The room's updated map metadata.</param> <returns>The
    /// payload.</returns>
    public static byte[] RoomMapUpdate(MapMetaBlob map)
    {
        var w = PacketWriter.Create();
        map.Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 517 join handshake. Handler <c>sub_9DF920</c> at <c>0x9DF920</c> (dispatcher
    /// <c>sub_9D9930</c>, case line 220): the body is exactly one <c>i32</c> token read off the stream. The
    /// handler then builds the S2C 518 reply with that token echoed first, so the value is ours to choose and
    /// the client only checks the echo. Evidence: verified body (<c>kr-1256 sub_9DF920</c>). </summary> <param
    /// name="token">The handshake token the S2C 518 reply must echo.</param>
    public static byte[] JoinHandshake(int token)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(token);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 518 join slot update. Inline case at <c>sub_9D9930</c> line 223:
    /// four <c>i32</c> written as the echoed S2C 517 token, a literal 0,
    /// <c>dword_18E8F00</c> and <c>*(MainApp+72)</c>. The last two are client
    /// globals this emulator does not model; they are written as 0, which the
    /// handler stores without a proven gate. Evidence: verified order and the
    /// literal-zero second field (sub_9DF920 lines 49-56).
    ///
    /// </summary>
    /// <param name="token">The token echoed from the S2C 517 handshake.</param>
    /// <param name="unknown8">Third <c>i32</c>; the client's <c>dword_18E8F00</c>, unmodeled here.</param>
    /// <param name="unknown12">Fourth <c>i32</c>; the client's <c>*(MainApp+72)</c>, unmodeled here.</param>
    public static byte[] JoinSlotUpdate(int token, int unknown8, int unknown12)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(token);
        w.WriteInt32(0);
        w.WriteInt32(unknown8);
        w.WriteInt32(unknown12);
        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 118 <c>gspRepWaitUserList</c>. Parser <c>sub_10D4B50</c>: <c>u32 count</c> then
    /// <c>count</c> times (prefixed wstring, <c>i32</c>). Evidence: verified.
    ///
    /// </summary>
    /// <param name="entries">Waiting-room rows. The second field is the player level: <c>sub_EE9550</c> builds the row's <c>LevelSymbolIcon</c> from <c>level - 1</c>, so a zero row level makes the icon negative and the row invisible.</param>
    public static byte[] WaitUserList(IReadOnlyList<RoomPlayerIdentity> entries)
    {
        var w = PacketWriter.Create();
        w.WriteUInt32((uint)entries.Count);
        foreach (var entry in entries)
        {
            w.WriteWideStringPrefixed(entry.Nickname);
            // sub_EE9550 stores level - 1; a zero level yields -1 and the row
            // never renders. Floor at 1 so the icon is always valid.
            w.WriteInt32(Math.Max(1, entry.Level));
        }

        return WireUtil.Written(w);
    }

    /// <summary>
    /// S2C 201 <c>gspUpdateParty</c> for one seated player. Handler
    /// <c>sub_9064B0</c> writes a profile-map record per party member
    /// (<c>sub_9064B0</c>, through <c>sub_81CCE0</c>) and then spawns and
    /// dresses a Character per occupied seat through <c>sub_920940</c>
    /// (<c>sub_9064B0</c>, gated on the stage not being 5) - the same
    /// spawner the login burst uses to render the plaza character. That is why
    /// the room-entry burst carries it: it is the packet that turns
    /// the seat list the 117 installed into visible characters.
    ///
    /// </summary>
    /// <param name="userNum">The member identity; <c>sub_906490</c> requires it to equal the local <c>UserNum</c>.</param>
    /// <param name="nickname">The member display name.</param>
    /// <param name="characterId">The equipped character id written as the profile-map entry payload.</param>
    /// <param name="equippedItemIds">Committed worn items for vecA; the character is encoded separately.</param>
    /// <returns>The S2C 201 body for that member.</returns>
    public static byte[] UpdatePartySolo(int userNum, string nickname, int characterId,
        IReadOnlyList<int>? equippedItemIds = null)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(2);                                     // dword_18E5660+4; sub_99D510 requires 2
        w.WriteUInt32(1);                                    // member count
        w.WriteInt32(userNum);                               // member +4; sub_906490 vs user-info +48
        w.WriteWideStringPrefixed(nickname);                 // member +8
        for (var i = 0; i < 4; i++)
            w.WriteInt32(0);                                 // member +32..+44
        w.WriteUInt32(1);                                    // profile-map entry count
        w.WriteInt32(userNum);                               // entry key
        w.WriteInt32(characterId);                           // entry payload: the equipped character id
        var items = equippedItemIds?.Where(id => id != characterId).ToArray() ?? [];
        w.WriteUInt32((uint)items.Length);                    // 0x817DF0 clears and replays vecA
        foreach (var item in items) w.WriteInt32(item);
        w.WriteUInt32(0);                                    // entry vecB
        w.WriteUInt32(0);                                    // entry vecC
        w.WriteUInt32(0);                                    // entry blob: zero-length counted vector
        w.WriteUInt32(0);                                    // entry items
        return WireUtil.Written(w);
    }

    /// <summary>
    /// Writes one <c>RoomPlayerInfo</c> (<c>sub_1143710</c>) seat record for an
    /// identity. The body is produced by <see cref="RoomPlayerInfo"/> so the
    /// S2C 117 seat list and the S2C 503 create reply can never drift apart.
    ///
    /// </summary>
    /// <param name="w">Destination writer.</param>
    /// <param name="player">Seat identity.</param>
    /// <remarks>
    /// The record's first dword is the occupancy value: <c>sub_9EE100</c>
    /// (called from the S2C 503 handler) walks the 320-byte seat array at
    /// <c>session+592</c> and clears <c>session+1984</c> — the occupancy flag
    /// the room member list reads — when both bytes at
    /// <c>session+592+320*slot</c> and <c>+593</c> are zero. Those two bytes
    /// are the low half of that dword, so a seat whose record starts with a
    /// zero dword is invisible in the member list. Evidence: verified read
    /// site (<c>sub_9EE100</c> lines 12-26) and the seat-array base
    /// (<c>sub_9D9250</c> line 114, <c>this+148</c>).
    /// </remarks>
    public static void WriteRoomPlayerInfo(PacketWriter w, RoomPlayerIdentity player) =>
        player.ToPlayerInfo().Write(w);

    /// <summary>
    /// Empty <c>ServerTeamInfo</c> (<c>sub_1159550</c>): three zero counts. Evidence: verified empty form.
    ///
    /// </summary>
    /// <param name="w">Destination writer.</param>
    public static void WriteEmptyServerTeamInfo(PacketWriter w)
    {
        WireShapes.WriteEmptyVector(w);
        WireShapes.WriteEmptyVector(w);
        WireShapes.WriteEmptyVector(w);
    }
}
