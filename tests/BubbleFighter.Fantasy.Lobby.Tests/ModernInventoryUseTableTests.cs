using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// Pins the modern item use bodies: kr-1256 and cn-774 carry a counted string list (kr-1256 sub_80DA80 /
/// sub_9B4100), tw-454 one string (sub_C0AE70 / sub_A901A0), and S2C 105 field 0 carries the color index.
/// </summary>
public sealed class ModernInventoryUseTableTests
{
    private static readonly Lazy<PacketTables> Tables = new(() => PacketTables.Load(PacketsDirectory()));

    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }

    /// <summary>A spray's request: item, quantity 1 and an empty list (kr-1256 sub_D407C0 case 1).</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void ListBuildsReadTheSprayRequestWithAnEmptyList(string build)
    {
        byte[] body = [0x41, 0x0D, 0x03, 0x00, 1, 0, 0, 0, 0, 0, 0, 0];
        var fields = PacketCodec.Read(Tables.Value, build, "InventoryUseRequest", body);
        Assert.Equal(200001, Convert.ToInt32(fields["item_id"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.ICollection>(fields["parameters"]));
        Assert.Equal(body, PacketCodec.Write(Tables.Value, build, "InventoryUseRequest", fields));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, build, "InventoryUseRequest", body[..^1]));
    }

    /// <summary>The reply carries result, item and the list: one string is a count of one, then the string.</summary>
    [Fact]
    public void Kr1256ReplyWritesTheParametersAsAList()
    {
        var bytes = PacketCodec.Write(Tables.Value, "kr-1256", "InventoryUseReply", new Dictionary<string, object?>
        {
            ["result"] = 0, ["item_id"] = 200001, ["parameters"] = new[] { "ab" },
        });
        Assert.Equal(Convert.FromHexString("00000000410D0300" + "01000000" + "02000000" + "61006200"), bytes);
        var empty = PacketCodec.Write(Tables.Value, "kr-1256", "InventoryUseReply", new Dictionary<string, object?>
        {
            ["result"] = 1, ["item_id"] = 200001, ["parameters"] = Array.Empty<string>(),
        });
        Assert.Equal(12, empty.Length);
    }

    /// <summary>tw-454 keeps the legacy single string in both directions.</summary>
    [Fact]
    public void Tw454KeepsOneString()
    {
        var bytes = PacketCodec.Write(Tables.Value, "tw-454", "InventoryUseReply", new Dictionary<string, object?>
        {
            ["result"] = 0, ["item_id"] = 7, ["parameters"] = "",
        });
        Assert.Equal(12, bytes.Length);
        var request = PacketCodec.Read(Tables.Value, "tw-454", "InventoryUseRequest", Convert.FromHexString("070000000100000000000000"));
        Assert.Equal("", request["parameters"]);
    }

    /// <summary>S2C 105 starts with the color index the client applies to the local actor (kr-1256 sub_9A5C40).</summary>
    [Fact]
    public void UserStatCarriesTheColorIndexFirst()
    {
        var tables = WireLengthSupport.Tables;
        var plain = BubbleFighter.Fantasy.Dialect.Messages.UserStatWire.Write(tables, "kr-1256", "nick", 10, 0, 0, 0, 1001);
        var painted = BubbleFighter.Fantasy.Dialect.Messages.UserStatWire.Write(tables, "kr-1256", "nick", 10, 0, 0, 23, 1001);
        Assert.Equal(plain.Length, painted.Length);
        Assert.Equal(23, BitConverter.ToInt32(painted, 0));
        Assert.Equal(plain[4..], painted[4..]);
    }
}
