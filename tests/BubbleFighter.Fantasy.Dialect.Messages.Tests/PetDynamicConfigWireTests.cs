using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete pet configuration bodies, native category framing and cache invalidation.</summary>
public sealed class PetDynamicConfigWireTests
{
    /// <summary>CN's category contains its own form, four choices, an empty preview map and both explicit settings.</summary>
    [Fact]
    public void CnCategoryHasTheNativeU16EnvelopeAndSeventySixByteBody()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "cn-774");
        var settings = new PetClientConfiguration(317, 12600);
        Assert.True(wire.SupportsPetConfiguration);
        Assert.False(wire.HasCreationContent);
        Assert.Empty(wire.ServedBits);
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-774", "character_creation"));
        wire.Validate(settings);

        var snapshot = wire.CreationSnapshot(settings);
        Assert.Equal(Convert.FromHexString("0040"), snapshot[..2]);
        var compressed = PetDynamicConfigTestData.Compressed(snapshot, 2);
        Assert.Equal(8, compressed[0] & 0x0f);
        Assert.True((compressed[0] >> 4) <= 7);
        Assert.Equal(0, ((compressed[0] << 8) | compressed[1]) % 31);
        Assert.Equal(0, compressed[1] & 0x20);
        var plain = PetDynamicConfigTestData.Inflate(snapshot, 2);
        Assert.Equal(76, plain.Length);
        Assert.Equal(Convert.FromHexString("14000000"), plain[..4]);
        Assert.Equal("Ui/DlgCreateUser3.ui", Encoding.Unicode.GetString(plain, 4, 40));
        // cn-774 sub_5F67F0 consumes one map; cn-774 sub_E5B7F0 consumes the first trailing word as f32 days.
        Assert.Equal(Convert.FromHexString(
            "040000000A00000013000000190000001B000000" +
            "000000005555153E3D010000"), plain[44..]);
        var fields = PacketCodec.Read(TestBuilds.Packets, "cn-774", "CreationContent", plain);
        Assert.Equal(new object?[] { 10, 19, 25, 27 }, Assert.IsType<object?[]>(fields["character_types"]));
        Assert.Empty(Assert.IsType<object?[]>(fields["preview_overrides"]));
        Assert.Equal(DynamicConfigWire.Checksum(compressed), wire.StampOf(14, settings));
        Assert.Equal(new byte[2], wire.CreationSnapshot());
        Assert.Throws<InvalidOperationException>(() => wire.StampOf(14));
    }

    /// <summary>Changing either setting invalidates the previous compressed stamp while keeping the whole category coherent.</summary>
    [Theory]
    [InlineData("kr-1256", 4, 409, 12600, 0x3E155555)]
    [InlineData("kr-1256", 4, 317, 25200, 0x3E955555)]
    [InlineData("cn-774", 2, 409, 12600, 0x3E155555)]
    [InlineData("cn-774", 2, 317, 25200, 0x3E955555)]
    public void ChangingEitherSettingRefreshesTheCompleteCategory(string build, int maskBytes,
        int maximum, int cooldown, int expectedCooldownBits)
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, build);
        var before = new PetClientConfiguration(317, 12600);
        var after = new PetClientConfiguration(maximum, cooldown);
        var original = wire.CreationSnapshot(before);
        var changed = wire.CreationSnapshot(after);
        var originalStamp = wire.StampOf(14, before);
        var changedStamp = wire.StampOf(14, after);
        Assert.NotEqual(originalStamp, changedStamp);
        Assert.NotEqual(PetDynamicConfigTestData.Compressed(original, maskBytes),
            PetDynamicConfigTestData.Compressed(changed, maskBytes));
        var originalPlain = PetDynamicConfigTestData.Inflate(original, maskBytes);
        var changedPlain = PetDynamicConfigTestData.Inflate(changed, maskBytes);
        Assert.Equal(originalPlain[..^8], changedPlain[..^8]);
        Assert.Equal(expectedCooldownBits, BinaryPrimitives.ReadInt32LittleEndian(changedPlain.AsSpan(changedPlain.Length - 8)));
        Assert.Equal(maximum, BinaryPrimitives.ReadInt32LittleEndian(changedPlain.AsSpan(changedPlain.Length - 4)));
        Assert.Equal(DynamicConfigWire.Checksum(PetDynamicConfigTestData.Compressed(changed, maskBytes)), changedStamp);
        Assert.Equal(changed, wire.Reply(PetDynamicConfigTestData.Request(maskBytes, 1u << 14, originalStamp), after));
        Assert.Null(wire.Reply(PetDynamicConfigTestData.Request(maskBytes, 1u << 14, changedStamp), after));
        Assert.Equal(original, wire.Reply(PetDynamicConfigTestData.Request(maskBytes, 1u << 14, changedStamp), before));
    }

    /// <summary>Publishing pet settings leaves all other Korean categories and the original generation unchanged.</summary>
    [Fact]
    public void PetSettingsPreserveEveryOtherKoreanCategory()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "kr-1256");
        var settings = new PetClientConfiguration(317, 12600);
        var original = wire.CreationSnapshot();
        var originalStamp = wire.StampOf(14);
        var bits = wire.ServedBits.OrderBy(bit => bit).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 8, 9, 12, 13, 14, 16 }, bits);
        foreach (var bit in bits.Where(bit => bit != 14))
        {
            var stamp = wire.StampOf(bit);
            var request = PetDynamicConfigTestData.Request(4, 1u << bit, stamp ^ 1);
            Assert.Equal(wire.Reply(request), wire.Reply(request, settings));
            Assert.Equal(stamp, wire.StampOf(bit, settings));
        }
        var cached = PetDynamicConfigTestData.Request(4, bits.Aggregate(0u, (mask, bit) => mask | 1u << bit),
            bits.Select(bit => wire.StampOf(bit)).ToArray());
        Assert.Equal(wire.CreationSnapshot(settings), wire.Reply(cached, settings));
        Assert.Null(wire.Reply(cached));
        Assert.Equal(original, wire.CreationSnapshot());
        Assert.Equal(originalStamp, wire.StampOf(14));
    }

    /// <summary>CN consumes sixteen ascending checksum words but can publish only its admitted category fourteen.</summary>
    [Fact]
    public void CnFullMaskConsumesEveryChecksumWithoutAdmittingOtherCategories()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "cn-774");
        var settings = new PetClientConfiguration(317, 12600);
        var stamp = wire.StampOf(14, settings);
        var checksums = Enumerable.Range(0, 16).Select(bit => 0x10203000u + (uint)bit).ToArray();
        checksums[14] = stamp;
        var request = PetDynamicConfigTestData.Request(2, ushort.MaxValue, checksums);
        Assert.Equal(66, request.Length);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "cn-774", "DynamicConfigRequest", request);
        Assert.Equal(ushort.MaxValue, Assert.IsType<ushort>(fields["mask"]));
        Assert.Null(wire.Reply(request, settings));

        checksums[0] = stamp;
        checksums[14] = stamp ^ 1;
        checksums[15] = stamp;
        var reply = Assert.IsType<byte[]>(wire.Reply(PetDynamicConfigTestData.Request(2, ushort.MaxValue, checksums), settings));
        Assert.Equal(wire.CreationSnapshot(settings), reply);
        var replyFields = PacketCodec.Read(TestBuilds.Packets, "cn-774", "DynamicConfigReply", reply);
        Assert.Equal(new[] { "mask", "slot_14" }, replyFields.Keys.OrderBy(key => key).ToArray());
        Assert.Null(wire.Reply(PetDynamicConfigTestData.Request(2, 0xBFFF,
            checksums.Where((_, bit) => bit != 14).ToArray()), settings));
        Assert.Null(wire.Reply(PetDynamicConfigTestData.Request(2, 0), settings));
        Assert.Throws<PacketFormatException>(() => wire.Reply(request[..^4], settings));
        Assert.Throws<PacketFormatException>(() => wire.Reply([.. request, 0, 0, 0, 0], settings));
    }

    /// <summary>An active CN category rejects missing masks, missing checksum bytes and trailing request data.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    public void CnActiveCategoryRejectsMalformedRequests(int length)
    {
        var request = new byte[length];
        if (length >= 2) BinaryPrimitives.WriteUInt16LittleEndian(request, 1 << 14);
        var wire = new DynamicConfigWire(TestBuilds.Packets, "cn-774");
        Assert.Throws<PacketFormatException>(() => wire.Reply(request, new(317, 12600)));
    }

    /// <summary>The CN envelope reader requires its declared compressed bytes to consume the entire reply.</summary>
    [Fact]
    public void CnReplyRejectsTruncatedOrMismatchedCompressedLengths()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "cn-774");
        var reply = wire.CreationSnapshot(new(317, 12600));
        foreach (var length in new[] { 0, 1, 2, 5, reply.Length - 1 })
            Assert.Throws<PacketFormatException>(() => Read(reply[..length]));
        Assert.Throws<PacketFormatException>(() => Read([.. reply, 0]));
        foreach (var difference in new[] { -1, 1 })
        {
            var malformed = reply.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(2), reply.Length - 6 + difference);
            Assert.Throws<PacketFormatException>(() => Read(malformed));
        }

        static void Read(byte[] body) => PacketCodec.Read(TestBuilds.Packets, "cn-774", "DynamicConfigReply", body);
    }

    /// <summary>Builds without independently admitted pet content reject supplied settings and retain their empty generations.</summary>
    [Theory]
    [InlineData("cn-735", 2)]
    [InlineData("tw-454", 2)]
    [InlineData("kr-812", 4)]
    public void UnsupportedBuildsRejectPetSettings(string build, int maskBytes)
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, build);
        var settings = new PetClientConfiguration(317, 12600);
        Assert.False(wire.SupportsPetConfiguration);
        Assert.Throws<ConfigException>(() => wire.Validate(settings));
        Assert.Throws<ConfigException>(() => wire.CreationSnapshot(settings));
        Assert.Throws<ConfigException>(() => wire.Reply(PetDynamicConfigTestData.Request(maskBytes, 0), settings));
        Assert.Equal(new byte[maskBytes], wire.CreationSnapshot());
        Assert.Empty(wire.ServedBits);
    }
}

