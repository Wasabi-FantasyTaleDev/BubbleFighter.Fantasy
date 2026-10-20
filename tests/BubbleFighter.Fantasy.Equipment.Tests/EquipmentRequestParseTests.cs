using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Pins <c>InventoryEquipmentRequest@kr-1256</c>: kr-1256 sub_659850 (wear) and sub_805A60 (removal) each write one
/// i64 key, nothing more. Keys below 100000000 are bag item ids (kr-1256 sub_806CE0); the key is kept as the reference.
/// </summary>
public sealed class EquipmentRequestParseTests
{
    private const long SampleKey = 0x1122334455667788L;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARequestIsEightBytesAndRejectsAnythingElse(bool worn)
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt64(SampleKey));
        Assert.Equal(8, payload.Length);

        var parsed = EquipmentCodec.Decode(payload, worn);
        Assert.Equal(SampleKey, parsed.Reference);
        Assert.Null(parsed.ItemId);
        Assert.Equal(worn, parsed.Worn);

        Assert.Throws<PacketFormatException>(() => EquipmentCodec.Decode([.. payload, 0], worn));
        Assert.ThrowsAny<Exception>(() => EquipmentCodec.Decode(payload[..4], worn));
        Assert.ThrowsAny<Exception>(() => EquipmentCodec.Decode([], worn));
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    [InlineData(201003L, 201003)]
    [InlineData(99999999L, 99999999)]
    [InlineData(100000000L, null)]
    public void OnlyTheBagBandNamesAnItem(long key, int? itemId)
    {
        var parsed = EquipmentCodec.Decode(WireLengthSupport.Written(w => w.WriteInt64(key)), true);
        Assert.Equal(key, parsed.Reference);
        Assert.Equal(itemId, parsed.ItemId);
    }
}
