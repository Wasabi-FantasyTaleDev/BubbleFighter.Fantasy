using System.Security.Cryptography;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-1256 item-use bodies before that build moves off the modern row: the counted string
/// list in both directions (<c>InventoryUseRequest</c>/<c>InventoryUseReply</c> of
/// <c>packets/inventory-use.yaml</c>, kr-1256 sub_80DA80/sub_9B4100) and the user-state refresh whose
/// nested FighterInfo the modern login codec fills (kr-1256 sub_9D8030, <c>UserStat@kr-1256</c>).
/// A body that changes on purpose updates its pin together with the client reader that decides it.
/// </summary>
public sealed class InventoryUseKr1256Pins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>A spray reply carries no parameters: result, item identity, then a zero list count.</summary>
    [Fact]
    public void SprayReplyWithAnEmptyParameterListIsPinned()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "InventoryUseReply", new Dictionary<string, object?>
        {
            ["result"] = 0, ["item_id"] = 100180, ["parameters"] = Array.Empty<string>(),
        });
        Assert.Equal(12, body.Length);
        Assert.Equal("000000005487010000000000", Convert.ToHexString(body));
    }

    /// <summary>A request that carried more than one parameter is answered with result 1 and no parameters.</summary>
    [Fact]
    public void RefusedReplyWithTwoRequestParametersIsPinned()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "InventoryUseReply", new Dictionary<string, object?>
        {
            ["result"] = 1, ["item_id"] = 200001, ["parameters"] = Array.Empty<string>(),
        });
        Assert.Equal(12, body.Length);
        Assert.Equal("01000000410D030000000000", Convert.ToHexString(body));
    }

    /// <summary>
    /// The refresh after a paint: color, wallet and the nested FighterInfo the modern client reads
    /// (identity +4, nickname +8, character type +32, level +36, name +504 of FighterInfo@kr-1256).
    /// </summary>
    [Fact]
    public void UserStateRefreshIsPinned()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "UserStat", new Dictionary<string, object?>
        {
            ["color_index"] = (ushort)14,
            ["lucci"] = 12345,
            ["cash"] = 678,
            ["fighter"] = new Dictionary<string, object?>
            {
                ["unknown_4"] = 1001,
                ["nickname"] = "nick",
                ["unknown_32"] = 1,
                ["level"] = 10,
                ["unknown_504"] = "nick",
                ["social"] = new Dictionary<string, object?>(),
            },
        });
        Assert.Equal(494, body.Length);
        Assert.Equal("0605263A23BCEB22FFFE0866DB319C0A", Convert.ToHexString(SHA256.HashData(body))[..32]);
    }

    /// <summary>The request field is the counted list the modern client writes, in table data only.</summary>
    [Fact]
    public void RequestParametersAreTheCountedList()
    {
        var parameter = Assert.Single(Tables.Resolve("kr-1256", "InventoryUseRequest"), field => field.Name == "parameters");
        Assert.Equal("list<wstr>", parameter.Type);
        var spray = PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest",
            Convert.FromHexString("410D03000100000000000000"));
        Assert.Equal(200001, Convert.ToInt32(spray["item_id"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(spray["quantity"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(Assert.IsAssignableFrom<object?[]>(spray["parameters"]));
        var two = PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest",
            Convert.FromHexString("410D0300010000000200000002000000610062000200000063006400"));
        Assert.Equal(["ab", "cd"], Assert.IsAssignableFrom<object?[]>(two["parameters"]));
    }
}
