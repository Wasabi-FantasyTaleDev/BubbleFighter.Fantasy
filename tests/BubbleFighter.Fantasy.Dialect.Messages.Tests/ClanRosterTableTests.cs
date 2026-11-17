using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection roster bodies through <c>packets/clan-server.yaml</c>, byte for byte: the member list
/// (reader kr-1256 sub_52B4B0), the detailed member list (reader kr-1256 sub_52C010), their requests and the
/// member report (writer kr-1256 sub_527AB0), plus their five opcode numbers.
/// </summary>
public sealed class ClanRosterTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>
    /// A roster row is the id, the name and three words; the first two are the member's report in the order the
    /// client stores them (+36 is the report's second word, +28 its first), the third is the role.
    /// </summary>
    [Fact]
    public void RosterRowEchoesTheReportInRecordOrderThenTheRole()
    {
        var body = ClanDirectoryBinding.WriteRoster(Tables, "kr-1256", new ClanRosterSent([
            new ClanRosterRow(42, "Wolf", new ClanMemberReported(5, 3), ClanRole.Master),
            new ClanRosterRow(43, "Aide", null, ClanRole.Member),
        ]));
        Assert.Equal(U32(2) + U32(42) + Wc("Wolf") + U32(3) + U32(5) + U32(101) + U32(43) + Wc("Aide") + U32(0) + U32(0) + U32(100),
            Convert.ToHexString(body));
    }

    /// <summary>An empty roster is the zero count alone.</summary>
    [Fact]
    public void EmptyRosterIsTheZeroCount() =>
        Assert.Equal(U32(0), Convert.ToHexString(ClanDirectoryBinding.WriteRoster(Tables, "kr-1256", new ClanRosterSent([]))));

    /// <summary>
    /// The detailed list leads with a word the reader skips, then the count at body offset 4 (kr-1256 sub_52C010);
    /// each row is the id, the name, the report's first word, the role and 48 zero bytes. 4 + 4 + 4 + 10 + 4 + 4 + 48 = 78 bytes.
    /// </summary>
    [Fact]
    public void DetailedRowCarriesTheReportedWordTheRoleAndZeroes()
    {
        var body = ClanDirectoryBinding.WriteMemberDetails(Tables, "kr-1256", new ClanMemberDetailsSent([
            new ClanRosterRow(42, "Wolf", new ClanMemberReported(5, 3), ClanRole.Deputy),
        ]));
        Assert.Equal(U32(0) + U32(1) + U32(42) + Wc("Wolf") + U32(5) + U32(102) + new string('0', 96), Convert.ToHexString(body));
        Assert.Equal(78, body.Length);
    }

    /// <summary>The detailed request is one word (always 1 from kr-1256 sub_527910); the report is two words.</summary>
    [Fact]
    public void RequestsReadTheirWords()
    {
        Assert.Equal(new ClanMemberDetailsRequested(1), ClanDirectoryBinding.ReadMemberDetailsRequest(Tables, "kr-1256", Convert.FromHexString(U32(1))));
        Assert.Equal(new ClanMemberReported(7, -3), ClanDirectoryBinding.ReadMemberReport(Tables, "kr-1256", Convert.FromHexString(U32(7) + U32(-3))));
        Assert.Throws<PacketFormatException>(() => ClanDirectoryBinding.ReadMemberReport(Tables, "kr-1256", Convert.FromHexString(U32(7))));
    }

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1011, (int)clan.C2S("ClanRosterRequest"));
        Assert.Equal(1012, (int)clan.S2C("ClanRoster"));
        Assert.Equal(1013, (int)clan.C2S("ClanMemberDetailsRequest"));
        Assert.Equal(1014, (int)clan.S2C("ClanMemberDetails"));
        Assert.Equal(1045, (int)clan.C2S("ClanMemberReport"));
    }

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
