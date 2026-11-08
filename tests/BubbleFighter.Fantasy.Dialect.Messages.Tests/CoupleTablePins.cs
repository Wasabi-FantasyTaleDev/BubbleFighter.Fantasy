using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins audited couple bodies and build exclusions. Distinct words, names and flags detect reordered fields;
/// complete-body reads reject truncation and suffixes without claiming a native gameplay binding.
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

    /// <summary>All nine audited legacy clients retain the bool32/name sequence, including compact CN324 strings.</summary>
    [Theory]
    [InlineData("kr-812", "unknown_36")]
    [InlineData("cn-324", "unknown_12")]
    [InlineData("cn-404", "unknown_36")]
    [InlineData("cn-406", "unknown_36")]
    [InlineData("cn-408", "unknown_36")]
    [InlineData("cn-706", "unknown_36")]
    [InlineData("cn-717", "unknown_36")]
    [InlineData("id-400", "unknown_36")]
    [InlineData("id-1809", "unknown_36")]
    public void LegacyFlatBodiesKeepTheirNativeSequence(string build, string secondName)
    {
        Pin(build, "CoupleInviteExpire", "01000000", ("unknown_4", true));
        Pin(build, "CoupleInviteAnswerRequest", "01000000000000000100000041000200000042004300",
            ("unknown_4", true), ("unknown_5", false), ("unknown_8", "A"), (secondName, "BC"));
        Pin(build, "CoupleBreakUpRequest", "010000004100", ("unknown_4", "A"));
        Pin(build, "CoupleInviteResult", "04030201010000004100", ("unknown_4", 0x01020304), ("unknown_8", "A"));
        Pin(build, "CoupleInviteAnswerReply", "010000000100000041000200000042004300",
            ("unknown_4", true), ("unknown_8", "A"), (secondName, "BC"));
        Pin(build, "CoupleNotifyPartner", "0100000004030201010000004100",
            ("unknown_4", true), ("unknown_8", 0x01020304), ("unknown_12", "A"));
    }

    /// <summary>The three modern clients use full flat replies with 24-byte in-memory string objects.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernRepliesKeepEveryNativeField(string build)
    {
        Pin(build, "CoupleInviteResult", "04030201010000004100", ("unknown_0", 0x01020304), ("unknown_4", "A"));
        Pin(build, "CoupleInviteAnswerReply", "010000000100000041000200000042004300",
            ("unknown_0", true), ("unknown_4", "A"), ("unknown_28", "BC"));
        Pin(build, "CoupleNotifyPartner", "0100000004030201010000004100",
            ("unknown_0", true), ("unknown_4", 0x01020304), ("unknown_8", "A"));
        Pin(build, "CoupleChangeRingReply", "040302010807060501000000",
            ("unknown_0", 0x01020304), ("unknown_4", 0x05060708), ("unknown_8", true));
    }

    /// <summary>CN717 and modern breakup replies differ in type, order and number of names.</summary>
    [Fact]
    public void BreakupDoesNotInheritAnUnrelatedReplyShape()
    {
        foreach (var build in new[] { "kr-812", "id-400", "id-1809", "cn-404", "cn-406", "cn-408", "cn-706" })
            Pin(build, "CoupleBreakUpReply", "01000000010000004100010000004200",
                ("unknown_4", true), ("unknown_8", "A"), ("unknown_36", "B"));
        Pin("cn-324", "CoupleBreakUpReply", "01000000010000004100010000004200",
            ("unknown_4", true), ("unknown_8", "A"), ("unknown_12", "B"));
        Pin("cn-717", "CoupleBreakUpReply", "01000000410001000000420004030201",
            ("unknown_4", "A"), ("unknown_32", "B"), ("unknown_60", 0x01020304));
        Pin("kr-1256", "CoupleBreakUpReply", "04030201010000004100", ("unknown_0", 0x01020304), ("unknown_4", "A"));
        foreach (var build in new[] { "cn-774", "tw-454" })
            Pin(build, "CoupleBreakUpReply", "01000000410001000000420004030201",
                ("unknown_0", "A"), ("unknown_24", "B"), ("unknown_48", 0x01020304));
    }

    /// <summary>CN706 restores the ring packets missing from its base; CN717 adds a flag in both directions.</summary>
    [Fact]
    public void LegacyRingPacketsKeepTheirActualWidths()
    {
        foreach (var build in new[] { "kr-812", "cn-706", "id-1809" })
        {
            Pin(build, "CoupleChangeRingRequest", "04030201", ("unknown_4", 0x01020304));
            Pin(build, "CoupleChangeRingReply", "0403020108070605", ("unknown_4", 0x01020304), ("unknown_8", 0x05060708));
        }
        Pin("cn-717", "CoupleChangeRingRequest", "0403020101000000", ("unknown_4", 0x01020304), ("unknown_8", true));
        Pin("cn-717", "CoupleChangeRingReply", "040302010807060501000000",
            ("unknown_4", 0x01020304), ("unknown_8", 0x05060708), ("unknown_12", true));
    }

    /// <summary>Confirmation retains the equipment suffix only where its native type is established.</summary>
    [Fact]
    public void ConfirmationsKeepTheirCompleteBody()
    {
        Pin("cn-324", "CoupleConfirmInviteRequest", "0100000001000000410004030201010000004200",
            ("unknown_4", true), ("unknown_8", "A"), ("unknown_12", 0x01020304), ("unknown_16", "B"));
        foreach (var build in new[] { "id-400", "cn-404", "cn-406", "cn-408", "cn-706", "cn-717" })
            Pin(build, "CoupleConfirmInviteRequest", "0100000001000000410004030201010000004200",
                ("unknown_4", true), ("unknown_8", "A"), ("unknown_36", 0x01020304), ("unknown_40", "B"));
        Pin(Build, "CoupleConfirmInviteRequest",
            "0100000001000000410004030201010000004200" +
            "4433221166550000000000000100000004030201000000000000000000000000",
            ("unknown_4", true), ("unknown_8", "A"), ("unknown_36", 0x01020304), ("unknown_40", "B"),
            ("equipped_items", Values(("user_number", 0x11223344), ("character_item", 0x5566),
                ("equipped_items", new[] { 0x01020304 }))));
    }

    /// <summary>The full KR812 invitation includes equipment and FighterInfo after three names.</summary>
    [Fact]
    public void InvitationDoesNotStopBeforeItsNestedRecords()
    {
        var values = Values(("unknown_4", true), ("unknown_8", "A"), ("unknown_36", "B"), ("unknown_64", "C"),
            ("equipped_items", Values(("user_number", 0x11223344), ("character_item", 0x5566),
                ("equipped_items", new[] { 0x01020304 }))),
            ("fighter_info", Values(("nickname", "D"), ("level", 9))));
        var body = CheckBody(Build, "CoupleInvite", 396, values);
        Assert.Equal("01000000010000004100010000004200010000004300", Convert.ToHexString(body[..22]));
        Assert.Equal("4433221166550000000000000100000004030201000000000000000000000000", Convert.ToHexString(body[22..54]));
        Assert.Equal("000000000100000044000000000009000000", Convert.ToHexString(body[54..72]));
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "CoupleInvite", body);
        var fighter = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(read["fighter_info"]);
        Assert.Equal("D", fighter["nickname"]);
        Assert.Equal(9, Convert.ToInt32(fighter["level"]));
    }

    /// <summary>KR1256's invitation answer is independently a C2S frame, and its own-info reply preserves tag semantics.</summary>
    [Fact]
    public void ModernAnswerAndOwnInfoAreCompleteBodies()
    {
        Assert.Equal((ushort)932, TestBuilds.Opcodes("kr-1256").C2S("gcpReqCoupleInviteAnswer"));
        Assert.Equal((ushort)932, TestBuilds.Opcodes("kr-1256").S2C("gspTitleSyncInfo"));
        Pin("kr-1256", "CoupleInviteAnswerRequest", "0100000000000000010000004100010000004200",
            ("unknown_0", true), ("unknown_1", false), ("unknown_4", "A"), ("unknown_28", "B"));
        Pin("kr-1256", "CoupleMyInfo", "00", ("unknown_0", 0));
        Pin("kr-1256", "CoupleMyInfo", "7F", ("unknown_0", 0x7F));
        Pin("kr-1256", "CoupleMyInfo", "3A0807060504030201443322118877665501000000",
            ("unknown_0", Values(("unknown_48", 0x0102030405060708L), ("unknown_56", 0x11223344),
                ("unknown_60", 0x55667788), ("unknown_64", true))));
        Pin("cn-324", "CoupleInitialInviteRequest", "01000000010000004100", ("unknown_4", true), ("unknown_8", "A"));
        Pin("cn-324", "CoupleInviterStatusReply", "04030201", ("unknown_4", 0x01020304));
    }

    /// <summary>Every modern couple-list envelope includes its final record even when both top-level lists are empty.</summary>
    [Theory]
    [InlineData("kr-1256", 947)]
    [InlineData("cn-774", 946)]
    [InlineData("tw-454", 946)]
    public void ModernListsRequireTheirSuffixAndPreserveNestedMembers(string build, int opcode)
    {
        Assert.Equal((ushort)opcode, TestBuilds.Opcodes(build).S2C("gspUpdateCoupleList"));
        var empty = CheckBody(build, "CoupleList", 44, Values());
        Assert.All(empty, value => Assert.Equal((byte)0, value));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "CoupleList", new byte[8]));
        var record = Values(("unknown_0", 1), ("unknown_4", 2), ("unknown_8", 3), ("unknown_12", true),
            ("unknown_13", false), ("unknown_16", 4), ("unknown_24", 0x0102030405060708L),
            ("unknown_32", new[] { Values(("unknown_0", 5), ("unknown_4", "A"), ("unknown_28", true)) }));
        const string recordHex = "0100000002000000030000000100000000000000040000000807060504030201010000000500000001000000410001000000";
        Pin(build, "CoupleList", "01000000" + recordHex + "020000000600000007000000" + recordHex,
            ("unknown_0", new[] { record }), ("unknown_12", new[] { 6, 7 }), ("unknown_24", record));
    }

    /// <summary>Unsupported or incomplete layouts cannot inherit a plausible baseline body.</summary>
    [Fact]
    public void UnverifiedBodiesAreExcluded()
    {
        foreach (var structure in TestBuilds.Packets.Structs.Where(name => name.StartsWith("Couple", StringComparison.Ordinal)))
            Assert.Empty(TestBuilds.Packets.Resolve("cn-279", structure));
        foreach (var build in new[] { "cn-324", "id-400", "cn-404", "cn-406", "cn-408" })
        {
            Assert.Empty(TestBuilds.Packets.Resolve(build, "CoupleChangeRingRequest"));
            Assert.Empty(TestBuilds.Packets.Resolve(build, "CoupleChangeRingReply"));
        }
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
            foreach (var structure in new[] { "CoupleInviteExpire", "CoupleBreakUpRequest", "CoupleChangeRingRequest", "CoupleConfirmInviteRequest", "CoupleInvite" })
                Assert.Empty(TestBuilds.Packets.Resolve(build, structure));
        foreach (var structure in new[] { "CoupleInviteAnswerRequest", "CoupleMyInfo" })
        {
            Assert.Empty(TestBuilds.Packets.Resolve("cn-774", structure));
            Assert.Empty(TestBuilds.Packets.Resolve("tw-454", structure));
        }
        Assert.Empty(TestBuilds.Packets.Resolve("id-1809", "CoupleConfirmInviteRequest"));
    }

    private static Dictionary<string, object?> Values(params (string Name, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Name, field => field.Value);

    private static void Check(string structure, int length, params (string Name, object? Value)[] fields)
        => CheckBody(Build, structure, length, Values(fields));

    private static void Pin(string build, string structure, string hex, params (string Name, object? Value)[] fields)
    {
        var expected = Convert.FromHexString(hex);
        Assert.Equal(expected, CheckBody(build, structure, expected.Length, Values(fields)));
    }

    private static byte[] CheckBody(string build, string structure, int length, Dictionary<string, object?> values)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, structure, values);
        Assert.Equal(length, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
        return body;
    }
}
