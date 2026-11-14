using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the title and medal bodies from <c>packets/titles-medals.yaml</c>. The kr-812 shapes come from the
/// request senders and the reply readers; the kr-1256 title-sync list is pinned from its own reader chain.
/// Audited catalogue identities and concealment semantics remain distinct from installed ownership.
/// </summary>
public sealed class TitlesMedalsTableTests
{
    /// <summary>Legacy-only requests and replies do not imply a body for an unverified modern reader.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void UnverifiedModernShapesAreExcluded(string build)
    {
        foreach (var name in new[] { "MakeTitleRequest", "SetTitleRequest", "MakeTitleReply", "MakeTitleRewardReply", "SetTitleReply",
                     "ProvideMedal", "MedalInfoRequest", "MedalInfoReply", "ClanMarkChangeRequest", "PartyRecruitingRequest", "PartyQuickStartRequest",
                     "PartyChatRequest", "PartyChatMessage", "PartyJoinReply" })
        {
            Assert.Empty(TestBuilds.Packets.Resolve(build, name));
            Assert.NotEmpty(TestBuilds.Packets.Resolve("kr-812", name));
        }
        Assert.NotEmpty(TestBuilds.Packets.Resolve(build, "TitleSyncInfo"));
    }

    /// <summary>Two words: kr-812 sub_886040 writes both before stamping C2S 929.</summary>
    [Fact]
    public void MakeTitleRequestPinsTwoWords()
    {
        var expected = Convert.FromHexString("0100000002000000");
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "MakeTitleRequest", expected);
        Assert.Equal(1, Convert.ToInt32(fields["user_number"]));
        Assert.Equal(2, Convert.ToInt32(fields["title_id"]));
        Assert.Equal(8, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, "kr-812", "MakeTitleRequest", fields));
    }

    /// <summary>Three words: kr-812 sub_885F80 writes all three before stamping C2S 930.</summary>
    [Fact]
    public void SetTitleRequestPinsThreeWords()
    {
        var expected = Convert.FromHexString("010000000200000003000000");
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "SetTitleRequest", expected);
        Assert.Equal(1, Convert.ToInt32(fields["user_number"]));
        Assert.Equal(2, Convert.ToInt32(fields["action"]));
        Assert.Equal(3, Convert.ToInt32(fields["title_id"]));
        Assert.Equal(12, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, "kr-812", "SetTitleRequest", fields));
    }

    /// <summary>Two words and a counted UTF-16 string: reader kr-812 sub_8B9660.</summary>
    [Fact]
    public void MakeTitleReplyPinsTwoWordsAndTheCountedString()
    {
        var expected = Convert.FromHexString("0100000002000000020000004100A903");
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "MakeTitleReply", expected);
        Assert.Equal(1, Convert.ToInt32(fields["result"]));
        Assert.Equal(2, Convert.ToInt32(fields["stock_id"]));
        Assert.Equal("A\u03A9", fields["unknown_12"]);
        Assert.Equal(16, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, "kr-812", "MakeTitleReply", fields));
    }

    /// <summary>Three words: reader kr-812 sub_9D95D0.</summary>
    [Fact]
    public void SetTitleReplyPinsThreeWords()
    {
        var expected = Convert.FromHexString("010000000200000003000000");
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "SetTitleReply", expected);
        Assert.Equal(3, Convert.ToInt32(fields["unknown_12"]));
        Assert.Equal(12, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, "kr-812", "SetTitleReply", fields));
    }

    /// <summary>
    /// The title-sync body is a counted word list in both families: reader kr-812 sub_840820 and the
    /// kr-1256 chain kr-1256 sub_9B1B40, kr-1256 sub_998FB0 and kr-1256 sub_999C00.
    /// </summary>
    /// <param name="build">A build whose reader was read.</param>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void TitleSyncInfoPinsACountedWordList(string build)
    {
        var expected = Convert.FromHexString("020000000700000009000000");
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "TitleSyncInfo", expected);
        var words = Assert.IsAssignableFrom<IEnumerable<object?>>(fields["concealed_title_ids"]);
        Assert.Equal([7, 9], words.Select(value => Convert.ToInt32(value)));
        Assert.Equal(12, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, build, "TitleSyncInfo", fields));
    }

    /// <summary>Two words: the shared codec kr-812 sub_6DB1B0 behind kr-812 vtable 0xD6F6B4.</summary>
    [Fact]
    public void ProvideMedalPinsTwoWords()
    {
        var expected = Convert.FromHexString("0100000002000000");
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-812", "ProvideMedal", expected);
        Assert.Equal(2, Convert.ToInt32(fields["unknown_8"]));
        Assert.Equal(8, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, "kr-812", "ProvideMedal", fields));
    }

    /// <summary>The separate medal snapshot installs key/count pairs, not title identifiers or reward popups.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void MedalInfoPinsHeaderAndCountedPairs(string build)
    {
        var expected = Convert.FromHexString("0100000007000000020000003775000014000000387500001E000000");
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "MedalInfoReply", expected);
        Assert.Equal(1, fields["result"]);
        Assert.Equal(7, fields["unknown_8"]);
        var rows = Assert.IsAssignableFrom<IEnumerable<object?>>(fields["medals"]).ToArray();
        Assert.Equal(2, rows.Length);
        var first = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(rows[0]);
        Assert.Equal(30007, first["medal_id"]);
        Assert.Equal(20, first["count"]);
        Assert.Equal(28, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, build, "MedalInfoReply", fields));
        for (var length = 0; length < expected.Length; length++)
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "MedalInfoReply", expected[..length]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "MedalInfoReply", [.. expected, 0]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "MedalInfoReply",
            Convert.FromHexString("0100000000000000FFFFFFFF")));
    }
}
