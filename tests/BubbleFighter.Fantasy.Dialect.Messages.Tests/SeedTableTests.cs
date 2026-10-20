using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the audited native seed-table wire shapes without enabling their runtime routes.</summary>
public sealed class SeedTableTests
{
    public static TheoryData<string> NativeBuilds => new()
    {
        "kr-812", "cn-717", "cn-324", "cn-406", "id-400", "id-1809",
    };

    /// <summary>Ensures every audited build has the same counted-vector critical table bytes.</summary>
    [Theory, MemberData(nameof(NativeBuilds))]
    public void CriticalTableUsesTwoCountedWordVectors(string build)
    {
        var bytes = PacketCodec.Write(TestBuilds.Packets, build, "RelayCriticalSeedTable", new Dictionary<string, object?>
        {
            ["entity_id"] = 0x11223344,
            ["unknown_8"] = new[] { 1, 2 },
            ["unknown_24"] = new[] { 3 },
            ["unknown_20"] = new[] { 3 },
        });
        Assert.Equal(Convert.FromHexString("443322110200000001000000020000000100000003000000"), bytes);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "RelayCriticalSeedTable", bytes);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "RelayCriticalSeedTable", decoded));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "RelayCriticalSeedTable", bytes[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "RelayCriticalSeedTable", [.. bytes, 0]));
    }

    /// <summary>Ensures the defence table is an actor ID followed by a counted byte vector.</summary>
    [Theory, MemberData(nameof(NativeBuilds))]
    public void DefenceTableUsesCountedBytes(string build)
    {
        var bytes = PacketCodec.Write(TestBuilds.Packets, build, "RelayDefenceSeedTable", new Dictionary<string, object?>
        {
            ["entity_id"] = 0x11223344,
            ["unknown_8"] = new byte[] { 0xAA, 0xBB },
        });
        Assert.Equal(Convert.FromHexString("4433221102000000AABB"), bytes);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "RelayDefenceSeedTable",
            PacketCodec.Read(TestBuilds.Packets, build, "RelayDefenceSeedTable", bytes)));
    }

    /// <summary>Ensures the cancellation table is an actor ID followed by a counted word vector.</summary>
    [Theory, MemberData(nameof(NativeBuilds))]
    public void CancelTableUsesCountedWords(string build)
    {
        var bytes = PacketCodec.Write(TestBuilds.Packets, build, "RelayCriticalSeedCancelTable", new Dictionary<string, object?>
        {
            ["entity_id"] = 0x11223344,
            ["unknown_8"] = new[] { 0x10, 0x20 },
        });
        Assert.Equal(Convert.FromHexString("44332211020000001000000020000000"), bytes);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "RelayCriticalSeedCancelTable",
            PacketCodec.Read(TestBuilds.Packets, build, "RelayCriticalSeedCancelTable", bytes)));
    }

    /// <summary>Leaves CN2011's unsupported native seed structures empty and its capability disabled.</summary>
    [Fact]
    public void Cn2011DoesNotClaimNativeSeedTables()
    {
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-279", "native_scene_seed_tables"));
        Assert.Empty(TestBuilds.Packets.Resolve("cn-279", "RelayCriticalSeedTable"));
        Assert.Empty(TestBuilds.Packets.Resolve("cn-279", "RelayDefenceSeedTable"));
        Assert.Empty(TestBuilds.Packets.Resolve("cn-279", "RelayCriticalSeedCancelTable"));
    }
}
