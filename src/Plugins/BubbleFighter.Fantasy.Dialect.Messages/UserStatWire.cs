using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Writes the native user state shared by login and wallet refreshes.</summary>
internal static class UserStatWire
{
    // kr-1256 sub_1135760 maps the login character type to the equipped character item; the modern login
    // codec writes its Dao value (1) at FighterInfo +32 and the display name at +504.
    private const int DaoCharacterType = 1;

    // kr-812 sub_81A820 updates wallet counters; kr-812 sub_87DD90 consumes nickname and level.
    // The modern family's handler kr-1256 sub_9D8030 reads the whole FighterInfo the modern login codec
    // writes (FighterInfo@kr-1256), so that family carries the identity, character type and name rows.
    internal static byte[] Write(PacketTables tables, string build, string nickname, int level, int lucci, int cash,
        ushort colorIndex = 0, long userNum = 0)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["color_index"] = colorIndex, ["lucci"] = lucci, ["cash"] = cash,
        };
        if (HasModernFighterRows(tables, build))
        {
            values["fighter"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unknown_4"] = checked((int)userNum),
                ["nickname"] = nickname,
                ["unknown_32"] = DaoCharacterType,
                ["level"] = level,
                ["unknown_504"] = nickname,
                // The social block keeps its constructor defaults; its own +4 must not take the identity above.
                ["social"] = new Dictionary<string, object?>(StringComparer.Ordinal),
            };
        }
        else
        {
            // The legacy FighterInfo takes the name and level from the enclosing map.
            values["nickname"] = nickname;
            values["level"] = level;
        }
        return PacketCodec.Write(tables, build, "UserStat", values);
    }

    // The modern rows of FighterInfo (+4 identity, +32 character type, +504 display name) are the build's own
    // table statement: the FighterInfo reader kr-1256 sub_115A6D0 reads them and the gspUserStat reader
    // kr-1256 sub_9D8030 consumes the whole block. A build whose table has no +504 row keeps the legacy bytes (kr-812
    // sub_40BA50 reads only +4/+8/+36/+40), so the zero at its FighterInfo +4 stays zero.
    private static bool HasModernFighterRows(PacketTables tables, string build) =>
        tables.Resolve(build, "FighterInfo").Any(field => string.Equals(field.Name, "unknown_504", StringComparison.Ordinal));
}
