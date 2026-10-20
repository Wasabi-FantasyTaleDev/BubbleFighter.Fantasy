using BubbleFighter.Fantasy.Abstractions.Protocol;

using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Pins the S2C 811 / 812 reply bodies to the 12 bytes the client handlers
/// <c>sub_9B34D0</c> / <c>sub_9B3E00</c> read: one little-endian <c>i32</c> result and one
/// little-endian <c>i64</c> key, with nothing following.
/// </summary>
public sealed class EquipmentWireByteLengthTests
{
    private const long SampleKey = 0x1122334455667788L;

    [Fact]
    public void RepInventoryEquipPinsTwelveBytesAndDocumentedHex()
    {
        var payload = EquipmentWire.RepInventoryEquip(EquipmentWire.SuccessResult, SampleKey);
        Assert.Equal(EquipmentWire.RepInventoryEquipBytes, payload.Length);
        Assert.Equal(12, payload.Length);
        Assert.Equal("00000000" + "8877665544332211", Convert.ToHexString(payload));
    }

    [Fact]
    public void RepInventoryUnequipPinsTwelveBytesAndDocumentedHex()
    {
        var payload = EquipmentWire.RepInventoryUnequip(EquipmentWire.SuccessResult, SampleKey);
        Assert.Equal(EquipmentWire.RepInventoryUnequipBytes, payload.Length);
        Assert.Equal(12, payload.Length);
        Assert.Equal("00000000" + "8877665544332211", Convert.ToHexString(payload));
    }

    [Fact]
    public void RepInventoryEquipIsFullyConsumedBySub9B34D0()
    {
        var payload = EquipmentWire.RepInventoryEquip(0, SampleKey);
        var r = new PacketReader(payload);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(SampleKey, r.ReadInt64());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RepInventoryUnequipIsFullyConsumedBySub9B3E00()
    {
        var payload = EquipmentWire.RepInventoryUnequip(0, SampleKey);
        var r = new PacketReader(payload);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(SampleKey, r.ReadInt64());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RepliesPinTheSameShapeForAnyResultValue()
    {
        // The client reads the i64 even on failure, so the body length never varies.
        Assert.Equal(12, EquipmentWire.RepInventoryEquip(1, 0).Length);
        Assert.Equal(12, EquipmentWire.RepInventoryEquip(10, -1).Length);
        Assert.Equal(12, EquipmentWire.RepInventoryUnequip(1, 0).Length);
        Assert.Equal(12, EquipmentWire.RepInventoryUnequip(3, -1).Length);
    }

    [Fact]
    public void SuccessResultIsTheProvenCaseZero()
    {
        Assert.Equal(0, EquipmentWire.SuccessResult);
    }
}
