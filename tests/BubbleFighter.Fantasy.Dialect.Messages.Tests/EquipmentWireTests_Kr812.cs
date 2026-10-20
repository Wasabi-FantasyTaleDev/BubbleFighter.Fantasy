using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class EquipmentWireTests_Kr812
{
    [Fact]
    public void WearRequestMatchesReferenceItemIdAndRejectsModernWidth()
    {
        // C803, four bytes.
        Assert.Equal(204033, EquipmentRequestWire.ReadRequest(TestBuilds.Packets, "kr-812", Convert.FromHexString("011D0300"), true).ItemId);
        Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, "kr-812", new byte[3], true));
        Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, "kr-812", new byte[8], true));
    }

    [Fact]
    public void ResultsContainOnlyTheFourByteDiscriminator()
    {
        // 0x824AC0 / 0x824F00 switch on zero for success and one for the database failure path.
        Assert.Equal(new byte[4], EquipmentRequestWire.WriteResult(TestBuilds.Packets, "kr-812", 0, 204033));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, EquipmentRequestWire.WriteResult(TestBuilds.Packets, "kr-812", 1, 204033));
    }

    [Fact]
    public void EquippedViewMatchesTheReferenceWeaponChange()
    {
        // S805 after the C803 request.
        var expected = Convert.FromHexString(
            "1A010000EA9304000000000013000000EE8601000F8701002F87010080870100" +
            "9187010092870100D6870100D7870100C0890100C1890100C2890100C3890100" +
            "C4890100B68A0100E9200300EA93040036110300011D03009928030000000000" +
            "0000000000000000");
        var actual = EquipmentViewBinding.WriteView(TestBuilds.Packets, "kr-812", 282, 300010,
            [100078, 100111, 100143, 100224, 100241, 100242, 100310, 100311,
             100800, 100801, 100802, 100803, 100804, 101046, 205033, 300010,
             201014, 204033, 207001]);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptyViewKeepsEveryTrailingCount() =>
        Assert.Equal(28, EquipmentViewBinding.WriteView(TestBuilds.Packets, "kr-812", 1, 300001, []).Length);

    [Fact]
    public void OlderChineseViewsEndEarlier()
    {
        // cn-324 sub_45B640 reads three i32, the i32 vector and one more vector, then stops.
        Assert.Equal(Convert.FromHexString(
            "01000000" + "E1930400" + "00000000" + "02000000" + "11150300" + "29110300" + "00000000"),
            EquipmentViewBinding.WriteView(TestBuilds.Packets, "cn-324", 1, 300001, [202001, 201001]));
        Assert.Equal(20, EquipmentViewBinding.WriteView(TestBuilds.Packets, "cn-324", 1, 300001, []).Length);

        // cn-279 sub_448840 reads two scalars and the i32 vector alone.
        Assert.Equal(Convert.FromHexString("01000000" + "E1930400" + "02000000" + "29110300" + "F9180300"),
            EquipmentViewBinding.WriteView(TestBuilds.Packets, "cn-279", 1, 300001, [201001, 203001]));
    }

    [Fact]
    public void Kr1256CarriesTheEightByteKeyAndEchoesItAfterTheResult()
    {
        // kr-1256 sub_659850 writes one i64; kr-1256 sub_9B34D0 reads an i32 result and that i64.
        var request = EquipmentRequestWire.ReadRequest(TestBuilds.Packets, "kr-1256", Convert.FromHexString("2B11030000000000"), false);
        Assert.Equal((201003L, 201003, false), (request.Reference, request.ItemId, request.Worn));
        Assert.Null(EquipmentRequestWire.ReadRequest(TestBuilds.Packets, "kr-1256", Convert.FromHexString("00E1F50500000000"), true).ItemId);
        Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, "kr-1256", new byte[4], true));
        Assert.Equal("030000002B11030000000000", Convert.ToHexString(EquipmentRequestWire.WriteResult(TestBuilds.Packets, "kr-1256", 3, 201003)));
        Assert.True(TestBuilds.Packets.CapabilityOf("kr-1256", "equip_result_reply"));
        Assert.False(EquipmentValues.ResultAfterInventory(TestBuilds.Packets, "kr-1256"));
        Assert.True(EquipmentValues.ResultAfterInventory(TestBuilds.Packets, "cn-324"));
    }
}
