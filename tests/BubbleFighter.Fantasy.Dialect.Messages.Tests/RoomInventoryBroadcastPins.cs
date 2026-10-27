using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins S2C gspRoomChangeInventory (506) before any binding exists: the legacy body of
/// <c>packets/room-details.yaml</c> (three words and a counted word list, read by kr-812 sub_80C100,
/// cn-717 sub_890EC0, cn-324 sub_6EB2E0 and cn-279 sub_449C30), the two-word kr-1256 body
/// (kr-1256 sub_9D9930 case 0x1FA), and the longer modern CN/TW form. A body that changes on
/// purpose updates its pin together with the client reader that decides it.
/// </summary>
public sealed class RoomInventoryBroadcastPins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The legacy builds read three four-byte words and then a counted four-byte list.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    public void LegacyBodyPinsThreeWordsAndACountedList(string build)
    {
        var body = PacketCodec.Write(Tables, build, "RoomInventoryChanged", new Dictionary<string, object?>
        {
            ["unknown_4"] = 1,
            ["unknown_8"] = 2,
            ["unknown_12"] = 3,
            ["unknown_16"] = new[] { 7, 8 },
        });
        Assert.Equal(24, body.Length);
        Assert.Equal("010000000200000003000000020000000700000008000000", Convert.ToHexString(body));

        var fields = PacketCodec.Read(Tables, build, "RoomInventoryChanged", body);
        Assert.Equal(4, fields.Count);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "RoomInventoryChanged", fields));
        AssertRejectsIncompleteOrTrailing(build, "RoomInventoryChanged", body);
    }

    /// <summary>The kr-1256 handler reads only the seat word and one more word.</summary>
    [Fact]
    public void Kr1256BodyPinsTwoWordsOnly()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "RoomInventoryChanged", new Dictionary<string, object?>
        {
            ["slot"] = 1,
            ["unknown_8"] = 2,
        });
        Assert.Equal(8, body.Length);
        Assert.Equal("0100000002000000", Convert.ToHexString(body));

        var fields = PacketCodec.Read(Tables, "kr-1256", "RoomInventoryChanged", body);
        Assert.Equal(2, fields.Count);
        Assert.Equal(body, PacketCodec.Write(Tables, "kr-1256", "RoomInventoryChanged", fields));
        AssertRejectsIncompleteOrTrailing("kr-1256", "RoomInventoryChanged", body);
    }

    /// <summary>The modern CN/TW consumers restore the longer form with the first word as the seat.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernCnTwBodyPinsSeatAndTheLongerForm(string build)
    {
        var body = PacketCodec.Write(Tables, build, "RoomInventoryChanged", new Dictionary<string, object?>
        {
            ["slot"] = 1,
            ["unknown_8"] = 2,
            ["unknown_12"] = 3,
            ["unknown_16"] = new[] { 7, 8 },
        });
        Assert.Equal(24, body.Length);
        Assert.Equal("010000000200000003000000020000000700000008000000", Convert.ToHexString(body));

        var fields = PacketCodec.Read(Tables, build, "RoomInventoryChanged", body);
        Assert.Equal(4, fields.Count);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "RoomInventoryChanged", fields));
        AssertRejectsIncompleteOrTrailing(build, "RoomInventoryChanged", body);
    }

    private static void AssertRejectsIncompleteOrTrailing(string build, string structure, byte[] body)
    {
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, structure, [.. body, 0]));
    }
}
