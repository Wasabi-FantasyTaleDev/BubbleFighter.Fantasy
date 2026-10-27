using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins restriction, alarm and disconnect bodies against their client readers, including both strings
/// consumed by kr-1256 sub_905EF0 and the empty moved-user case kr-1256 0x9D34FF.
/// </summary>
public sealed class AdminBanTableTests
{
    /// <summary>The legacy restriction body preserves unknown words and eight-byte values without interpreting them as dates.</summary>
    [Theory]
    [InlineData("kr-812")]
    public void RestrictionDisconnectRetainsTwoOpaqueEightByteValues(string build)
    {
        // 1, 2, 3, 4, "" : 4 + 8 + 8 + 4 + 4 = 28 bytes.
        var empty = Convert.FromHexString("01000000" + "0200000000000000" + "0300000000000000" + "04000000" + "00000000");
        Assert.Equal(28, empty.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "DisconnectFromRestriction", empty);
        Assert.Equal(empty, PacketCodec.Write(TestBuilds.Packets, build, "DisconnectFromRestriction", decoded));

        // The same with "hi" (two characters): 32 bytes.
        var text = Convert.FromHexString("01000000" + "0200000000000000" + "0300000000000000" + "04000000" + "02000000" + "68006900");
        Assert.Equal(32, text.Length);
        var withText = PacketCodec.Read(TestBuilds.Packets, build, "DisconnectFromRestriction", text);
        Assert.Equal("hi", Assert.IsType<string>(withText["unknown_28"]));
        Assert.Equal(text, PacketCodec.Write(TestBuilds.Packets, build, "DisconnectFromRestriction", withText));

        foreach (var length in new[] { 0, 4, 12, 24, 27, 29 })
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "DisconnectFromRestriction", new byte[length]));
    }

    /// <summary>Both client families consume two counted strings, including their length prefixes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void BlockIpDisconnectReadsTwoStrings(string build)
    {
        // "" and "": 8 bytes.
        var empty = Convert.FromHexString("00000000" + "00000000");
        Assert.Equal(8, empty.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "DisconnectBlockIP", empty);
        Assert.Equal(empty, PacketCodec.Write(TestBuilds.Packets, build, "DisconnectBlockIP", decoded));

        // "a" and "bc": 4 + 2 + 4 + 4 = 14 bytes.
        var text = Convert.FromHexString("01000000" + "6100" + "02000000" + "62006300");
        Assert.Equal(14, text.Length);
        Assert.Equal(text, PacketCodec.Write(TestBuilds.Packets, build, "DisconnectBlockIP",
            PacketCodec.Read(TestBuilds.Packets, build, "DisconnectBlockIP", text)));

        foreach (var length in new[] { 0, 4, 7, 9 })
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "DisconnectBlockIP", new byte[length]));
    }

    /// <summary>Both alarm readers consume exactly one counted UTF-16 string.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void OveruseAlarmIsOneStringOnBothFamilies(string build)
    {
        foreach (var (hex, expected) in new[] { ("00000000", ""), ("02000000" + "68006900", "hi") })
        {
            var body = Convert.FromHexString(hex);
            Assert.Equal(4 + 2 * expected.Length, body.Length);
            var decoded = PacketCodec.Read(TestBuilds.Packets, build, "OveruseAlarmMsg", body);
            Assert.Equal(expected, Assert.IsType<string>(decoded["unknown_4"]));
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "OveruseAlarmMsg", decoded));
        }

        foreach (var length in new[] { 0, 3, 5 })
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "OveruseAlarmMsg", new byte[length]));
    }

    /// <summary>Modern notices preserve the sync word, both web strings and the empty moved-user frame.</summary>
    [Theory]
    [InlineData("kr-1256")]
    public void ModernNoticesRetainTheirCompleteBodies(string build)
    {
        // gspOveruseSync: one word.
        var sync = Convert.FromHexString("01000000");
        Assert.Equal(4, sync.Length);
        Assert.Equal(sync, PacketCodec.Write(TestBuilds.Packets, build, "OveruseSync",
            PacketCodec.Read(TestBuilds.Packets, build, "OveruseSync", sync)));

        // gspDisconnectOpenWeb: two counted strings, with the second empty in these fixtures.
        foreach (var (hex, expected) in new[] { ("0000000000000000", ""), ("020000006800690000000000", "hi") })
        {
            var body = Convert.FromHexString(hex);
            var decoded = PacketCodec.Read(TestBuilds.Packets, build, "DisconnectOpenWeb", body);
            Assert.Equal(expected, Assert.IsType<string>(decoded["unknown_0"]));
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "DisconnectOpenWeb", decoded));
        }

        // The moved-user notice carries no byte, and a non-empty body is rejected.
        foreach (var structure in new[] { "DisconnectMovedUser" })
        {
            Assert.Empty(PacketCodec.Read(TestBuilds.Packets, build, structure, ReadOnlySpan<byte>.Empty));
            Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, structure, new Dictionary<string, object?>()));
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, structure, new byte[] { 0, 0, 0, 0 }));
        }

        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "OveruseSync", new byte[3]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "OveruseSync", new byte[5]));
    }
}
