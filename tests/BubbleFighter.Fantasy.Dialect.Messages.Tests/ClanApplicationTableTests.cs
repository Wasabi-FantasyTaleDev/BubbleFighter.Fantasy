using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection application bodies through <c>packets/clan-server.yaml</c>, byte for byte: the
/// application and join requests (writers kr-1256 sub_527010 / sub_527130), their result (reader kr-1256
/// sub_52E2A0), the withdrawal and its result (writer kr-1256 sub_527250, kr-1256 sub_525170 case 0x428), the
/// applicant list (reader kr-1256 sub_52C750), plus their seven opcode numbers.
/// </summary>
public sealed class ClanApplicationTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The application is the clan id and name; the join request adds the sender's nickname.</summary>
    [Fact]
    public void RequestsReadTheClanAndTheSender()
    {
        Assert.Equal(new ClanApplyRequested(1000, "Wolves"),
            ClanDirectoryBinding.ReadApply(Tables, "kr-1256", Convert.FromHexString(U32(1000) + Wc("Wolves"))));
        Assert.Equal(new ClanJoinRequested(1001, "Open", "clanner"),
            ClanDirectoryBinding.ReadJoin(Tables, "kr-1256", Convert.FromHexString(U32(1001) + Wc("Open") + Wc("clanner"))));
        Assert.Equal(new ClanCancelApplicationRequested(1000),
            ClanDirectoryBinding.ReadCancelApplication(Tables, "kr-1256", Convert.FromHexString(U32(1000))));
        Assert.Throws<PacketFormatException>(() => ClanDirectoryBinding.ReadApply(Tables, "kr-1256", Convert.FromHexString(U32(1000))));
    }

    /// <summary>The result is the result, the role the client stores as its own, the clan id and name: 4 + 4 + 4 + 14 = 26 bytes.</summary>
    [Fact]
    public void ApplyResultCarriesTheRoleTheClientStores()
    {
        var body = ClanDirectoryBinding.WriteApplyResult(Tables, "kr-1256",
            new ClanApplyAnswered(ClanApplyAnswered.Applied, ClanRole.Applicant, 1000, "Wolves"));
        Assert.Equal(U32(1) + U32(1) + U32(1000) + Wc("Wolves"), Convert.ToHexString(body));
        Assert.Equal(26, body.Length);
        Assert.Equal(U32(-5) + U32(0) + U32(0) + "0000",
            Convert.ToHexString(ClanDirectoryBinding.WriteApplyResult(Tables, "kr-1256", new ClanApplyAnswered(ClanApplyAnswered.NotRecruiting, 0, 0, ""))));
    }

    /// <summary>The withdrawal result is two words, the second never read and echoing the clan id.</summary>
    [Fact]
    public void CancelResultIsTwoWords() =>
        Assert.Equal(U32(1) + U32(1000), Convert.ToHexString(ClanDirectoryBinding.WriteCancelApplicationResult(Tables, "kr-1256",
            new ClanCancelApplicationAnswered(ClanCancelApplicationAnswered.Cancelled, 1000))));

    /// <summary>
    /// An applicant row is the id, the name, a zero word, the role, a zero eight-byte value and four zero words:
    /// after the count, 4 + 10 + 4 + 4 + 8 + 16 = 46 bytes per row.
    /// </summary>
    [Fact]
    public void ApplicantRowCarriesTheRoleAndZeroes()
    {
        var body = ClanDirectoryBinding.WriteApplicants(Tables, "kr-1256", new ClanApplicantsSent([new ClanApplicantRow(43, "Aide", ClanRole.Applicant)]));
        Assert.Equal(U32(1) + U32(43) + Wc("Aide") + U32(0) + U32(1) + new string('0', 16) + new string('0', 32), Convert.ToHexString(body));
        Assert.Equal(4 + 46, body.Length);
        Assert.Equal(U32(0), Convert.ToHexString(ClanDirectoryBinding.WriteApplicants(Tables, "kr-1256", new ClanApplicantsSent([]))));
    }

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1032, (int)clan.C2S("ClanApply"));
        Assert.Equal(1033, (int)clan.C2S("ClanJoin"));
        Assert.Equal(1034, (int)clan.S2C("ClanApplyResult"));
        Assert.Equal(1063, (int)clan.C2S("ClanCancelApplication"));
        Assert.Equal(1064, (int)clan.S2C("ClanCancelApplicationResult"));
        Assert.Equal(1069, (int)clan.C2S("ClanApplicantsRequest"));
        Assert.Equal(1070, (int)clan.S2C("ClanApplicants"));
    }

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
