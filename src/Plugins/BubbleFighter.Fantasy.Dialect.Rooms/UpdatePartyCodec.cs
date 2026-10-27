using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Legacy S2C 201 <c>gspUpdateParty</c> solo idle snapshot shared by the plaza-capable
/// builds. Packet readers (i32 state, member vector, equipped vector): cn-279
/// <c>0x4802A0</c>, cn-324 <c>0x7C6B00</c>, id-400 <c>0x89DCE0</c>, id-1809 <c>0x8C2520</c>, kr-812
/// <c>0x92B130</c>, cn-717 <c>0x9CD590</c>. Handlers store state at <c>Party+4</c>
/// (cn-279 <c>0x8326D0</c>, cn-324 <c>0x7C7430</c>, id-400 <c>0x89E3A0</c>,
/// kr-812 <c>0x926900</c>, cn-717 <c>0x9C7E60</c>). cn-706 dispatches the labelled
/// <c>gspUpdateParty</c> case 0xC9 to <c>sub_8AEB60</c>.
/// </summary>
internal static class UpdatePartyCodec
{
    /// <summary>
    /// Idle party state required by MainMenu plaza (<c>ecx = 2</c> gate) before
    /// <c>gcpReqGotoLobby</c>. Gates: cn-279 <c>0x6F9E50</c>, cn-324 <c>0x6FFE70</c>,
    /// id-400 <c>0x79BED0</c>, kr-812 <c>0x812AD0</c>, cn-717 <c>0x8980D0</c>. When
    /// <c>Party+4 != 2</c> they show the client string "파티가 이동중입니다.".
    /// </summary>
    public const int IdleState = 2;

    /// <summary>
    /// Writes a one-member idle party for the local player. Member readers
    /// (i32 user number, wstr nickname, three i32): cn-279 <c>0x44E7E0</c>,
    /// cn-324 <c>0x7987C0</c>, id-400 <c>0x86D6E0</c>, kr-812 <c>0x415C00</c>,
    /// cn-717 <c>0x8CFA40</c>. The equipped vector is empty (count 0); element
    /// strides differ by build but are not read when the count is zero.
    /// </summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build whose solo snapshot is written.</param>
    /// <param name="room">The main-menu player identity.</param>
    /// <returns>The S2C 201 body.</returns>
    public static byte[] WriteSolo(PacketTables tables, string build, MainMenuRoomOpened room)
    {
        ValidateLayouts(tables, build);
        return PacketCodec.Write(tables, build, "SoloPartySnapshot", new Dictionary<string, object?>
        {
            ["state"] = IdleState,
            ["members"] = new Dictionary<string, object?>[]
            {
                new() { ["user_number"] = room.UserNumber, ["nickname"] = room.Nickname },
            },
        });
    }

    /// <summary>The same snapshot for a whole party, leader first (kr-812 sub_926900 takes member[0] as the leader).</summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    /// <param name="party">The party.</param>
    /// <returns>The S2C 201 body.</returns>
    public static byte[] WriteParty(PacketTables tables, string build, PartySnapshot party)
    {
        ValidateLayouts(tables, build);
        return PacketCodec.Write(tables, build, "SoloPartySnapshot", new Dictionary<string, object?>
        {
            ["state"] = IdleState,
            ["members"] = party.Members.Select(member => new Dictionary<string, object?>
            {
                ["user_number"] = member.UserNum, ["nickname"] = member.Nickname,
            }).ToArray(),
        });
    }

    /// <summary>
    /// Checks the two layouts the writers size. The snapshot has one variant that adds a profile-entries vector, so
    /// only the state word and the member vector are required; each member row is two fields, the words the writers
    /// fill. This helper has no bind step of its own, so it checks before every use rather than on the first send.
    /// </summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    internal static void ValidateLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "SoloPartySnapshot", "Solo party snapshot",
            ("state", "u32"), ("members", "list<SoloPartyMember>"));
        PacketLayoutContract.RequireFields(tables, build, "SoloPartyMember", "Solo party snapshot",
            ("user_number", "u32"), ("nickname", "wstr"));
    }

    /// <summary>
    /// Whether this room schema's MainMenu plaza gate needs the solo idle
    /// <c>gspUpdateParty</c> before <c>gcpReqGotoLobby</c> can leave.
    /// </summary>
    /// <param name="schema">The build's room layout.</param>
    /// <returns><see langword="true"/> for the proven plaza gates.</returns>
    public static bool NeedsIdleParty(RoomSchema schema) => schema.NeedsIdleParty;
}
