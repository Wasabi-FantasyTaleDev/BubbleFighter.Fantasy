using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the refund exchange from <c>packets/refund.yaml</c>. The request is a wstring and one word in both
/// Korean builds: kr-812 sub_4EC0B0 reads them at object +4 and +32, and kr-1256 sub_D3E0B0 writes them before
/// stamping C2S 817. The kr-812 reply has two words (kr-812 sub_6DB1B0); the kr-1256 table adds a string
/// inferred from the message object in kr-1256 sub_9B8700. These pins check that table assumption, not a
/// verified string read or native-client acceptance.
/// </summary>
public sealed class RefundTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>
    /// An empty name and the word 0x01020304: the request is the four count bytes and that one word in both
    /// builds, so its empty form is eight bytes.
    /// </summary>
    /// <param name="build">The build whose request layout is written.</param>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void RefundRequestIsOneEmptyStringAndOneWord(string build)
    {
        var body = PacketCodec.Write(Tables, build, "RefundRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = string.Empty,
            ["unknown_32"] = 0x01020304,
        });
        Assert.Equal(8, body.Length);
        Assert.Equal("0000000004030201", Convert.ToHexString(body));
    }

    /// <summary>A named request: "ab" is the four-byte count 2 and two UTF-16 units, so the body is twelve bytes.</summary>
    /// <param name="build">The build whose request layout is written.</param>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void RefundRequestCarriesTheStringBeforeTheWord(string build)
    {
        var body = PacketCodec.Write(Tables, build, "RefundRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = "ab",
            ["unknown_32"] = 0x01020304,
        });
        Assert.Equal(12, body.Length);
        Assert.Equal("020000006100620004030201", Convert.ToHexString(body));
    }

    /// <summary>kr-812's reply is the cash record key and the result word, eight bytes.</summary>
    [Fact]
    public void Kr812ReplyIsTwoWords()
    {
        var body = PacketCodec.Write(Tables, "kr-812", "RefundReply", new Dictionary<string, object?>
        {
            ["cash_slot_id"] = 0x01020304,
            ["result"] = 1,
        });
        Assert.Equal(8, body.Length);
        Assert.Equal("0403020101000000", Convert.ToHexString(body));
    }

    /// <summary>The inferred kr-1256 reply table adds an empty trailing string, making its body twelve bytes.</summary>
    [Fact]
    public void Kr1256ReplyIsTwoWordsAndOneEmptyString()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "RefundReply", new Dictionary<string, object?>
        {
            ["cash_slot_id"] = 0x01020304,
            ["result"] = 1,
            ["unknown_8"] = string.Empty,
        });
        Assert.Equal(12, body.Length);
        Assert.Equal("040302010100000000000000", Convert.ToHexString(body));
    }

    /// <summary>Pins counted UTF-16 text after two words in the inferred kr-1256 table, without proving its reader.</summary>
    [Fact]
    public void Kr1256ReplyCarriesTheStringAfterTheWords()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "RefundReply", new Dictionary<string, object?>
        {
            ["cash_slot_id"] = 0x01020304,
            ["result"] = 1,
            ["unknown_8"] = "ab",
        });
        Assert.Equal(16, body.Length);
        Assert.Equal("04030201010000000200000061006200", Convert.ToHexString(body));
    }

    /// <summary>
    /// The two layouts' field lists: the request has the same two fields everywhere, and the reply gains the
    /// inferred string for kr-1256; the two builds based on kr-1256 inherit that assumption.
    /// </summary>
    [Fact]
    public void TheReplyStringBelongsToTheKr1256Family()
    {
        Assert.Equal(["unknown_4", "unknown_32"], Tables.Resolve("kr-812", "RefundRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4", "unknown_32"], Tables.Resolve("kr-1256", "RefundRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["cash_slot_id", "result"], Tables.Resolve("kr-812", "RefundReply").Select(field => field.Name).ToArray());
        Assert.Equal(["cash_slot_id", "result", "unknown_8"], Tables.Resolve("kr-1256", "RefundReply").Select(field => field.Name).ToArray());
        Assert.Equal(["cash_slot_id", "result", "unknown_8"], Tables.Resolve("cn-774", "RefundReply").Select(field => field.Name).ToArray());
        Assert.Equal(["cash_slot_id", "result", "unknown_8"], Tables.Resolve("tw-454", "RefundReply").Select(field => field.Name).ToArray());
    }
}