internal static class PetDynamicConfigTestData
{
    internal static byte[] Request(int maskBytes, uint mask, params uint[] checksums)
    {
        var request = new byte[maskBytes + checksums.Length * 4];
        if (maskBytes == 2) BinaryPrimitives.WriteUInt16LittleEndian(request, checked((ushort)mask));
        else BinaryPrimitives.WriteUInt32LittleEndian(request, mask);
        for (var index = 0; index < checksums.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(maskBytes + index * 4), checksums[index]);
        return request;
    }

    internal static byte[] Compressed(byte[] reply, int maskBytes)
    {
        Assert.Equal(reply.Length - maskBytes - 4, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(maskBytes)));
        return reply[(maskBytes + 4)..];
    }

    internal static byte[] Inflate(byte[] reply, int maskBytes)
    {
        using var input = new MemoryStream(Compressed(reply, maskBytes));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    internal static void AssertSettings(byte[] reply, int maskBytes, PetClientConfiguration settings)
    {
        if (maskBytes == 2) Assert.Equal((ushort)(1 << 14), BinaryPrimitives.ReadUInt16LittleEndian(reply));
        else Assert.Equal(1u << 14, BinaryPrimitives.ReadUInt32LittleEndian(reply));
        var plain = Inflate(reply, maskBytes);
        Assert.Equal(BitConverter.SingleToInt32Bits((float)(settings.FeedCooldownSeconds / 86400d)),
            BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(plain.Length - 8)));
        Assert.Equal(settings.MileageDisplayMaximum, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(plain.Length - 4)));
    }
}
