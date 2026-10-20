using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>S2C 105 (<c>UserStat</c>) as <c>packets/user-stat.yaml</c> writes it for every legacy build.</summary>
public sealed class UserStatTableTests
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    // SHA-256 prefixes of the bodies the per-build C# writers produced before the table replaced them.
    [Theory]
    [InlineData("kr-812", "4D2AD23A3BAD870255DF944E5079F781", "9BBE94049DD5F3809317C391E7EA4524", 380)]
    [InlineData("cn-717", "4D2AD23A3BAD870255DF944E5079F781", "9BBE94049DD5F3809317C391E7EA4524", 380)]
    [InlineData("id-400", "28AB75ED591270BD00498FEB3F787325", "7E45303EDC17AC16EFB583FFB4076F5F", 340)]
    [InlineData("id-1809", "6B27F5E49F0C2A796C83D40FA7688E64", "D7205FEDA06F1B23DAF6C490ECF1AB3D", 344)]
    [InlineData("cn-324", "EC61E889EAE716C0090957807A491B79", "71538C468DD35402F4861F37DA163692", 256)]
    [InlineData("cn-279", "26A2DDF1DA5BEE8B16F6A7C50AE2A1B8", "497F88CC5B0815ED280AD292F60BF64F", 168)]
    public void EveryBuildWritesTheBytesOfItsFormerWriter(string build, string named, string empty, int length)
    {
        var bytes = ShopBinding.WriteStat(Tables, build, "", 1, 0, 0);
        Assert.Equal(length, bytes.Length);
        Assert.Equal(empty, Hash(bytes));
        Assert.Equal(named, Hash(ShopBinding.WriteStat(Tables, build, "ab", 5, 7, 9)));
    }

    [Fact]
    public void Kr812FieldsLandAtTheirClientObjectBoundaries()
    {
        var bytes = PacketCodec.Write(Tables, "kr-812", "UserStat", Value());
        Assert.Equal(380, bytes.Length);
        Assert.Equal(5, IntAt(bytes, 16));
        Assert.Equal(6, IntAt(bytes, 20));
        Assert.Equal(8, IntAt(bytes, 28));
        Assert.Equal(0, IntAt(bytes, 32));
        Assert.Equal(10, IntAt(bytes, 36));
        Assert.Equal(101, IntAt(bytes, 40));
        Assert.Equal(103, IntAt(bytes, 48));
        Assert.Equal(105, IntAt(bytes, 56));
        Assert.Equal(0x0102030405060708L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(60)));
        Assert.Equal(107, IntAt(bytes, 68));
        Assert.Equal(unchecked((long)0xFEDCBA9876543210), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(72)));
        Assert.Equal(109, IntAt(bytes, 80));
        Assert.Equal(1, IntAt(bytes, 84)); // kr-812 sub_A56BD0 consumes four bytes for the bool stored at +76.
        Assert.Equal(111, IntAt(bytes, 88));
        Assert.Equal(159, IntAt(bytes, 280));
        Assert.Equal(0, IntAt(bytes, 284));
        Assert.Equal(161, IntAt(bytes, 288));
        Assert.Equal(165, IntAt(bytes, 304));
        Assert.Equal(166, IntAt(bytes, 308));
        Assert.Equal(173, IntAt(bytes, 336));
        Assert.Equal(174, IntAt(bytes, 340));
        Assert.Equal(181, IntAt(bytes, 368));
        Assert.Equal(0, IntAt(bytes, 372));
        Assert.Equal(183, IntAt(bytes, 376));
    }

    [Fact]
    public void Kr812ReferenceShapeIs428BytesWithTwoTwelveUnitStrings()
    {
        // Synthetic strings preserve only the verified shape.
        var value = Value();
        value["unknown_36"] = "ABCDEFGHIJKL";
        Fighter(value)["nickname"] = "MNOPQRSTUVWX";
        var bytes = PacketCodec.Write(Tables, "kr-812", "UserStat", value);
        Assert.Equal(428, bytes.Length);
        Assert.Equal(12, IntAt(bytes, 32));
        Assert.Equal(12, IntAt(bytes, 68));
        Assert.Equal(0, IntAt(bytes, 332));
        Assert.Equal(183, IntAt(bytes, 424));
    }

    [Fact]
    public void Kr812StringsAndRatingListKeepIndependentWidths()
    {
        var value = Value();
        value["unknown_36"] = "A漢";
        var fighter = Fighter(value);
        fighter["nickname"] = "B";
        fighter["unknown_76"] = false;
        fighter["unknown_284"] = new[] { Rating(-1, 1000), Rating(17, 2000) };
        fighter["unknown_392"] = "\U00020000";
        var bytes = PacketCodec.Write(Tables, "kr-812", "UserStat", value);
        Assert.Equal(406, bytes.Length);
        Assert.Equal(2, IntAt(bytes, 32));
        Assert.Equal("A漢", Encoding.Unicode.GetString(bytes, 36, 4));
        Assert.Equal(0, IntAt(bytes, 90));
        Assert.Equal(2, IntAt(bytes, 290));
        Assert.Equal([-1, 1000, 17, 2000, 161], new[] { 294, 298, 302, 306, 310 }.Select(offset => IntAt(bytes, offset)));
        Assert.Equal(2, IntAt(bytes, 394));
        Assert.Equal("\U00020000", Encoding.Unicode.GetString(bytes, 398, 4));
        Assert.Equal(183, IntAt(bytes, 402));
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketCodec.Write(Tables, "kr-812", "UserStat", value, maxBytes: 405));
    }

    [Fact]
    public void ARunOfTheWrongLengthIsRefused()
    {
        var value = Value();
        Fighter(value)["unknown_80"] = new int[48];
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(Tables, "kr-812", "UserStat", value));
    }

    [Fact]
    public void Id400KeepsItsOwnRatingDefaultsAndHasNoKr812SocialTail()
    {
        var stat = ShopBinding.WriteStat(Tables, "id-400", "", 1, 0, 0);
        Assert.Equal(0, IntAt(stat, 284)); // the empty rating list follows FighterInfo +272
        Assert.Equal([1000, 0, 1], new[] { 288, 292, 296 }.Select(offset => IntAt(stat, offset)));
        Assert.Equal(new byte[40], stat.AsSpan(300).ToArray());
    }

    [Fact]
    public void Id1809IsTheId400BodyPlusOneTrailingInteger()
    {
        var id400 = ShopBinding.WriteStat(Tables, "id-400", "ab", 5, 7, 9);
        var id1809 = ShopBinding.WriteStat(Tables, "id-1809", "ab", 5, 7, 9);
        Assert.Equal(id400, id1809.AsSpan(0, id400.Length).ToArray());
        Assert.Equal(new byte[4], id1809.AsSpan(id400.Length).ToArray());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];

    private static int IntAt(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    private static Dictionary<string, object?> Fighter(Dictionary<string, object?> value) =>
        (Dictionary<string, object?>)value["fighter"]!;

    private static Dictionary<string, object?> Rating(int a, int b) => new() { ["unknown_4"] = a, ["unknown_8"] = b };

    // Every field carries a distinct value so a misplaced one shows.
    private static Dictionary<string, object?> Value() => new()
    {
        ["color_index"] = 1, ["unknown_8"] = 2, ["unknown_12"] = 3, ["unknown_16"] = 4, ["lucci"] = 5, ["cash"] = 6,
        ["unknown_28"] = 7, ["unknown_32"] = 8, ["unknown_36"] = "", ["unknown_64"] = 10,
        ["fighter"] = new Dictionary<string, object?>
        {
            ["unknown_4"] = 101, ["nickname"] = "", ["unknown_36"] = 103, ["level"] = 104, ["unknown_44"] = 105,
            ["unknown_48"] = 0x0102030405060708L, ["unknown_56"] = 107, ["unknown_64"] = unchecked((long)0xFEDCBA9876543210),
            ["unknown_72"] = 109, ["unknown_76"] = true, ["unknown_80"] = Enumerable.Range(111, 49).ToArray(),
            ["unknown_284"] = Array.Empty<object>(), ["unknown_276"] = 161, ["unknown_280"] = 162, ["unknown_300"] = 163,
            ["unknown_304"] = 164, ["unknown_312"] = 165, ["unknown_320"] = Enumerable.Range(166, 8).ToArray(),
            ["unknown_360"] = Enumerable.Range(174, 8).ToArray(), ["unknown_392"] = "", ["unknown_356"] = 183,
        },
    };
}
