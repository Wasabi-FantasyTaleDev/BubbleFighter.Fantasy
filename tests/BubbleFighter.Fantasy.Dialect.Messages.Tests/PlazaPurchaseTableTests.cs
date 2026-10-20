using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins plaza floating-point bit preservation and purchase validation across inherited layouts.</summary>
public sealed class PlazaPurchaseTableTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-735")]
    public void BroadcastPreservesAllReportedFloatBitsAndFlags(string build)
    {
        uint[] patterns = [0x80000000, 0x7FC12345, 0x7FA12345, 0xFFC12345, 0x7F800000, 0xFF800000, 0x00000001];
        foreach (var bits in patterns)
        {
            var upload = new byte[22];
            BinaryPrimitives.WriteInt32LittleEndian(upload, -1);
            foreach (var offset in new[] { 4, 8, 12, 18 })
                BinaryPrimitives.WriteUInt32LittleEndian(upload.AsSpan(offset), bits);
            upload[16] = 0xFE;
            upload[17] = 0x80;
            var parsed = PlayRoomCodec.ReadPositionUpload(TestBuilds.Packets, build, upload);
            Assert.Equal(upload, parsed.Body.ToArray());
            var broadcast = PlayRoomCodec.WritePlazaPositionLine(TestBuilds.Packets, build, -1, int.MinValue, upload);
            Assert.Equal(26, broadcast.Length);
            Assert.Equal(-1, BitConverter.ToInt32(broadcast, 0));
            Assert.Equal(int.MinValue, BitConverter.ToInt32(broadcast, 4));
            Assert.Equal(upload[4..], broadcast[8..]);
        }
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadPositionUpload(TestBuilds.Packets, build, new byte[21]));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadPositionUpload(TestBuilds.Packets, build, new byte[23]));
        Assert.Equal(PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", 282, 21, 7),
            PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, build, 282, 21, 7));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-735")]
    public void PurchasesRetainCanonicalOptionsAndSignedKeyBits(string build)
    {
        Assert.Equal(new ShopPurchaseRequested(-1, true),
            ShopBinding.ReadPurchase(TestBuilds.Packets, build, Convert.FromHexString("FFFFFFFF00000000")));
        Assert.Equal(new ShopPurchaseRequested(-1, false),
            ShopBinding.ReadPurchase(TestBuilds.Packets, build, Convert.FromHexString("FFFFFFFF01000000")));
        foreach (var word in new[] { "02000000", "00010000", "FFFFFFFF" })
        {
            var body = Convert.FromHexString("FFFFFFFF" + word);
            Assert.Throws<PacketFormatException>(() => ShopBinding.ReadPurchase(TestBuilds.Packets, build, body));
            Assert.True((bool)PacketCodec.ReadScalars(TestBuilds.Packets, build, "ShopPurchaseRequest", body)["unknown_8"]);
        }
        Assert.Throws<PacketFormatException>(() => ShopBinding.ReadPurchase(TestBuilds.Packets, build, new byte[7]));
        Assert.Throws<PacketFormatException>(() => ShopBinding.ReadPurchase(TestBuilds.Packets, build, new byte[9]));
    }
}
