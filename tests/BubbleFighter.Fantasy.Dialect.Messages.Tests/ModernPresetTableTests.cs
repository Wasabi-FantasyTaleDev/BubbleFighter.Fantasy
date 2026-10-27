using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins modern preset records independently of their larger native object strides.</summary>
public sealed class ModernPresetTableTests
{
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RequestRetainsItsOpaqueContextAndReplyRetainsItsStatus(string build)
    {
        var request = PacketCodec.ReadScalars(TestBuilds.Packets, build, "WeaponPresetChangeRequest",
            Convert.FromHexString("FFFFFFFF04"));
        Assert.Equal(-1, Assert.IsType<int>(request["context"]));
        Assert.Equal((byte)4, Assert.IsType<byte>(request["slot"]));
        foreach (var (hex, status) in new[] { ("0200000000", 0), ("0201000000", 1), ("02FFFFFFFF", -1) })
        {
            var body = Convert.FromHexString(hex);
            var reply = PacketCodec.Read(TestBuilds.Packets, build, "WeaponPresetChangeReply", body);
            Assert.Equal((byte)2, Assert.IsType<byte>(reply["active_slot"]));
            Assert.Equal(status, Assert.IsType<int>(reply["status"]));
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "WeaponPresetChangeReply", reply));
        }
        foreach (var length in new[] { 0, 1, 4, 6 })
        {
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(
                TestBuilds.Packets, build, "WeaponPresetChangeRequest", new byte[length]));
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(
                TestBuilds.Packets, build, "WeaponPresetChangeReply", new byte[length]));
        }
    }

    public static TheoryData<string, string, string, int> Fixtures => new()
    {
        { "kr-1256", "4433221188776655CCBBAA99", "102030405060708090A0B0C0", 40 },
        { "cn-774", "AABB4433221188776655CCBBAA99", "1122102030405060708090A0B0C0", 44 },
        { "tw-454", "AABB4433221188776655", "11221020304050607080", 36 },
    };

    [Theory, MemberData(nameof(Fixtures))]
    public void UpdateUsesTheBuildsOwnEntryWidthsAndCountedTail(
        string build, string firstHex, string secondHex, int expectedBytes)
    {
        // Independent envelope: slot 2, identity 0x01020304, two distinct entries,
        // and three nonzero opaque tail bytes. No object padding is sent.
        var body = Convert.FromHexString("020403020102000000" + firstHex + secondHex + "03000000A1B2C3");
        Assert.Equal(expectedBytes, body.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "WeaponPresetUpdate", body);
        Assert.Equal((byte)2, Assert.IsType<byte>(decoded["active_slot"]));
        var list = Assert.IsType<Dictionary<string, object?>>(decoded["presets"]);
        Assert.Equal(0x01020304, Assert.IsType<int>(list["user_number"]));
        var entries = Assert.IsType<object?[]>(list["entries"]);
        Assert.Equal(2, entries.Length);
        var first = Assert.IsType<Dictionary<string, object?>>(entries[0]);
        var second = Assert.IsType<Dictionary<string, object?>>(entries[1]);
        Assert.Equal(Convert.FromHexString(firstHex), PacketCodec.Write(
            TestBuilds.Packets, build, "WeaponPresetInfo", first));
        Assert.Equal(Convert.FromHexString(secondHex), PacketCodec.Write(
            TestBuilds.Packets, build, "WeaponPresetInfo", second));
        var tailName = build == "kr-1256" ? "unknown_16" : "unknown_20";
        Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3 },
            Assert.IsType<object?[]>(list[tailName]).Select(value => Assert.IsType<byte>(value)).ToArray());
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "WeaponPresetUpdate", decoded));
    }

    [Theory]
    [InlineData("kr-1256", "unknown_0:u32,unknown_4:u32,unknown_8:u32")]
    [InlineData("cn-774", "unknown_4:u8,unknown_5:u8,unknown_8:u32,unknown_12:u32,unknown_16:u32")]
    [InlineData("tw-454", "unknown_4:u8,unknown_5:u8,unknown_8:u32,unknown_12:u32")]
    public void EntryFieldsRetainUnknownMeaningsAndProvenWidths(string build, string expected) =>
        Assert.Equal(expected, string.Join(",", TestBuilds.Packets.Resolve(build, "WeaponPresetInfo")
            .Select(field => field.Name + ":" + field.Type)));

    [Theory, MemberData(nameof(Fixtures))]
    public void UpdateRejectsTruncationTrailingBytesAndUnboundedCounts(
        string build, string firstHex, string secondHex, int expectedBytes)
    {
        var body = Convert.FromHexString("020403020102000000" + firstHex + secondHex + "03000000A1B2C3");
        Assert.Equal(expectedBytes, body.Length);
        for (var length = 0; length < body.Length; length++)
        {
            var prefix = body[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(
                TestBuilds.Packets, build, "WeaponPresetUpdate", prefix));
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(
            TestBuilds.Packets, build, "WeaponPresetUpdate", [.. body, 0]));
        var oversizedEntries = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedEntries.AsSpan(5), uint.MaxValue);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(
            TestBuilds.Packets, build, "WeaponPresetUpdate", oversizedEntries));
        var oversizedTail = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedTail.AsSpan(body.Length - 7), uint.MaxValue);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(
            TestBuilds.Packets, build, "WeaponPresetUpdate", oversizedTail));
    }

    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void EmptyModernUpdateIsThirteenBytesAndRoundTrips(string build)
    {
        var body = Convert.FromHexString("00040302010000000000000000");
        Assert.Equal(13, body.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "WeaponPresetUpdate", body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "WeaponPresetUpdate", decoded));
    }

    /// <summary>Pins the fourth positional record without changing its opaque three-word contents.</summary>
    [Fact]
    public void FourPositionalRecordsCarryTheExpansionSlot()
    {
        // kr-1256 sub_81B820 keys each preset record by its position in the list and
        // kr-1256 sub_5FB4F0 enables index 3 only when a record exists for it, so the slot
        // expansion is one more record: active slot 1, identity 7, four 12-byte records,
        // empty byte tail. The refusal reply is pinned by the status cases above.
        var body = Convert.FromHexString(
            "01" + "07000000" + "04000000"
            + "010000000200000003000000"
            + "040000000500000006000000"
            + "070000000800000009000000"
            + "0A0000000B0000000C000000"
            + "00000000");
        Assert.Equal(61, body.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "WeaponPresetUpdate", body);
        var list = Assert.IsType<Dictionary<string, object?>>(decoded["presets"]);
        var entries = Assert.IsType<object?[]>(list["entries"]);
        Assert.Equal(4, entries.Length);
        Assert.Equal(10, Assert.IsType<Dictionary<string, object?>>(entries[3])["unknown_0"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, "kr-1256", "WeaponPresetUpdate", decoded));
    }

    [Theory]
    [InlineData("kr-1256", true)] // kr-1256 sub_9C3880 reads the tabled list.
    [InlineData("cn-774", false)] // its entry shape is not proven
    [InlineData("tw-454", false)]
    public void OnlyTheBuildsWhoseListIsReadHaveTheCapability(string build, bool enabled)
    {
        Assert.Equal(enabled, TestBuilds.Packets.CapabilityOf(build, "weapon_presets"));
    }
}
