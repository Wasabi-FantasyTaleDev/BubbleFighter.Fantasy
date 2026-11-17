using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection chat and whisper bodies through <c>packets/clan-server.yaml</c>, byte for byte (writers
/// kr-1256 sub_5282A0 / sub_526160, readers kr-1256 sub_52EFC0 / sub_529D30 / sub_52A8A0), plus their six opcode
/// numbers.
/// </summary>
public sealed class ClanChatTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The requests read their fields in the writers' order; the whisper's flag is one byte.</summary>
    [Fact]
    public void RequestsReadTheirFields()
    {
        var chat = PacketCodec.Read(Tables, "kr-1256", "ClanServerChat", Convert.FromHexString(U32(2) + Wc("Me") + Wc("hi")));
        Assert.Equal(("Me", "hi"), ((string)chat["nickname"]!, (string)chat["text"]!));
        var whisper = PacketCodec.Read(Tables, "kr-1256", "ClanServerWhisper", Convert.FromHexString(U32(1) + Wc("You") + Wc("psst") + "01"));
        Assert.Equal(("You", "psst", 1L), ((string)whisper["target"]!, (string)whisper["text"]!, Convert.ToInt64(whisper["unknown_3"])));
    }

    /// <summary>The replies in their readers' order.</summary>
    [Fact]
    public void RepliesPutTheirFieldsWhereTheirReadersTakeThem()
    {
        Assert.Equal(U32(0) + Wc("Me") + Wc("hi"), Hex("ClanServerChatMessage", ("nickname", "Me"), ("text", "hi")));
        Assert.Equal(U32(2) + U32(1) + Wc("Me") + Wc("You") + Wc("psst"),
            Hex("ClanServerWhisperResult", ("result", 2), ("kind", 1), ("from", "Me"), ("to", "You"), ("text", "psst")));
        Assert.Equal(U32(1) + Wc("Me") + Wc("You") + Wc("psst") + "00",
            Hex("ClanServerWhisperMessage", ("kind", 1), ("from", "Me"), ("to", "You"), ("text", "psst"), ("unknown_4", 0)));
        Assert.Equal(U32(3), Hex("ClanServerChatResult", ("result", 3)));
    }

    /// <summary>The numbers are the client's own: the stamps of its writers and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1042, (int)clan.C2S("ClanWhisper"));
        Assert.Equal(1043, (int)clan.S2C("ClanWhisperResult"));
        Assert.Equal(1044, (int)clan.S2C("ClanWhisperMessage"));
        Assert.Equal(1066, (int)clan.C2S("ClanChat"));
        Assert.Equal(1067, (int)clan.S2C("ClanChatResult"));
        Assert.Equal(1068, (int)clan.S2C("ClanChatMessage"));
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
