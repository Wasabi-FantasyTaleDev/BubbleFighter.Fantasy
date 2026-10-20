using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class ShopLayoutTests
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    [Theory]
    [InlineData("kr", 812, 380, 44, true, 44)]
    [InlineData("cn", 717, 380, 44, true, 44)]
    [InlineData("id", 400, 340, 44, true, 44)]
    [InlineData("id", 1809, 344, 44, true, 44)]
    [InlineData("cn", 324, 256, 36, true, 44)]
    [InlineData("cn", 279, 168, 36, true, 32)]
    public void PinsNativeStatusOffsetsAndInventoryStride(
        string region, int build, int minimum, int stride, bool checkCash, int nameOffset)
    {
        Assert.Equal(checkCash, Tables.CapabilityOf($"{region}-{build}", "cash_check_request"));
        Assert.Equal(minimum, ShopBinding.WriteStat(Tables, $"{region}-{build}", string.Empty, 1, 0, 0).Length);
        const string name = "A\u6C34\uD840\uDC00";
        var stat = ShopBinding.WriteStat(Tables, $"{region}-{build}", name, 27, 12345, 67890, 14);
        Assert.Equal(minimum + 8, stat.Length);
        Assert.Equal(14, Int(stat, 0));
        Assert.Equal(12345, Int(stat, 16));
        Assert.Equal(67890, Int(stat, 20));
        Assert.Equal(4, Int(stat, nameOffset));
        Assert.Equal(Encoding.Unicode.GetBytes(name), stat.AsSpan(nameOffset + 4, 8).ToArray());
        Assert.Equal(0, Int(stat, nameOffset + 12));
        Assert.Equal(27, Int(stat, nameOffset + 16));
        var slot = new InventorySlot(204033, 9, 2.5, true, false, true, false, true, string.Empty);
        var wire = new InventoryWire(Tables, $"{region}-{build}");
        var extended = stride == 44;
        var inventory = wire.RepInventoryAll([slot, slot]);
        Assert.Equal(4 + 2 * stride, inventory.Length);
        Assert.Equal(2, Int(inventory, 0));
        foreach (var offset in new[] { 4, 4 + stride })
        {
            Assert.Equal(204033, Int(inventory, offset));
            Assert.Equal(9, Int(inventory, offset + 4));
            Assert.Equal(2.5, BinaryPrimitives.ReadDoubleLittleEndian(inventory.AsSpan(offset + 8)));
            Assert.Equal(new[] { 1, 0, 1, 0, 1 }, Enumerable.Range(0, 5).Select(i => Int(inventory, offset + 16 + 4 * i)));
            if (extended)
                Assert.Equal(new byte[8], inventory.AsSpan(offset + 36, 8).ToArray());
        }
        Assert.Equal(new byte[4], wire.RepInventoryAll([]));
    }

    // Each build's values; descendants inherit through builds.yaml bases.
    [Theory]
    [InlineData("kr-812", true, false)]
    [InlineData("cn-717", true, false)]
    [InlineData("cn-735", true, false)]
    [InlineData("cn-324", true, true)]
    [InlineData("cn-279", true, true)]
    [InlineData("id-400", true, false)]
    [InlineData("id-1809", true, false)]
    [InlineData("cn-706", true, false)]
    [InlineData("cn-406", false, false)]
    public void PinsShopCapabilitiesPerBuild(string build, bool cashCheckRequest, bool equipResultReply)
    {
        Assert.Equal(cashCheckRequest, Tables.CapabilityOf(build, "cash_check_request"));
        Assert.Equal(equipResultReply, Tables.CapabilityOf(build, "equip_result_reply"));
    }

    [Fact]
    public void ShortInventoryRefusesUnrepresentableExtension()
    {
        var slot = new InventorySlot(1, 1, 1, false, false, false, false, false, "extension");
        Assert.Throws<ArgumentException>(() => new InventoryWire(Tables, "cn-324").RepInventoryAll([slot]));
    }

    private static int Int(byte[] value, int offset) => BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(offset));
}
