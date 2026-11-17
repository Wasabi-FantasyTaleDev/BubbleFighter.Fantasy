using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection bodies of the home, the profile and the create exchange through
/// <c>packets/clan-server.yaml</c>, byte for byte, and their six opcode numbers.
/// </summary>
public sealed class ClanDirectoryTableTests
{
    // 2100-01-01 as days since 1899-12-30 (73051.0): the date kr-1256 sub_51A3A0 compares the home's date with.
    private const string NoDisband = "00000000B0D5F140";

    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>A player without a clan: a zero byte, two zero words, an empty string and the far date. 1 + 4 + 4 + 2 + 8 = 19 bytes.</summary>
    [Fact]
    public void HomeWithoutAClanIsNineteenBytes()
    {
        var body = ClanDirectoryBinding.WriteHome(Tables, "kr-1256", new ClanHomeSent(false, 0, 0, "", null));
        Assert.Equal("00" + U32(0) + U32(0) + "0000" + NoDisband, Convert.ToHexString(body));
        Assert.Equal(19, body.Length);
    }

    /// <summary>A seated member's home in the reader's order (kr-1256 sub_526330): flag, clan id, role, name, disband date.</summary>
    [Fact]
    public void HomeOfAMemberStatesClanRoleNameAndDisbandDate()
    {
        // 2030-01-01 12:00 UTC is 47484.5 days after 1899-12-30.
        var disband = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var body = ClanDirectoryBinding.WriteHome(Tables, "kr-1256", new ClanHomeSent(true, 1000, ClanRole.Master, "Wolf", disband));
        Assert.Equal("01" + U32(1000) + U32(101) + Wc("Wolf") + F64(47484.5), Convert.ToHexString(body));
    }

    /// <summary>The sixteen profile fields in the order kr-1256 sub_5268A0 reads them.</summary>
    [Fact]
    public void ProfileWritesTheSixteenFieldsInReaderOrder()
    {
        var created = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        var clan = new ClanRecord(1000, "Wolf", 42, "intro", "notice", 3, 250, 7, 100, 1, created, null, 12);
        var body = ClanDirectoryBinding.WriteProfile(Tables, "kr-1256", new ClanProfileSent(clan, "Boss", 43, "Aide"));
        Assert.Equal(
            Wc("Wolf") + U32(42) + Wc("Boss") + U32(43) + Wc("Aide") + U32(3) + U32(250)
            + F64(ClanDirectoryBinding.ToOleDate(created)) + NoDisband
            + U32(1) + U32(12) + U32(0) + Wc("notice") + Wc("intro") + U32(7) + U32(100),
            Convert.ToHexString(body));
        // 2026-11-15 is 46341 days after 1899-12-30.
        Assert.Equal(46341.0, ClanDirectoryBinding.ToOleDate(created));
    }

    /// <summary>The create request as kr-1256 sub_5275B0 writes it: name, word, introduction, two words.</summary>
    [Fact]
    public void CreateReadsNameWordIntroductionAndTwoWords()
    {
        var body = Convert.FromHexString(Wc("Wolf") + U32(5) + Wc("hi") + U32(100819) + U32(-1));
        Assert.Equal(new ClanCreateRequested("Wolf", 5, "hi", 100819, -1), ClanDirectoryBinding.ReadCreate(Tables, "kr-1256", body));
    }

    /// <summary>A create request cut inside its last word is rejected, not padded.</summary>
    [Fact]
    public void TruncatedCreateIsRejected()
    {
        var body = Convert.FromHexString(Wc("Wolf") + U32(5) + Wc("hi") + U32(100819) + "0000");
        Assert.Throws<PacketFormatException>(() => ClanDirectoryBinding.ReadCreate(Tables, "kr-1256", body));
    }

    /// <summary>The create result is three words; a refusal code is written as two's complement (kr-1256 sub_517FE0 switches on a signed value).</summary>
    [Theory]
    [InlineData(1, 100819, 30, "01000000D3890100" + "1E000000")]
    [InlineData(-100, 0, 0, "9CFFFFFF0000000000000000")]
    public void CreateResultIsThreeWords(int result, int item, int third, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(ClanDirectoryBinding.WriteCreateResult(Tables, "kr-1256", new ClanCreateAnswered(result, item, third))));

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1015, (int)clan.C2S("ClanHomeRequest"));
        Assert.Equal(1016, (int)clan.S2C("ClanHome"));
        Assert.Equal(1017, (int)clan.C2S("ClanProfileRequest"));
        Assert.Equal(1018, (int)clan.S2C("ClanProfile"));
        Assert.Equal(1027, (int)clan.C2S("ClanCreate"));
        Assert.Equal(1028, (int)clan.S2C("ClanCreateResult"));
    }

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string F64(double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    /// <summary>A string as this connection carries it: UTF-16LE up to a NUL word, no count.</summary>
    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
