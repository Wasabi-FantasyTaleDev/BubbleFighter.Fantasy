using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-connection disband bodies through <c>packets/clan-server.yaml</c>, byte for byte: the request
/// (writer kr-1256 sub_528190), its result (kr-1256 sub_525170 case 0x406) and the notice (reader kr-1256
/// sub_52E860), plus their three opcode numbers.
/// </summary>
public sealed class ClanDisbandTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The request is one word; the result and the notice are two words each.</summary>
    [Fact]
    public void BodiesAreWords()
    {
        Assert.Equal(2L, Convert.ToInt64(PacketCodec.Read(Tables, "kr-1256", "ClanServerDisband", Convert.FromHexString(U32(2)))["kind"]));
        Assert.Equal(U32(1) + U32(2), Convert.ToHexString(ClanDirectoryBinding.Write(Tables, "kr-1256", "ClanServerDisbandResult", ("result", 1), ("kind", 2))));
        Assert.Equal(U32(1) + U32(1), Convert.ToHexString(ClanDirectoryBinding.Write(Tables, "kr-1256", "ClanServerDisbandChanged", ("unknown_0", 1), ("kind", 1))));
    }

    /// <summary>The numbers are the client's own: the stamp of its writer and the cases of kr-1256 sub_525170.</summary>
    [Fact]
    public void OpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(1029, (int)clan.C2S("ClanDisband"));
        Assert.Equal(1030, (int)clan.S2C("ClanDisbandResult"));
        Assert.Equal(1031, (int)clan.S2C("ClanDisbandChanged"));
    }

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }
}
