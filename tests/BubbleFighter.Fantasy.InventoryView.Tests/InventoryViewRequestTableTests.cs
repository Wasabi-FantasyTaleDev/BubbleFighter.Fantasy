using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Pins the view request body through the packet tables (<c>InventoryUseRequest</c> of
/// <c>packets/inventory-use.yaml</c>): kr-1256 builder <c>sub_80DA80</c> stamps 806 and writes
/// item, quantity, then the counted string list of <c>kr-1256 sub_429D10</c>; the kr-812 baseline
/// reader <c>kr-812 sub_8B9490</c> takes item, quantity and one string. The fixed bodies below
/// are the bytes the deleted hand-written reader pinned, so a table regression changes an
/// assertion here and not just the logs.
/// </summary>
public sealed class InventoryViewRequestTableTests
{
    private static readonly PacketTables Tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());

    private static int Word(Dictionary<string, object?> fields, string name) =>
        Convert.ToInt32(fields[name], CultureInfo.InvariantCulture);

    [Fact]
    public void EmptyListReadsAndWritesTheTwelveByteBody()
    {
        var hex = "05000000" + "06000000" + "00000000";
        var fields = PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", Convert.FromHexString(hex));
        Assert.Equal(5, Word(fields, "item_id"));
        Assert.Equal(6, Word(fields, "quantity"));
        Assert.Empty(Assert.IsAssignableFrom<object?[]>(fields["parameters"]));

        var body = PacketCodec.Write(Tables, "kr-1256", "InventoryUseRequest", new Dictionary<string, object?>
        {
            ["item_id"] = 5, ["quantity"] = 6, ["parameters"] = Array.Empty<string>(),
        });
        Assert.Equal(12, body.Length);
        Assert.Equal(hex, Convert.ToHexString(body));
    }

    [Fact]
    public void TwoRecordsReadAndWriteTheTwentyFourByteBody()
    {
        var hex = "01000000" + "02000000" + "02000000" + "02000000" + "41004200" + "00000000";
        var fields = PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", Convert.FromHexString(hex));
        Assert.Equal(1, Word(fields, "item_id"));
        Assert.Equal(2, Word(fields, "quantity"));
        Assert.Equal(["AB", ""], Assert.IsAssignableFrom<object?[]>(fields["parameters"]));

        var body = PacketCodec.Write(Tables, "kr-1256", "InventoryUseRequest", new Dictionary<string, object?>
        {
            ["item_id"] = 1, ["quantity"] = 2, ["parameters"] = new[] { "AB", "" },
        });
        Assert.Equal(24, body.Length);
        Assert.Equal(hex, Convert.ToHexString(body));
    }

    [Fact]
    public void KoreanTextSurvivesTheTableRoundTrip()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "InventoryUseRequest", new Dictionary<string, object?>
        {
            ["item_id"] = -1, ["quantity"] = 0, ["parameters"] = new[] { "다오" },
        });
        var fields = PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", body);
        Assert.Equal(-1, Word(fields, "item_id"));
        Assert.Equal(["다오"], Assert.IsAssignableFrom<object?[]>(fields["parameters"]));
    }

    /// <summary>The kr-812 baseline of the same packet: one string where kr-1256 writes the list.</summary>
    [Fact]
    public void Kr812BaselineTakesASingleParameterString()
    {
        var hex = "05000000" + "06000000" + "02000000" + "41004200";
        var fields = PacketCodec.Read(Tables, "kr-812", "InventoryUseRequest", Convert.FromHexString(hex));
        Assert.Equal(5, Word(fields, "item_id"));
        Assert.Equal(6, Word(fields, "quantity"));
        Assert.Equal("AB", fields["parameters"]);

        var body = PacketCodec.Write(Tables, "kr-812", "InventoryUseRequest", new Dictionary<string, object?>
        {
            ["item_id"] = 5, ["quantity"] = 6, ["parameters"] = "AB",
        });
        Assert.Equal(hex, Convert.ToHexString(body));
    }

    [Fact]
    public void TrailingBytesFailLoud()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(0);
            w.WriteByte(0);
        });
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", payload));
    }

    [Fact]
    public void TruncatedStringFailsLoud()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(1);
            w.WriteUInt32(3);
            w.WriteBytes(System.Text.Encoding.Unicode.GetBytes("Da"));
        });
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", payload));
    }

    [Fact]
    public void ImplausibleCountFailsLoud()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(100);
        });
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", payload));
    }

    [Fact]
    public void EmptyPayloadFailsLoud()
    {
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", []));
    }

}
