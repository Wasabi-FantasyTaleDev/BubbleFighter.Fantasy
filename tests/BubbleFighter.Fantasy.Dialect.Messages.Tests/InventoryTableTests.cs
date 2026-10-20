using System.Security.Cryptography;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>S801/S802 (<c>InventoryUpdate</c>) and S805 (<c>EquippedItemList</c>) as the packet tables write them.</summary>
public sealed class InventoryTableTests
{
    private static readonly InventorySlot[] Slots =
    [
        new(204033, 9, 2.5, true, false, true, false, true, string.Empty),
        new(300001, 0, -1, false, true, false, true, false, string.Empty),
    ];

    // SHA-256 prefixes of the bodies the hand-written writers produced before the tables replaced them.
    [Theory]
    [InlineData("kr-812", "F7CFA5871F3F6FC693E3F0090EB9234D", "4E7E46644848DBBBBAA055353E2BED29", "E4BE22A6F8328FF95414012CCA1FD905")]
    [InlineData("cn-717", "F7CFA5871F3F6FC693E3F0090EB9234D", "4E7E46644848DBBBBAA055353E2BED29", "E4BE22A6F8328FF95414012CCA1FD905")]
    [InlineData("id-400", "F7CFA5871F3F6FC693E3F0090EB9234D", "4E7E46644848DBBBBAA055353E2BED29", "E4BE22A6F8328FF95414012CCA1FD905")]
    [InlineData("id-1809", "F7CFA5871F3F6FC693E3F0090EB9234D", "4E7E46644848DBBBBAA055353E2BED29", "E4BE22A6F8328FF95414012CCA1FD905")]
    [InlineData("cn-706", "F7CFA5871F3F6FC693E3F0090EB9234D", "4E7E46644848DBBBBAA055353E2BED29", "E4BE22A6F8328FF95414012CCA1FD905")]
    [InlineData("cn-324", "513DF6CF77F4EE3E82ED92F086FA70C6", "1D66C9C289E684F51B02A2566CB41D71", "25E55BB6A2BD6FDF1F5DA92D6A0034AF")]
    [InlineData("cn-279", "513DF6CF77F4EE3E82ED92F086FA70C6", "01E38994D950CC7BCAC4079B9E3A7F6D", "DBE180B729CD74CDD7711BC61D08336C")]
    public void EveryBuildWritesTheBytesOfItsFormerWriter(string build, string inventory, string view, string emptyView)
    {
        var wire = new InventoryWire(TestBuilds.Packets, build);
        Assert.Equal(inventory, Hash(wire.RepInventoryAll(Slots)));
        Assert.Equal(new byte[4], wire.RepInventoryAll([]));
        Assert.Equal(view, Hash(EquipmentViewBinding.WriteView(TestBuilds.Packets, build, 1234, 300002, [100001, 100002, 100003])));
        Assert.Equal(emptyView, Hash(EquipmentViewBinding.WriteView(TestBuilds.Packets, build, 7, 300000, [])));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];
}
