using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the shared BnB seed in the native entry body without changing unaudited build fields.</summary>
public sealed class BnBMapSeedTests
{
    /// <summary>Empty entry strings leave the seed at byte 22, after the state, unused word and two bytes.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(422402801)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void SeedPreservesEveryBitInTheNativeEntry(int seed)
    {
        var expected = new byte[39];
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(12), 3);
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(22), seed);
        var body = RoundEntryWire.Write(TestBuilds.Packets, "kr-1256", 0, "", roomState: 3, mapSeed: seed);
        Assert.Equal(expected, body);
        Assert.Equal(seed, PacketCodec.Read(TestBuilds.Packets, "kr-1256", "RoomEnterGame", body)["map_seed"]);
    }

    /// <summary>The shared layout width remains unchanged where the corresponding field has no proven seed purpose.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void UnauditedBuildsRetainUnknownFieldAndRefuseSeededEntry(string build)
    {
        var expected = new byte[39];
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(12), 3);
        Assert.Equal(expected, RoundEntryWire.Write(TestBuilds.Packets, build, 0, "", roomState: 3));
        Assert.Contains(TestBuilds.Packets.Resolve(build, "RoomEnterGame"), field => field.Name == "unknown_88");
        Assert.Throws<ConfigException>(() => RoundEntryWire.Write(TestBuilds.Packets, build, 0, "", mapSeed: 1));
    }
}
