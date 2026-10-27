using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the social safety bodies in <c>packets/social-safety.yaml</c>: the legacy recommend and commend
/// requests and their replies, plus the kr-1256 block result and commend notice whose read order was observed.
/// Every body has its exact length, reads back through <see cref="PacketCodec"/> unchanged, and is refused one
/// byte short and one byte long; the word-carrying bodies are written out in full hex.
/// </summary>
public sealed class SocialSafetyTablePins
{
    private const string LegacyBuild = "kr-812";
    private const string ModernBuild = "kr-1256";

    /// <summary>Every tabled body keeps its length and round-trips.</summary>
    [Fact]
    public void TheSocialSafetyBodiesMatchTheirLengthsAndRoundTrip()
    {
        // Packet_gcpReqRecommendSend, deserializer kr-812 sub_5C8FD0: one string.
        Check(LegacyBuild, "SocialRecommendRequest", 6, ("unknown_4", "A"));
        // Packet_gcpReqSocialSystemCommend, deserializer kr-812 sub_52F4A0: four words and a four-byte boolean word.
        Check(LegacyBuild, "SocialCommendRequest", 20,
            ("unknown_4", 1), ("unknown_8", 2), ("unknown_12", 3), ("unknown_16", 4), ("unknown_20", true));
        // Packet_gspRepRecommendResult, deserializer kr-812 sub_80C160: one flag.
        Check(LegacyBuild, "SocialRecommendResult", 4, ("unknown_4", true));
        // Packet_gspNotifySocialSystem, deserializer kr-812 sub_6DB1B0: two words.
        Check(LegacyBuild, "SocialSystemNotice", 8, ("unknown_4", 5), ("unknown_8", 6));
        // kr-1256 171, handler kr-1256 sub_9D0AC0 -> kr-1256 sub_10C0640: one tag byte, then one word only
        // when the tag is 0x3A, then an unconditional counted string.
        Check(ModernBuild, "SocialBlockAddResult", 5);
        Check(ModernBuild, "SocialBlockAddResult", 9,
            ("unknown_0", new Dictionary<string, object?> { ["unknown_1"] = 7 }));
        // kr-1256 143, handler kr-1256 sub_9C44D0: four words, no object.
        Check(ModernBuild, "SocialCommendNotice", 16,
            ("unknown_0", 1), ("unknown_4", 2), ("unknown_8", 3), ("unknown_12", 4));
    }

    /// <summary>The word-carrying bodies are pinned byte for byte, so a swapped word fails.</summary>
    [Fact]
    public void TheWordBodiesArePinnedInHex()
    {
        var commend = PacketCodec.Write(TestBuilds.Packets, LegacyBuild, "SocialCommendRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = 1, ["unknown_8"] = 2, ["unknown_12"] = 3, ["unknown_16"] = 4, ["unknown_20"] = true,
        });
        Assert.Equal("0100000002000000030000000400000001000000", Convert.ToHexString(commend));
        Assert.Equal(20, commend.Length);

        var notice = PacketCodec.Write(TestBuilds.Packets, ModernBuild, "SocialCommendNotice", new Dictionary<string, object?>
        {
            ["unknown_0"] = 1, ["unknown_4"] = 2, ["unknown_8"] = 3, ["unknown_12"] = 4,
        });
        Assert.Equal("01000000020000000300000004000000", Convert.ToHexString(notice));
        Assert.Equal(16, notice.Length);

        var blocked = PacketCodec.Write(TestBuilds.Packets, ModernBuild, "SocialBlockAddResult",
            new Dictionary<string, object?> { ["unknown_0"] = new Dictionary<string, object?> { ["unknown_1"] = 7 } });
        Assert.Equal("3A0700000000000000", Convert.ToHexString(blocked));
        Assert.Equal(9, blocked.Length);
        var blockedAbsent = PacketCodec.Write(TestBuilds.Packets, ModernBuild, "SocialBlockAddResult",
            new Dictionary<string, object?>());
        Assert.Equal("0000000000", Convert.ToHexString(blockedAbsent));
        Assert.Equal(5, blockedAbsent.Length);
    }

    /// <summary>Each half resolves only for its corresponding build family.</summary>
    [Fact]
    public void EachHalfIsScopedToItsBuild()
    {
        foreach (var structure in new[] { "SocialRecommendRequest", "SocialCommendRequest", "SocialRecommendResult", "SocialSystemNotice" })
        {
            Assert.NotEmpty(TestBuilds.Packets.Resolve(LegacyBuild, structure));
            Assert.Empty(TestBuilds.Packets.Resolve(ModernBuild, structure));
            Assert.Empty(TestBuilds.Packets.Resolve("cn-774", structure));
        }
        foreach (var structure in new[] { "SocialBlockAddResult", "SocialBlockAddWord", "SocialCommendNotice" })
        {
            Assert.NotEmpty(TestBuilds.Packets.Resolve(ModernBuild, structure));
            Assert.Empty(TestBuilds.Packets.Resolve(LegacyBuild, structure));
        }
    }

    private static void Check(string build, string structure, int length, params (string Name, object? Value)[] fields)
    {
        var values = fields.ToDictionary(field => field.Name, field => field.Value);
        var body = PacketCodec.Write(TestBuilds.Packets, build, structure, values);
        Assert.Equal(length, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
    }
}
