using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection member change bodies through <c>packets/clan-server.yaml</c>, byte for byte: the change
/// request (writer kr-1256 sub_527B80), its result (reader kr-1256 sub_52D090), the change notice (reader kr-1256
/// sub_52CE70), the leave notice (reader kr-1256 sub_52E640), plus their five opcode numbers.
/// </summary>
public sealed class ClanMemberTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The change request is the row's id, name, listed role and the new word, signed.</summary>
    [Fact]
    public void ChangeRequestReadsTheRowAndTheNewWord()
    {
        Assert.Equal(new ClanMemberChangeRequested(43, "Aide", 1, -10),
            ClanDirectoryBinding.ReadMemberChange(Tables, "kr-1256", Convert.FromHexString(U32(43) + Wc("Aide") + U32(1) + U32(-10))));
        Assert.Throws<PacketFormatException>(() =>
            ClanDirectoryBinding.ReadMemberChange(Tables, "kr-1256", Convert.FromHexString(U32(43) + Wc("Aide") + U32(1))));
    }

    /// <summary>The result is a word, the name and two words: 4 + 10 + 4 + 4 = 22 bytes.</summary>
    [Fact]
    public void ChangeResultIsResultNameRoleAndJoined()
    {
        var body = ClanDirectoryBinding.WriteMemberChangeResult(Tables, "kr-1256", new ClanMemberChangeAnswered(1, "Aide", ClanRole.Member, 1));
        Assert.Equal(U32(1) + Wc("Aide") + U32(100) + U32(1), Convert.ToHexString(body));
        Assert.Equal(22, body.Length);
    }

    /// <summary>
    /// The change notice and the leave notice share a layout, but the leave notice's event word is its last field
    /// (its reader swaps the last two words), so a leave writes 0 then -20.
    /// </summary>
    [Fact]
    public void NoticesPutTheirWordsWhereTheirReadersTakeThem()
    {
        Assert.Equal(Wc("Wolves") + U32(43) + Wc("Aide") + U32(ClanMemberEvent.Removed) + U32(0),
            Convert.ToHexString(ClanDirectoryBinding.WriteMemberChanged(Tables, "kr-1256", new ClanMemberChanged("Wolves", 43, "Aide", ClanMemberEvent.Removed, 0))));
        Assert.Equal(Wc("Wolves") + U32(43) + Wc("Aide") + U32(0) + U32(-20),
            Convert.ToHexString(ClanDirectoryBinding.WriteMemberLeft(Tables, "kr-1256", new ClanMemberLeft("Wolves", 43, "Aide", 0, ClanMemberEvent.Left))));
    }

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1036, (int)clan.C2S("ClanLeave"));
        Assert.Equal(1037, (int)clan.S2C("ClanMemberLeft"));
        Assert.Equal(1039, (int)clan.C2S("ClanMemberChange"));
        Assert.Equal(1040, (int)clan.S2C("ClanMemberChangeResult"));
        Assert.Equal(1041, (int)clan.S2C("ClanMemberChanged"));
    }

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
