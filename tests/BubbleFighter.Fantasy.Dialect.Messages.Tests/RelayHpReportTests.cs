using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the legacy relay HP reports and the damage reply on every build that mounts them.</summary>
public sealed class RelayHpReportTests
{
    public static TheoryData<string> Builds => new()
    {
        "kr-812", "cn-324", "id-400", "cn-404", "cn-406", "cn-408", "cn-717", "id-1809",
    };

    public static TheoryData<string, string, int> Layouts => new()
    {
        { "kr-812", "RelayDamageAttributeReport", 11 }, { "kr-812", "RelayDamageRequest", 18 },
        { "kr-812", "RelayCharacterRespawnReport", 8 }, { "kr-812", "RelayCharacterTypeReport", 10 },
        { "kr-812", "RelayHpRecoverReport", 9 }, { "kr-812", "RelayCharacterStateReport", 5 },
        { "kr-812", "RelayHpDelta", 8 },
        { "cn-324", "RelayDamageAttributeReport", 11 }, { "cn-324", "RelayDamageRequest", 18 },
        { "cn-324", "RelayCharacterRespawnReport", 8 }, { "cn-324", "RelayCharacterTypeReport", 10 },
        { "cn-324", "RelayHpRecoverReport", 9 }, { "cn-324", "RelayCharacterStateReport", 5 },
        { "cn-324", "RelayHpDelta", 8 },
    };

    /// <summary>Ensures each report reads exactly its deserializer's width and round-trips.</summary>
    [Theory, MemberData(nameof(Layouts))]
    public void ReportHasItsNativeWidth(string build, string structure, int width)
    {
        var body = Enumerable.Range(1, width).Select(value => (byte)value).ToArray();
        var values = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, values));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
    }

    /// <summary>Ensures the damage reply echoes the request's first word with a zero float.</summary>
    [Theory, MemberData(nameof(Builds))]
    public void DamageReplyEchoesTheFirstWord(string build)
    {
        var request = Convert.FromHexString("804F12009B0B00000000D6D1042514000000");
        Assert.Equal(Convert.FromHexString("804F120000000000"), RelayHpReports.Reply(TestBuilds.Packets, build, request));
    }

    /// <summary>Ensures exactly the builds with their own relay rows state the capability.</summary>
    [Fact]
    public void CapabilityFollowsTheBuildsWithRows()
    {
        foreach (var build in new[] { "kr-812", "cn-324", "id-400", "cn-404", "cn-406", "cn-408", "cn-717", "id-1809" })
            Assert.True(TestBuilds.Packets.CapabilityOf(build, "native_relay_hp_reports"), build);
        foreach (var build in new[] { "cn-279", "cn-705", "cn-706", "kr-1256", "cn-774", "tw-454" })
            Assert.False(TestBuilds.Packets.CapabilityOf(build, "native_relay_hp_reports"), build);
    }
}
