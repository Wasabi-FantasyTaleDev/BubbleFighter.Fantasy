using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Rejects packet values that could consume unbounded memory before serialization completes.</summary>
public sealed class PacketCodecBoundsTests
{
    [Fact]
    public void ScalarRequestsRejectOversizedStringsBodiesAndTrailingData()
    {
        var tables = TestBuilds.Packets;
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(tables, "cn-279", "CharacterNameRequest", Convert.FromHexString("FFFFFFFF")));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(tables, "cn-279", "CharacterNameRequest", Convert.FromHexString("0000000001")));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(tables, "cn-279", "CharacterNameRequest", new byte[65536]));
        Assert.Throws<ConfigException>(() =>
            PacketCodec.ReadScalars(tables, "kr-812", "InventoryUpdate", new byte[4]));
    }

    [Fact]
    public void AnEndlessCollectionIsStoppedAtTheNativePacketBound()
    {
        var enumerated = 0;
        IEnumerable<object> Endless()
        {
            while (true)
            {
                enumerated++;
                yield return new Dictionary<string, object?>();
            }
        }
        var values = new Dictionary<string, object?> { ["slots"] = Endless() };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PacketCodec.Write(TestBuilds.Packets, "kr-812", "InventoryUpdate", values));
        Assert.Equal(ushort.MaxValue + 1, enumerated);
    }

    [Fact]
    public void AStringLargerThanTheNativeFrameIsRejected()
    {
        var values = new Dictionary<string, object?> { ["unknown_36"] = new string('a', ushort.MaxValue) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PacketCodec.Write(TestBuilds.Packets, "kr-812", "UserStat", values));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ABodyBudgetOutsideTheNativeFrameIsRejected(int budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PacketCodec.Write(TestBuilds.Packets, "kr-812", "UserStat", new Dictionary<string, object?>(), budget));
    }
}
