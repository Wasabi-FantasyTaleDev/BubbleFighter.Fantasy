using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete nested bodies and prevents legacy structures from leaking into modern builds.</summary>
public sealed class ReviewedFeatureTablePins
{
    /// <summary>Modern builds must not inherit legacy bodies whose readers have not been established.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernBuildsExcludeUnestablishedLegacyShapes(string build)
    {
        foreach (var name in new[] { "GameScoreData", "PlayerGameScoreData", "GameItemSpec", "ItemDropInfo",
            "GuildLineBoardRequest", "GuildLineBoardWriteRequest", "GuildLineBoardReply", "GuildLineBoardRecord",
            "GuildLineBoardResultReply", "DisconnectFromRestriction", "SecondPasswordRequest", "RoomRecruitRequest", "RoomRecruitReply" })
            Assert.Empty(TestBuilds.Packets.Resolve(build, name));
    }

    /// <summary>The ladder reply consumes its eight-byte vector and both complete record vectors before its final word.</summary>
    [Fact]
    public void LadderReplyRetainsAllNestedCollections()
    {
        // kr-812 sub_83AF00, kr-812 sub_83AEC0 and kr-812 sub_416820 establish this order.
        var hex = "01000000" + "0100000002000000" + "01000000" + "010000000300000000000000"
            + "01000000" + "01000000410004000000010000004200050000000600000007000000"
            + "01000000" + "000000000800000000000000090000000A0000000B000000" + "0C000000";
        Check("kr-812", "SeasonRankList", hex, 92);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-812", "SeasonRankList",
            Convert.FromHexString("010000000100000002000000010000000C000000")));
    }

    /// <summary>The block-add result always carries a string, whether or not its tagged word is present.</summary>
    [Theory]
    [InlineData("00010000004100", 7)]
    [InlineData("3A07000000010000004100", 11)]
    public void BlockResultRetainsTheRequiredString(string hex, int length)
    {
        Check("kr-1256", "SocialBlockAddResult", hex, length);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-1256", "SocialBlockAddResult",
            Convert.FromHexString("3A07000000")));
    }

    /// <summary>Both web and IP-block notices consume a second nonempty string through kr-1256 sub_905EF0.</summary>
    [Theory]
    [InlineData("DisconnectBlockIP")]
    [InlineData("DisconnectOpenWeb")]
    public void ModernDisconnectsRetainBothStrings(string structure)
    {
        Check("kr-1256", structure, "0100000041000200000042004300", 14);
    }

    /// <summary>An outer skill key must be followed by its nested count, even when the inner list is empty.</summary>
    [Fact]
    public void SkillGroupsCannotBeFlattenedToWords()
    {
        Check("kr-1256", "SkillSlotChanged", "01000000010000000200000000000000", 16);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-1256", "SkillSlotChanged",
            Convert.FromHexString("01000000020000000500000006000000")));
    }

    private static void Check(string build, string structure, string hex, int length)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(length, body.Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, fields));
        for (var i = 0; i < body.Length; i++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, body[..i]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
    }
}
