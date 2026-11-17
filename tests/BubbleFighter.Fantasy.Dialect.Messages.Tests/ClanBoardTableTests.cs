using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection board bodies through <c>packets/clan-server.yaml</c>, byte for byte: the page (reader
/// kr-1256 sub_5285B0), the write result (reader kr-1256 sub_528BD0), the empty deletion answer, the three requests
/// (writers kr-1256 sub_527360, sub_527430, sub_5274E0), plus their six opcode numbers.
/// </summary>
public sealed class ClanBoardTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>
    /// A page is the count, the newest and the oldest id, then per post a zero word, the author, a zero word, the id,
    /// the 16-bit shown flag, the date and the body: 4 + 10 + 4 + 4 + 2 + 8 + 6 = 38 bytes for this post.
    /// </summary>
    [Fact]
    public void BoardPageCarriesTheCursorsAndEachPost()
    {
        var at = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var body = ClanDirectoryBinding.WriteBoard(Tables, "kr-1256", new ClanBoardSent(9, 3, [new ClanPostRecord(9, 1000, 42, "Wolf", 1, "Hi", at)]));
        Assert.Equal(U32(1) + U32(9) + U32(3) + U32(0) + Wc("Wolf") + U32(0) + U32(9) + "0100"
            + Convert.ToHexString(BitConverter.GetBytes(ClanDirectoryBinding.ToOleDate(at))) + Wc("Hi"), Convert.ToHexString(body));
        Assert.Equal(12 + 38, body.Length);
        Assert.Equal(U32(0) + U32(0) + U32(0), Convert.ToHexString(ClanDirectoryBinding.WriteBoard(Tables, "kr-1256", new ClanBoardSent(0, 0, []))));
    }

    /// <summary>The requests read their words and the post; the write result is one word and the deletion answer has no body.</summary>
    [Fact]
    public void RequestsAndRepliesReadAndWriteTheirFields()
    {
        var request = PacketCodec.Read(Tables, "kr-1256", "ClanServerBoardRequest", Convert.FromHexString(U32(2) + U32(9)));
        Assert.Equal((2L, 9L), (Convert.ToInt64(request["mode"]), Convert.ToInt64(request["cursor"])));
        var write = PacketCodec.Read(Tables, "kr-1256", "ClanServerBoardWrite", Convert.FromHexString(U32(5) + Wc("Hi")));
        Assert.Equal("Hi", write["body"]);
        var delete = PacketCodec.Read(Tables, "kr-1256", "ClanServerBoardDelete", Convert.FromHexString(U32(42) + U32(9)));
        Assert.Equal(9L, Convert.ToInt64(delete["post_id"]));
        Assert.Equal(U32(-2), Convert.ToHexString(ClanDirectoryBinding.Write(Tables, "kr-1256", "ClanServerBoardWriteResult", ("result", -2))));
        Assert.Empty(PacketCodec.Write(Tables, "kr-1256", "ClanServerBoardDeleted", new Dictionary<string, object?>()));
    }

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1021, (int)clan.C2S("ClanBoardRequest"));
        Assert.Equal(1022, (int)clan.S2C("ClanBoard"));
        Assert.Equal(1023, (int)clan.C2S("ClanBoardWrite"));
        Assert.Equal(1024, (int)clan.S2C("ClanBoardWriteResult"));
        Assert.Equal(1025, (int)clan.C2S("ClanBoardDelete"));
        Assert.Equal(1026, (int)clan.S2C("ClanBoardDeleted"));
    }

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
