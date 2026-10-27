using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-812 couple bodies in <c>packets/couple.yaml</c>: every body has its exact length, reads back
/// through <see cref="PacketCodec"/> unchanged, and is refused one byte short and one byte long. The two
/// frames whose bytes are written out in full also fail on an equal-width field swap, because the pinned hex
/// carries a different value in each word.
/// </summary>
public sealed class CoupleTablePins
{
    private const string Build = "kr-812";

    /// <summary>Every couple structure the baseline client defines keeps its length and round-trips.</summary>
    [Fact]
    public void TheCoupleBodiesMatchTheirLengthsAndRoundTrip()
    {
        // Packet_gcpReqCoupleInviteExpire, deserializer kr-812 sub_80C160: one flag at +4.
        Check("CoupleInviteExpire", 4, ("unknown_4", true));
        // Packet_gcpReqCoupleInviteAnswer, deserializer kr-812 sub_9FDE70: bytes at +4 and +5, strings at +8 and +36.
        Check("CoupleInviteAnswerRequest", 20, ("unknown_4", true), ("unknown_5", false), ("unknown_8", "A"), ("unknown_36", "B"));
        // Packet_gcpReqCoupleBreakUp, deserializer kr-812 sub_5C8FD0: one string at +4.
        Check("CoupleBreakUpRequest", 6, ("unknown_4", "A"));
        // Packet_gcpReqChangeCouplering, deserializer kr-812 sub_80A910: one word at +4.
        Check("CoupleChangeRingRequest", 4, ("unknown_4", 7));
        // Packet_gspResCoupleInviteAnswer and Packet_gspResCoupleBreakUp share kr-812 sub_83C1D0:
        // a byte at +4, strings at +8 and +36.
        Check("CoupleInviteAnswerReply", 16, ("unknown_4", true), ("unknown_8", "A"), ("unknown_36", "B"));
        Check("CoupleBreakUpReply", 16, ("unknown_4", false), ("unknown_8", "A"), ("unknown_36", "B"));
        // Packet_gspResCoupleNotifyPartner, deserializer kr-812 sub_83C170: a byte at +4, a word at +8, a string at +12.
        Check("CoupleNotifyPartner", 14, ("unknown_4", true), ("unknown_8", 9), ("unknown_12", "A"));
    }

    /// <summary>
    /// The ring-change reply is two words and nothing else (kr-812 sub_6DB1B0), so its bytes are pinned
    /// exactly: an equal-width swap of the two words cannot pass.
    /// </summary>
    [Fact]
    public void TheRingChangeReplyIsTwoDistinctWords()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "CoupleChangeRingReply", Values(("unknown_4", 1), ("unknown_8", 2)));
        Assert.Equal("0100000002000000", Convert.ToHexString(body));
        Assert.Equal(8, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "CoupleChangeRingReply", body);
        Assert.Equal(1, Convert.ToInt32(read["unknown_4"]));
        Assert.Equal(2, Convert.ToInt32(read["unknown_8"]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, "CoupleChangeRingReply", body[..^1]));
    }

    /// <summary>
    /// The invitation result is one word and one prefixed wide string (kr-812 sub_844570); the pinned hex
    /// separates the word from the string's character count, which is the equal-width pair a wrong table would
    /// swap.
    /// </summary>
    [Fact]
    public void TheInviteResultIsAWordThenAName()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "CoupleInviteResult", Values(("unknown_4", 3), ("unknown_8", "A")));
        Assert.Equal("03000000010000004100", Convert.ToHexString(body));
        Assert.Equal(10, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "CoupleInviteResult", body);
        Assert.Equal(3, Convert.ToInt32(read["unknown_4"]));
        Assert.Equal("A", read["unknown_8"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "CoupleInviteResult", read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, "CoupleInviteResult", body[..^1]));
    }

    /// <summary>Legacy descendants inherit baseline fields; modern builds receive no couple fields.</summary>
    [Fact]
    public void TheCoupleLayoutsFollowLegacyInheritanceAndExcludeModernBuilds()
    {
        foreach (var structure in new[]
        {
            "CoupleInviteExpire", "CoupleInviteAnswerRequest", "CoupleBreakUpRequest", "CoupleChangeRingRequest",
            "CoupleInviteResult", "CoupleInviteAnswerReply", "CoupleBreakUpReply", "CoupleNotifyPartner",
            "CoupleChangeRingReply",
        })
        {
            var baseline = TestBuilds.Packets.Resolve(Build, structure).Select(field => field.Name).ToArray();
            Assert.NotEmpty(baseline);
            Assert.Equal(baseline, TestBuilds.Packets.Resolve("cn-279", structure).Select(field => field.Name).ToArray());
            Assert.Equal(baseline, TestBuilds.Packets.Resolve("id-400", structure).Select(field => field.Name).ToArray());
            Assert.Empty(TestBuilds.Packets.Resolve("kr-1256", structure));
            Assert.Empty(TestBuilds.Packets.Resolve("cn-774", structure));
            Assert.Empty(TestBuilds.Packets.Resolve("tw-454", structure));
        }
    }

    private static Dictionary<string, object?> Values(params (string Name, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Name, field => field.Value);

    private static void Check(string structure, int length, params (string Name, object? Value)[] fields)
    {
        var values = Values(fields);
        var body = PacketCodec.Write(TestBuilds.Packets, Build, structure, values);
        Assert.Equal(length, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, structure, read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, structure, [.. body, 0]));
    }
}
