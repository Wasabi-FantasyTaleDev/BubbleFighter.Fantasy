using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the guild message wall of the legacy builds, stated in <c>packets/guild-board.yaml</c> from the
/// kr-812 readers: the list and delete requests carry 8 bytes and a word (kr-812 sub_6DAF50), the write
/// request 8 bytes and a string (kr-812 sub_6DAF20), the list reply a word and a counted vector of the
/// client's <c>GuildLineBoardRecord</c> elements (kr-812 sub_83D960 and kr-812 sub_83D920), the write and
/// delete replies one word (kr-812 sub_80A910), and the guild-info request no field at all
/// (kr-812 sub_5C8FC0). A body that changes on purpose updates these pins together with the client reader.
/// </summary>
public sealed class GuildBoardTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The list and delete request: 8 bytes and a word, twelve bytes.</summary>
    [Fact]
    public void GuildLineBoardRequestIsEightBytesAndAWord()
    {
        var body = Write("GuildLineBoardRequest", new Dictionary<string, object?>
        {
            ["unknown_8"] = 0x0102030405060708,
            ["unknown_16"] = 0x0A0B0C0D,
        });

        Assert.Equal(12, body.Length);
        Assert.Equal("08070605040302010D0C0B0A", Convert.ToHexString(body));
    }

    /// <summary>The empty write request: 8 bytes and an empty string, twelve bytes.</summary>
    [Fact]
    public void GuildLineBoardWriteRequestEmptyIsTwelveBytes()
    {
        var body = Write("GuildLineBoardWriteRequest", new Dictionary<string, object?>
        {
            ["unknown_8"] = 0x0102030405060708,
            ["unknown_16"] = string.Empty,
        });

        Assert.Equal(12, body.Length);
        Assert.Equal("080706050403020100000000", Convert.ToHexString(body));
    }

    /// <summary>The named write request: the string is a 4-byte count and UTF-16 units, sixteen bytes.</summary>
    [Fact]
    public void GuildLineBoardWriteRequestCarriesTheText()
    {
        var body = Write("GuildLineBoardWriteRequest", new Dictionary<string, object?>
        {
            ["unknown_8"] = 0x0102030405060708,
            ["unknown_16"] = "ab",
        });

        Assert.Equal(16, body.Length);
        Assert.Equal("08070605040302010200000061006200", Convert.ToHexString(body));
    }

    /// <summary>The empty list reply: a word and an empty vector, eight bytes.</summary>
    [Fact]
    public void GuildLineBoardReplyEmptyIsEightBytes()
    {
        var body = Write("GuildLineBoardReply", new Dictionary<string, object?>
        {
            ["unknown_4"] = 5,
            ["records"] = Array.Empty<object?>(),
        });

        Assert.Equal(8, body.Length);
        Assert.Equal("0500000000000000", Convert.ToHexString(body));
    }

    /// <summary>One record of the client's own record class: word, two strings, 8 bytes, twenty bytes.</summary>
    [Fact]
    public void GuildLineBoardRecordIsTwentyBytesWhenEmpty()
    {
        var body = Write("GuildLineBoardRecord", new Dictionary<string, object?>
        {
            ["unknown_4"] = 7,
            ["unknown_8"] = string.Empty,
            ["unknown_36"] = string.Empty,
            ["unknown_64"] = 0x0102030405060708,
        });

        Assert.Equal(20, body.Length);
        Assert.Equal("0700000000000000000000000807060504030201", Convert.ToHexString(body));
    }

    /// <summary>The reply with one record keeps the count, the word and the record in that order.</summary>
    [Fact]
    public void GuildLineBoardReplyCarriesTheRecords()
    {
        var body = Write("GuildLineBoardReply", new Dictionary<string, object?>
        {
            ["unknown_4"] = 5,
            ["records"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["unknown_4"] = 7,
                    ["unknown_8"] = "a",
                    ["unknown_36"] = "b",
                    ["unknown_64"] = 0,
                },
            },
        });

        Assert.Equal(32, body.Length);
        Assert.Equal("0500000001000000070000000100000061000100000062000000000000000000",
            Convert.ToHexString(body));
    }

    /// <summary>The write and delete reply: one word, four bytes.</summary>
    [Fact]
    public void GuildLineBoardResultReplyIsOneWord()
    {
        var body = Write("GuildLineBoardResultReply", new Dictionary<string, object?> { ["unknown_4"] = 1 });

        Assert.Equal(4, body.Length);
        Assert.Equal("01000000", Convert.ToHexString(body));
    }

    /// <summary>The guild-info request carries no field, and the structures' field lists are the pinned ones.</summary>
    [Fact]
    public void GuildInfoRequestIsEmptyAndTheFieldListsArePinned()
    {
        Assert.Empty(Write("GuildInfoRequest", new Dictionary<string, object?>()));

        Assert.Equal(["unknown_8", "unknown_16"],
            Tables.Resolve("kr-812", "GuildLineBoardRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_8", "unknown_16"],
            Tables.Resolve("kr-812", "GuildLineBoardWriteRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4", "records"],
            Tables.Resolve("kr-812", "GuildLineBoardReply").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4", "unknown_8", "unknown_36", "unknown_64"],
            Tables.Resolve("kr-812", "GuildLineBoardRecord").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4"],
            Tables.Resolve("kr-812", "GuildLineBoardResultReply").Select(field => field.Name).ToArray());
        Assert.Empty(Tables.Resolve("kr-812", "GuildInfoRequest"));
    }

    private static byte[] Write(string structure, IReadOnlyDictionary<string, object?> values) =>
        PacketCodec.Write(Tables, "kr-812", structure, values);
}
