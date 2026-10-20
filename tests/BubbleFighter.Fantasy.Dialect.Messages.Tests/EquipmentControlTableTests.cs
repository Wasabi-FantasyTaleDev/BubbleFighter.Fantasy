using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins migrated equipment, purchase and allegiance scalars across native legacy layouts.</summary>
public sealed class EquipmentControlTableTests
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
    public void ScalarsPreserveWordBitsResultPolarityAndNativeLengths(string build)
    {
        Assert.Equal(-1L, EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, Convert.FromHexString("FFFFFFFF"), true).Reference);
        Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, new byte[3], true));
        Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, new byte[5], true));
        Assert.Equal("00000000", Convert.ToHexString(EquipmentRequestWire.WriteResult(TestBuilds.Packets, build, 0, 204033)));
        Assert.Equal("03000000", Convert.ToHexString(EquipmentRequestWire.WriteResult(TestBuilds.Packets, build, 3, 204033)));
        Assert.Equal("FFFFFFFF01000000", Convert.ToHexString(ShopBinding.WriteReply(TestBuilds.Packets, build, new(-1, true))));
        Assert.Equal("FFFFFFFF00000000", Convert.ToHexString(ShopBinding.WriteReply(TestBuilds.Packets, build, new(-1, false))));
        Assert.Equal("010000000F000000", Convert.ToHexString(ZombieConversionCodec.Encode(TestBuilds.Packets, build, new(1, 15))));
        Assert.Equal("0000000002000000", Convert.ToHexString(ZombieConversionCodec.Encode(TestBuilds.Packets, build, new(0, 2))));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void PresetRequestKeepsSignedContextAndTheUnfilteredNativeByte(string build)
    {
        Assert.Equal(new WeaponPresetChangeRequested(-1, 4),
            PresetWire.ReadChangeRequest(TestBuilds.Packets, build, Convert.FromHexString("FFFFFFFF04")));
        Assert.Equal(new WeaponPresetChangeRequested(1000000, 255),
            PresetWire.ReadChangeRequest(TestBuilds.Packets, build, Convert.FromHexString("40420F00FF")));
        Assert.Throws<PacketFormatException>(() => PresetWire.ReadChangeRequest(TestBuilds.Packets, build, new byte[4]));
        Assert.Throws<PacketFormatException>(() => PresetWire.ReadChangeRequest(TestBuilds.Packets, build, new byte[6]));
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    public void EarlyClientsCannotUseAnEmptyPresetRequestAsAPlaceholder(string build) =>
        Assert.Throws<ConfigException>(() => PresetWire.ReadChangeRequest(TestBuilds.Packets, build, []));
}
