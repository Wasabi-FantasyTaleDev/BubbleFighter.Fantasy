using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection edit bodies through <c>packets/clan-server.yaml</c>, byte for byte: the introduction and
/// notice edits (writers kr-1256 sub_527D30 / sub_527CA0) and their four replies, the recruitment and mark edits
/// (writers kr-1256 sub_528460 / sub_530C50) and their replies, plus their ten opcode numbers.
/// </summary>
public sealed class ClanEditTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The text edits are one string; the recruitment and mark edits are the clan id and a word.</summary>
    [Fact]
    public void EditsReadTheirFields()
    {
        Assert.Equal("We hunt", ClanDirectoryBinding.ReadText(Tables, "kr-1256", "ClanServerIntroductionEdit", "introduction", Convert.FromHexString(Wc("We hunt"))));
        Assert.Equal("Meet", ClanDirectoryBinding.ReadText(Tables, "kr-1256", "ClanServerNoticeEdit", "notice", Convert.FromHexString(Wc("Meet"))));
        Assert.Equal((1000, 2), ClanDirectoryBinding.ReadPair(Tables, "kr-1256", "ClanServerRecruitmentEdit", "recruitment", Convert.FromHexString(U32(1000) + U32(2))));
        Assert.Equal((1000, 5), ClanDirectoryBinding.ReadPair(Tables, "kr-1256", "ClanServerMarkEdit", "mark", Convert.FromHexString(U32(1000) + U32(5))));
        Assert.Throws<PacketFormatException>(() =>
            ClanDirectoryBinding.ReadPair(Tables, "kr-1256", "ClanServerMarkEdit", "mark", Convert.FromHexString(U32(1000))));
    }

    /// <summary>
    /// The introduction result is a word and the text; its notice leads with a skipped word. The notice result is
    /// one word and its notice the text alone. The recruitment result is three words, the mark notice one.
    /// </summary>
    [Fact]
    public void RepliesPutTheirWordsWhereTheirReadersTakeThem()
    {
        Assert.Equal(U32(1) + Wc("Hi"), Hex("ClanServerIntroductionEditResult", ("result", 1), ("introduction", "Hi")));
        Assert.Equal(U32(0) + Wc("Hi"), Hex("ClanServerIntroductionChanged", ("unknown_0", 0), ("introduction", "Hi")));
        Assert.Equal(U32(-1), Hex("ClanServerNoticeEditResult", ("result", -1)));
        Assert.Equal(Wc("Hi"), Hex("ClanServerNoticeChanged", ("notice", "Hi")));
        Assert.Equal(U32(1000) + U32(1) + U32(0), Hex("ClanServerRecruitmentEditResult", ("unknown_0", 1000), ("result", 1), ("recruitment", 0)));
        Assert.Equal(U32(5), Hex("ClanServerMarkChanged", ("mark", 5)));
    }

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1048, (int)clan.C2S("ClanIntroductionEdit"));
        Assert.Equal(1049, (int)clan.S2C("ClanIntroductionEditResult"));
        Assert.Equal(1050, (int)clan.S2C("ClanIntroductionChanged"));
        Assert.Equal(1051, (int)clan.C2S("ClanNoticeEdit"));
        Assert.Equal(1052, (int)clan.S2C("ClanNoticeEditResult"));
        Assert.Equal(1053, (int)clan.S2C("ClanNoticeChanged"));
        Assert.Equal(1096, (int)clan.C2S("ClanMarkEdit"));
        Assert.Equal(1097, (int)clan.S2C("ClanMarkChanged"));
        Assert.Equal(1102, (int)clan.C2S("ClanRecruitmentEdit"));
        Assert.Equal(1103, (int)clan.S2C("ClanRecruitmentEditResult"));
    }

    private static string Hex(string structure, params (string Name, object? Value)[] fields) =>
        Convert.ToHexString(ClanDirectoryBinding.Write(Tables, "kr-1256", structure, fields));

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
