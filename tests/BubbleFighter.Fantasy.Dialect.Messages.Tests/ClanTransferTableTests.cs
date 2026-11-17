using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection master transfer bodies through <c>packets/clan-server.yaml</c>, byte for byte: the
/// request, answer and confirmation (writers kr-1256 sub_527F60, sub_52F620, sub_528070) and the five replies
/// (readers kr-1256 sub_52F320, sub_52F620, sub_5300C0, sub_52FB70, sub_52E430), plus their eight opcode numbers.
/// </summary>
public sealed class ClanTransferTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The request is one word, the answer a byte and the echoed word, the confirmation a word and a name.</summary>
    [Fact]
    public void RequestsReadTheirFields()
    {
        Assert.Equal(new ClanTransferRequested(43), ClanDirectoryBinding.ReadTransferRequest(Tables, "kr-1256", Convert.FromHexString(U32(43))));
        Assert.Equal(new ClanTransferAnswerRequested(true, 42), ClanDirectoryBinding.ReadTransferAnswer(Tables, "kr-1256", Convert.FromHexString("01" + U32(42))));
        Assert.Equal(new ClanTransferAnswerRequested(false, 42), ClanDirectoryBinding.ReadTransferAnswer(Tables, "kr-1256", Convert.FromHexString("00" + U32(42))));
        Assert.Equal(new ClanTransferConfirmed(43, "Aide"), ClanDirectoryBinding.ReadTransferConfirm(Tables, "kr-1256", Convert.FromHexString(U32(43) + Wc("Aide"))));
        Assert.Throws<PacketFormatException>(() => ClanDirectoryBinding.ReadTransferAnswer(Tables, "kr-1256", Convert.FromHexString(U32(42))));
    }

    /// <summary>Each reply in its reader's order: byte flags are one byte, and the master change leads with a skipped word.</summary>
    [Fact]
    public void RepliesPutTheirFieldsWhereTheirReadersTakeThem()
    {
        Assert.Equal("01" + U32(1), Hex("ClanServerTransferRequestResult", ("sent", 1), ("result", 1)));
        Assert.Equal(Wc("Boss") + U32(42), Hex("ClanServerTransferOffer", ("nickname", "Boss"), ("unknown_1", 42)));
        Assert.Equal("00", Hex("ClanServerTransferAnswerResult", ("result", 0)));
        Assert.Equal("01" + U32(43) + Wc("Aide"), Hex("ClanServerTransferAnswered", ("accepted", 1), ("user_number", 43), ("nickname", "Aide")));
        Assert.Equal(U32(0) + U32(42) + Wc("Boss") + U32(43) + Wc("Aide"), Hex("ClanServerMasterChanged",
            ("unknown_0", 0), ("old_master_id", 42), ("old_master_name", "Boss"), ("new_master_id", 43), ("new_master_name", "Aide")));
    }

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1054, (int)clan.C2S("ClanTransferConfirm"));
        Assert.Equal(1056, (int)clan.S2C("ClanMasterChanged"));
        Assert.Equal(1057, (int)clan.C2S("ClanTransferRequest"));
        Assert.Equal(1058, (int)clan.S2C("ClanTransferRequestResult"));
        Assert.Equal(1059, (int)clan.S2C("ClanTransferOffer"));
        Assert.Equal(1060, (int)clan.C2S("ClanTransferAnswer"));
        Assert.Equal(1061, (int)clan.S2C("ClanTransferAnswerResult"));
        Assert.Equal(1062, (int)clan.S2C("ClanTransferAnswered"));
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
