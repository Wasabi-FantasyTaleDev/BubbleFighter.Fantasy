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
        Check("CoupleInviteAnswerRequest", 20, ("unknown_4", true), ("accepted", false), ("inviter", "A"), ("recipient", "B"));
        // Packet_gcpReqCoupleBreakUp, deserializer kr-812 sub_5C8FD0: one string at +4.
        Check("CoupleBreakUpRequest", 6, ("partner", "A"));
        // Packet_gcpReqChangeCouplering, deserializer kr-812 sub_80A910: one word at +4.
        Check("CoupleChangeRingRequest", 4, ("ring_item", 7));
        // Packet_gspResCoupleInviteAnswer and Packet_gspResCoupleBreakUp share kr-812 sub_83C1D0:
        // a byte at +4, strings at +8 and +36.
        Check("CoupleInviteAnswerReply", 16, ("accepted", true), ("inviter", "A"), ("recipient", "B"));
        Check("CoupleBreakUpReply", 16, ("status", false), ("unknown_8", "A"), ("unknown_36", "B"));
        // Packet_gspResCoupleNotifyPartner, deserializer kr-812 sub_83C170: a byte at +4, a word at +8, a string at +12.
        Check("CoupleNotifyPartner", 14, ("partner_online", true), ("partner_user_number", 9), ("partner_name", "A"));
    }

    /// <summary>
    /// The ring-change reply is two words and nothing else (kr-812 sub_6DB1B0), so its bytes are pinned
    /// exactly: an equal-width swap of the two words cannot pass.
    /// </summary>
    [Fact]
    public void TheRingChangeReplyIsTwoDistinctWords()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "CoupleChangeRingReply", Values(("ring_result", 1), ("ring_item", 2)));
        Assert.Equal("0100000002000000", Convert.ToHexString(body));
        Assert.Equal(8, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "CoupleChangeRingReply", body);
        Assert.Equal(1, Convert.ToInt32(read["ring_result"]));
        Assert.Equal(2, Convert.ToInt32(read["ring_item"]));
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
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "CoupleInviteResult", Values(("result", 3), ("name", "A")));
        Assert.Equal("03000000010000004100", Convert.ToHexString(body));
        Assert.Equal(10, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "CoupleInviteResult", body);
        Assert.Equal(3, Convert.ToInt32(read["result"]));
        Assert.Equal("A", read["name"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "CoupleInviteResult", read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, "CoupleInviteResult", body[..^1]));
    }

    /// <summary>The audited legacy clients retain the bool32/name sequence, including the compact CN324/TW410 records.</summary>
    [Theory]
    [InlineData("kr-812", "recipient")]
    [InlineData("cn-324", "recipient")]
    [InlineData("tw-410", "recipient")]
    [InlineData("cn-404", "recipient")]
    [InlineData("cn-406", "recipient")]
    [InlineData("cn-408", "recipient")]
    [InlineData("cn-706", "recipient")]
    [InlineData("cn-705", "recipient")]
    [InlineData("cn-717", "recipient")]
    [InlineData("id-400", "recipient")]
    [InlineData("id-1809", "recipient")]
    public void LegacyFlatBodiesKeepTheirNativeSequence(string build, string secondName)
    {
        Pin(build, "CoupleInviteExpire", "01000000", ("unknown_4", true));
        Pin(build, "CoupleInviteAnswerRequest", "01000000000000000100000041000200000042004300",
            ("unknown_4", true), ("accepted", false), ("inviter", "A"), (secondName, "BC"));
        Pin(build, "CoupleBreakUpRequest", "010000004100", ("partner", "A"));
        Pin(build, "CoupleInviteResult", "04030201010000004100", ("result", 0x01020304), ("name", "A"));
        Pin(build, "CoupleInviteAnswerReply", "010000000100000041000200000042004300",
            ("accepted", true), ("inviter", "A"), (secondName, "BC"));
        Pin(build, "CoupleNotifyPartner", "0100000004030201010000004100",
            ("partner_online", true), ("partner_user_number", 0x01020304), ("partner_name", "A"));
    }

    /// <summary>The three modern clients use full flat replies with 24-byte in-memory string objects.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernRepliesKeepEveryNativeField(string build)
    {
        Pin(build, "CoupleInviteResult", "04030201010000004100", ("result", 0x01020304), ("name", "A"));
        Pin(build, "CoupleInviteAnswerReply", "010000000100000041000200000042004300",
            ("accepted", true), ("inviter", "A"), ("recipient", "BC"));
        Pin(build, "CoupleNotifyPartner", "0100000004030201010000004100",
            ("partner_online", true), ("partner_user_number", 0x01020304), ("partner_name", "A"));
        Pin(build, "CoupleChangeRingReply", "040302010807060501000000",
            ("ring_result", 0x01020304), ("ring_item", 0x05060708), ("unknown_8", true));
    }

    /// <summary>CN717 and modern breakup replies differ in type, order and number of names.</summary>
    [Fact]
    public void BreakupDoesNotInheritAnUnrelatedReplyShape()
    {
        foreach (var build in new[] { "kr-812", "id-400", "id-1809", "cn-404", "cn-406", "cn-408", "cn-705", "cn-706" })
            Pin(build, "CoupleBreakUpReply", "01000000010000004100010000004200",
                ("status", true), ("unknown_8", "A"), ("unknown_36", "B"));
        Pin("cn-324", "CoupleBreakUpReply", "01000000010000004100010000004200",
            ("status", true), ("unknown_8", "A"), ("unknown_12", "B"));
        Pin("tw-410", "CoupleBreakUpReply", "01000000010000004100010000004200",
            ("status", true), ("unknown_8", "A"), ("unknown_12", "B"));
        Pin("cn-717", "CoupleBreakUpReply", "01000000410001000000420004030201",
            ("unknown_4", "A"), ("unknown_32", "B"), ("status", 0x01020304));
        Pin("kr-1256", "CoupleBreakUpReply", "04030201010000004100", ("status", 0x01020304), ("name", "A"));
        foreach (var build in new[] { "cn-774", "tw-454" })
            Pin(build, "CoupleBreakUpReply", "01000000410001000000420004030201",
                ("actor", "A"), ("partner", "B"), ("status", 0x01020304));
    }

    /// <summary>CN706 restores the ring packets missing from its base; CN717 adds a flag in both directions.</summary>
    [Fact]
    public void LegacyRingPacketsKeepTheirActualWidths()
    {
        foreach (var build in new[] { "kr-812", "cn-705", "cn-706", "id-1809" })
        {
            Pin(build, "CoupleChangeRingRequest", "04030201", ("ring_item", 0x01020304));
            Pin(build, "CoupleChangeRingReply", "0403020108070605", ("ring_result", 0x01020304), ("ring_item", 0x05060708));
        }
        Pin("cn-717", "CoupleChangeRingRequest", "0403020101000000", ("ring_item", 0x01020304), ("unknown_8", true));
        Pin("cn-717", "CoupleChangeRingReply", "040302010807060501000000",
            ("ring_result", 0x01020304), ("ring_item", 0x05060708), ("unknown_12", true));
    }

    /// <summary>Confirmation retains the equipment suffix only where its native type is established.</summary>
    [Fact]
    public void ConfirmationsKeepTheirCompleteBody()
    {
        Pin("cn-324", "CoupleConfirmInviteRequest", "0100000001000000410004030201010000004200",
            ("confirmed", true), ("target", "A"), ("ring_item", 0x01020304), ("message", "B"));
        // tw-410 sub_4023E0 consumes the complete compact confirmation, without an equipment suffix.
        Pin("tw-410", "CoupleConfirmInviteRequest", "0100000001000000410004030201010000004200",
            ("confirmed", true), ("target", "A"), ("ring_item", 0x01020304), ("message", "B"));
        foreach (var build in new[] { "id-400", "cn-404", "cn-406", "cn-408", "cn-705", "cn-706", "cn-717" })
            Pin(build, "CoupleConfirmInviteRequest", "0100000001000000410004030201010000004200",
                ("confirmed", true), ("target", "A"), ("ring_item", 0x01020304), ("message", "B"));
        foreach (var build in new[] { Build, "id-1809" })
        Pin(build, "CoupleConfirmInviteRequest",
            "0100000001000000410004030201010000004200" +
            "4433221166550000000000000100000004030201000000000000000000000000",
            ("confirmed", true), ("target", "A"), ("ring_item", 0x01020304), ("message", "B"),
            ("equipped_items", Values(("user_number", 0x11223344), ("character_item", 0x5566),
                ("equipped_items", new[] { 0x01020304 }))));
    }

    /// <summary>CN774 and TW454 write their own complete equipment records after the proposal text.</summary>
    [Theory]
    [InlineData("cn-774", "443322116655000000000000FFFFFFFF010000000403020100000000000000000000000000000000")]
    [InlineData("tw-454", "443322116655000000000000FFFFFFFF0100000004030201000000000000000000000000")]
    public void ModernChineseProposalsAndInvitationsCarryTheirNativeEquipment(string build, string equipmentHex)
    {
        // cn-774 sub_4DCEF0 / tw-454 sub_C7C3D0 serialize the record that their own inventory copier supplies.
        var equipment = Values(("user_number", 0x11223344), ("character_id", 0x5566), ("equipped_items", new[] { 0x01020304 }));
        Pin(build, "CoupleConfirmInviteRequest", "0100000001000000410004030201010000004200" + equipmentHex,
            ("confirmed", true), ("target", "A"), ("ring_item", 0x01020304), ("message", "B"), ("equipped_items", equipment));
        var invitation = Values(("shown", true), ("inviter", "A"), ("recipient", "B"), ("message", "C"),
            ("equipped_items", equipment), ("fighter_info", Values(("nickname", "D"), ("level", 9))));
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CoupleInvite", invitation);
        var prefix = Convert.FromHexString("01000000010000004100010000004200010000004300" + equipmentHex);
        Assert.Equal(prefix, body[..prefix.Length]);
        var fields = TestBuilds.Packets.Resolve(build, "CoupleInvite");
        Assert.Equal(new[] { "EquippedObjectEntry", "FighterInfo" }, fields.Skip(4).Select(field => field.Type));
        var read = PacketCodec.Read(TestBuilds.Packets, build, "CoupleInvite", body);
        var fighter = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(read["fighter_info"]);
        Assert.Equal("D", fighter["nickname"]);
        Assert.Equal(9, Convert.ToInt32(fighter["level"]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "CoupleInvite", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "CoupleInvite", [.. body, 0]));
        Pin(build, "CoupleInviteExpire", "01000000", ("unknown_0", true));
        Pin(build, "CoupleInviteAnswerRequest", "0000000001000000010000004100010000004200",
            ("unknown_0", false), ("accepted", true), ("inviter", "A"), ("recipient", "B"));
        Pin(build, "CoupleBreakUpRequest", "010000004100", ("partner", "A"));
        Pin(build, "CoupleChangeRingRequest", "0403020100000000", ("ring_item", 0x01020304));
    }

    /// <summary>The ring dialog request is distinct from the unrelated Bingo selection request.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernRingUsesItsOwnDialogOpcodeAndCompleteBody(string build)
    {
        Assert.Equal((ushort)945, TestBuilds.Opcodes(build).C2S("gcpReqChangeCouplering"));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "CoupleChangeRingRequest", Convert.FromHexString("04030201")));
    }

    /// <summary>The full KR812 invitation includes equipment and FighterInfo after three names.</summary>
    [Fact]
    public void InvitationDoesNotStopBeforeItsNestedRecords()
    {
        var values = Values(("shown", true), ("inviter", "A"), ("recipient", "B"), ("message", "C"),
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

    /// <summary>Every newly enabled legacy invitation pins all equipment vectors and every byte of its own FighterInfo.</summary>
    [Theory]
    [InlineData("cn-717", "extended", 42, 406)] // cn-717 sub_8C4E10 -> sub_569E70 / sub_40D620.
    [InlineData("cn-706", "flat", 42, 366)] // cn-706 sub_7A09A0 -> sub_4B8470 / sub_79F020.
    [InlineData("cn-406", "compact", 37, 284)] // cn-406 sub_714AB0 -> sub_41D060 / sub_713260.
    [InlineData("cn-404", "flat", 42, 366)] // cn-404 sub_7C5E60 -> sub_4AD020 / sub_7C44A0.
    [InlineData("cn-705", "flat", 42, 366)] // cn-705 sub_79F460 -> sub_4B8340 / sub_79D930.
    [InlineData("id-1809", "trailing", 42, 370)] // id-1809 sub_7BE0D0 -> sub_4D4960 / sub_7BCA50.
    [InlineData("cn-408", "flat", 42, 366)] // cn-408 sub_7C9480 -> sub_4ACA40 / sub_7C7A10.
    [InlineData("id-400", "flat", 42, 366)] // id-400 sub_798600 -> sub_47EA90 / sub_796CC0.
    public void LegacyInvitationsPinEveryNestedSection(string build, string tail, int counterCount, int length)
    {
        var fighter = Values(("nickname", "D"), ("level", 9), ("couple_present", 1),
            ("couple_since", 0x0102030405060708L), ("couple_ring_item", 0x11223344),
            ("couple_partner_online", true), ("unknown_80", 0x10203040),
            ("primary_wins", 3), ("primary_losses", 4), ("primary_draws", 5),
            ("primary_knockdowns", 6), ("primary_kills", 7), ("primary_rescues", 8),
            ("unknown_108", Enumerable.Range(1, counterCount).ToArray()));
        const string header = "01000000010000004100010000004200010000004300";
        const string equipment = "4433221166550000000000000100000004030201000000000000000000000000";
        const string fighterPrefix = "000000000100000044000000000009000000000000000000000000000000" +
            "010000000807060504030201443322110100000040302010" +
            "030000000400000005000000060000000700000008000000";
        string Words(IEnumerable<int> words) => string.Concat(words.Select(word =>
        {
            var bytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, word);
            return Convert.ToHexString(bytes);
        }));
        var expectedFighter = fighterPrefix + Words(Enumerable.Range(1, counterCount));
        if (tail == "compact")
        {
            expectedFighter += "E8030000";
        }
        else
        {
            fighter[tail == "extended" ? "unknown_284" : "unknown_288"] = new[]
            {
                Values(("unknown_4", 0x21314151), ("unknown_8", 0x61718191)),
            };
            expectedFighter += "010000005141312191817161";
            if (tail == "extended")
            {
                fighter["unknown_300"] = 0x1122;
                fighter["unknown_304"] = 0x3344;
                fighter["unknown_312"] = 0x5566;
                fighter["unknown_320"] = Enumerable.Range(101, 8).ToArray();
                fighter["unknown_360"] = Enumerable.Range(201, 8).ToArray();
                fighter["unknown_392"] = "Z";
                fighter["unknown_356"] = 0x7788;
                expectedFighter += "E803000001000000221100004433000066550000" +
                    Words(Enumerable.Range(101, 8)) + Words(Enumerable.Range(201, 8)) + "010000005A0088770000";
            }
            else
            {
                expectedFighter += "E80300000000000001000000";
                if (tail == "flat")
                {
                    fighter["unknown_304"] = true;
                    fighter["unknown_308"] = Enumerable.Range(101, 8).ToArray();
                    fighter["unknown_340"] = "Z";
                    expectedFighter += "01000000" + Words(Enumerable.Range(101, 8)) + "010000005A00";
                }
                else
                {
                    fighter["unknown_304"] = 0x3344;
                    fighter["unknown_312"] = Enumerable.Range(101, 8).ToArray();
                    fighter["unknown_344"] = "Z";
                    fighter["unknown_308"] = 0x7788;
                    expectedFighter += "44330000" + Words(Enumerable.Range(101, 8)) + "010000005A0088770000";
                }
            }
        }
        var body = CheckBody(build, "CoupleInvite", length, Values(
            ("shown", true), ("inviter", "A"), ("recipient", "B"), ("message", "C"),
            ("equipped_items", Values(("user_number", 0x11223344), ("character_item", 0x5566),
                ("equipped_items", new[] { 0x01020304 }))), ("fighter_info", fighter)));
        Assert.Equal(header + equipment + expectedFighter, Convert.ToHexString(body));
    }

    /// <summary>Each compact client retains its full byte-vector equipment and 36-word fighter tail after the three names.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("tw-410")]
    public void CompactInvitationPinsItsOwnCompleteNestedRecords(string build)
    {
        // cn-324 sub_6FD330/sub_6FD390, with nested readers cn-324 sub_45B640 and sub_6FBD60.
        // tw-410 sub_6FCFB0/sub_6FD010, with nested readers tw-410 sub_45AFC0 and sub_6FB910.
        var fighter = Values(("user_number", 42), ("nickname", "D"), ("character_type", 3), ("level", 9),
            ("couple_since", 0x0102030405060708L), ("couple_ring_item", 0x11223344),
            ("couple_partner_online", true), ("unknown_48", 0x10203040),
            ("primary_wins", 3), ("primary_losses", 4), ("primary_draws", 5),
            ("primary_knockdowns", 6), ("primary_kills", 7), ("primary_rescues", 8),
            ("unknown_76", Enumerable.Range(1, 36).ToArray()));
        var body = CheckBody(build, "CoupleInvite", 266, Values(
            ("shown", true), ("inviter", "A"), ("recipient", "B"), ("message", "C"),
            ("equipped_items", Values(("user_number", 0x11223344), ("character_item", 0x5566),
                ("unknown_12", 0x77889900), ("equipped_items", new[] { 0x01020304 }),
                ("unknown_28", new byte[] { 0xAB, 0xCD }))), ("fighter_info", fighter)));
        var tail = string.Concat(Enumerable.Range(1, 36).Select(word =>
        {
            var bytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, word);
            return Convert.ToHexString(bytes);
        }));
        Assert.Equal("01000000010000004100010000004200010000004300" +
            "443322116655000000998877010000000403020102000000ABCD" +
            "2A0000000100000044000300000009000000000000000000000000000000" +
            "0807060504030201443322110100000040302010" +
            "030000000400000005000000060000000700000008000000" + tail, Convert.ToHexString(body));
    }

    /// <summary>KR1256's invitation answer is independently a C2S frame, and its own-info reply preserves tag semantics.</summary>
    [Fact]
    public void ModernAnswerAndOwnInfoAreCompleteBodies()
    {
        Assert.Equal((ushort)932, TestBuilds.Opcodes("kr-1256").C2S("gcpReqCoupleInviteAnswer"));
        Assert.Equal((ushort)932, TestBuilds.Opcodes("kr-1256").S2C("gspTitleSyncInfo"));
        Pin("kr-1256", "CoupleInviteAnswerRequest", "0100000000000000010000004100010000004200",
            ("unknown_0", true), ("accepted", false), ("inviter", "A"), ("recipient", "B"));
        // kr-1256 sub_E880A0, sub_E849D0, sub_E86B50 and the actual ring dialog sender sub_EA0430.
        Pin("kr-1256", "CoupleConfirmInviteRequest", "01000000" + "010000004100" + "04030201" + "020000004200" + "4300",
            ("confirmed", true), ("target", "A"), ("ring_item", 0x01020304), ("message", "BC"));
        Pin("kr-1256", "CoupleInviteExpire", "01000000", ("unknown_0", true));
        Pin("kr-1256", "CoupleBreakUpRequest", "010000004100", ("partner", "A"));
        Pin("kr-1256", "CoupleChangeRingRequest", "0403020100000000", ("ring_item", 0x01020304));
        Pin("kr-1256", "CoupleMyInfo", "00", ("unknown_0", 0));
        Pin("kr-1256", "CoupleMyInfo", "7F", ("unknown_0", 0x7F));
        Pin("kr-1256", "CoupleMyInfo", "3A0807060504030201443322118877665501000000",
            ("unknown_0", Values(("couple_since", 0x0102030405060708L), ("couple_ring_item", 0x11223344),
                ("unknown_60", 0x55667788), ("couple_partner_online", true))));
        Pin("cn-324", "CoupleInitialInviteRequest", "01000000010000004100", ("selected", true), ("target", "A"));
        Pin("cn-324", "CoupleInviterStatusReply", "04030201", ("status", 0x01020304));
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

    /// <summary>
    /// The kr-1256 invitation is what kr-1256 sub_10E17C0 reads: flag, three names, the equipment block of the
    /// room profile record without its user number, then the fighter record.
    /// </summary>
    [Fact]
    public void Kr1256InvitationIsTheReadersBody()
    {
        var fields = TestBuilds.Packets.Resolve("kr-1256", "CoupleInvite");
        Assert.Equal(new[] { "shown", "inviter", "recipient", "message", "equipped_items", "fighter_info" },
            fields.Select(field => field.Name));
        Assert.Equal(new[] { "CoupleInviteEquipment", "FighterInfo" }, fields.Skip(4).Select(field => field.Type));
        var equipment = PacketCodec.Write(TestBuilds.Packets, "kr-1256", "CoupleInviteEquipment",
            Values(("character_id", 300001), ("equipped_items", new[] { 300001, 7 })));
        Assert.Equal("E1930400" + "02000000E193040007000000" + "00000000" + "00000000" + "00000000" + "00000000",
            Convert.ToHexString(equipment));
    }

    /// <summary>Unsupported or incomplete layouts cannot inherit a plausible baseline body.</summary>
    [Fact]
    public void UnverifiedBodiesAreExcluded()
    {
        foreach (var structure in TestBuilds.Packets.Structs.Where(name => name.StartsWith("Couple", StringComparison.Ordinal)))
            Assert.Empty(TestBuilds.Packets.Resolve("cn-279", structure));
        foreach (var build in new[] { "cn-324", "tw-410", "id-400", "cn-404", "cn-406", "cn-408" })
        {
            Assert.Empty(TestBuilds.Packets.Resolve(build, "CoupleChangeRingRequest"));
            Assert.Empty(TestBuilds.Packets.Resolve(build, "CoupleChangeRingReply"));
        }
        // CN/TW do not publish the KR1256 dedicated own-couple update packet.
        Assert.Empty(TestBuilds.Packets.Resolve("cn-774", "CoupleMyInfo"));
        Assert.Empty(TestBuilds.Packets.Resolve("tw-454", "CoupleMyInfo"));
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
