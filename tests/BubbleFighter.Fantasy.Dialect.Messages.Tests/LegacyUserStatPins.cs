using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the legacy S2C <c>gspUserStat</c> (105) body of the shared dialect.messages row before slice S4b moves
/// the user stat onto the shared sender. The writer is <c>ShopBinding.WriteStat</c>/<c>UserStatWire</c> over
/// <c>packets/user-stat.yaml</c> (kr-812 sub_81A820 reads the counters and kr-812 sub_87DD90 the nested name and
/// level); a body that changes on purpose updates this pin together with the client reader that decides it.
/// </summary>
public sealed class LegacyUserStatPins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>
    /// A named kr-812 body: color 0, lucci 7, cash 9, nickname "ab" and level 5 in the nested FighterInfo,
    /// 384 bytes. The empty-nickname variant (380 bytes) stays hashed in <see cref="UserStatTableTests"/>.
    /// </summary>
    [Fact]
    public void Kr812NamedBodyIsPinned()
    {
        var body = ShopBinding.WriteStat(Tables, "kr-812", "ab", 5, 7, 9);
        Assert.Equal(384, body.Length);
        Assert.Equal(
            "00000000000000000000000000000000070000000900000000000000000000000000000000000000000000000200000061006200000000000500000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000E803000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            Convert.ToHexString(body));
    }
}
