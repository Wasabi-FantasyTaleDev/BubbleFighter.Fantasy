using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan search bodies through <c>packets/clan-server.yaml</c>, byte for byte: the request (writer
/// kr-1256 sub_526020), the page header and its rows (reader kr-1256 sub_5290A0), and their opcode numbers.
/// </summary>
public sealed class ClanSearchTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    private static readonly DateTime Founded = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastLogin = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The request is the mode, the page and the text, in that order.</summary>
    [Fact]
    public void RequestReadsTheModeThePageAndTheText()
    {
        Assert.Equal(new ClanSearchRequested(4, 2, "Wolf"),
            ClanDirectoryBinding.ReadSearch(Tables, "kr-1256", Convert.FromHexString(U32(4) + U32(2) + Wc("Wolf"))));
        Assert.Throws<PacketFormatException>(() => ClanDirectoryBinding.ReadSearch(Tables, "kr-1256", Convert.FromHexString(U32(4) + U32(2))));
    }

    /// <summary>
    /// The header is the row count, the echoed mode, the page and the echoed text; each row then carries the
    /// fields in the order kr-1256 sub_5290A0 reads them: id, name, master id, master name, level, points, founding
    /// date, an unread date (0), recruitment, member count, a skipped word (0), introduction, disband date
    /// (2100-01-01 while none is pending), last login date and mark.
    /// </summary>
    [Fact]
    public void PageIsTheHeaderThenEachRowInReaderOrder()
    {
        var clan = new ClanRecord(1000, "Wolves", 42, "hi", "notice", 3, 250, 7, 30, 2, Founded, null, 4);
        var body = ClanDirectoryBinding.WriteSearchResult(Tables, "kr-1256",
            new ClanSearchSent(6, 1, "", [new ClanSearchRow(clan, "Alpha", LastLogin)]));
        Assert.Equal(
            U32(1) + U32(6) + U32(1) + Wc("")
            + U32(1000) + Wc("Wolves") + U32(42) + Wc("Alpha") + U32(3) + U32(250) + F64(Founded) + new string('0', 16)
            + U32(2) + U32(4) + U32(0) + Wc("hi") + "00000000B0D5F140" + F64(LastLogin) + U32(7),
            Convert.ToHexString(body));
    }

    /// <summary>A pending disband is written as its date, which the client compares with 2100-01-01.</summary>
    [Fact]
    public void PendingDisbandIsItsDate()
    {
        var disband = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);
        var clan = new ClanRecord(1000, "W", 42, "", "", 1, 0, 0, 30, 1, Founded, disband, 1);
        var body = Convert.ToHexString(ClanDirectoryBinding.WriteSearchResult(Tables, "kr-1256",
            new ClanSearchSent(6, 1, "", [new ClanSearchRow(clan, "A", LastLogin)])));
        Assert.Contains(Wc("") + F64(disband) + F64(LastLogin) + U32(0), body);
    }

    /// <summary>An empty page is the header alone, with the text echoed.</summary>
    [Fact]
    public void EmptyPageIsTheHeaderAlone() =>
        Assert.Equal(U32(0) + U32(4) + U32(3) + Wc("Cat"),
            Convert.ToHexString(ClanDirectoryBinding.WriteSearchResult(Tables, "kr-1256", new ClanSearchSent(4, 3, "Cat", []))));

    /// <summary>The numbers are the client's own: the stamp of kr-1256 sub_526020 and case 0x3FC of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1019, (int)clan.C2S("ClanSearch"));
        Assert.Equal(1020, (int)clan.S2C("ClanSearchResult"));
    }

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string F64(DateTime utc)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, (utc - new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc)).TotalDays);
        return Convert.ToHexString(bytes);
    }

    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
