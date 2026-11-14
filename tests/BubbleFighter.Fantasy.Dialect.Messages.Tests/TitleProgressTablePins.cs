using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the title condition progress notice (S2C gspNotifyUserTitleInstance) of <c>packets/titles-medals.yaml</c>: a counted list of
/// title records read by kr-1256 sub_10CF5D0 and kr-1256 sub_113DC60, each with a counted list of three-word condition rows.
/// </summary>
public sealed class TitleProgressTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The body is four bytes plus sixteen per title plus twelve per condition row, with the outer words zero.</summary>
    [Fact]
    public void ProgressBodyPinsTheNestedRows()
    {
        var body = TitleProgressBinding.Encode(Tables, "kr-1256", new(
        [
            new(400312, [new(6, 1, 1)]),
            new(400313, [new(6, 1, 2)]),
        ]));
        Assert.Equal(4 + 2 * 16 + 2 * 12, body.Length);
        Assert.Equal("02000000"
            + "B81B0600" + "00000000" + "00000000" + "01000000" + "06000000" + "01000000" + "01000000"
            + "B91B0600" + "00000000" + "00000000" + "01000000" + "06000000" + "01000000" + "02000000", Convert.ToHexString(body));
        Assert.Equal("00000000", Convert.ToHexString(TitleProgressBinding.Encode(Tables, "kr-1256", new([]))));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "TitleInstanceNotice", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "TitleInstanceNotice", [.. body, 0]));
        Assert.Throws<ArgumentException>(() => TitleProgressBinding.Encode(Tables, "kr-1256", new([new(400312, [new(6, 1, 0)])])));
        Assert.Throws<ArgumentException>(() => TitleProgressBinding.Encode(Tables, "kr-1256", new([new(0, [])])));
    }

    /// <summary>Only kr-1256 states the structures; every other build, including those that inherit its table, resolves them empty.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void OtherBuildsResolveTheStructuresEmpty(string build)
    {
        Assert.Empty(Tables.Resolve(build, "TitleInstanceNotice"));
        Assert.Empty(Tables.Resolve(build, "TitleInstance"));
        Assert.Empty(Tables.Resolve(build, "TitleConditionProgress"));
        Assert.Empty(Tables.Resolve(build, "FavoriteWeaponGrade"));
    }
}