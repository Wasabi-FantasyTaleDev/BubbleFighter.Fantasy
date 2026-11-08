using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the KR2015 pirate HUD counter independently of combat scores and wallet rewards.</summary>
public sealed class CollectedLucciWireTests
{
    /// <summary>KR2015 replaces row +128 with absolute stat 13 while a zero team leaves totals untouched.</summary>
    [Theory]
    [InlineData(100, "00000000FF010000000D640000000000")]
    [InlineData(150, "00000000FF010000000D960000000000")]
    [InlineData(100000, "00000000FF010000000DFFFF00000000")]
    public void CoinOnlyUpdatePinsItsNativeBody(int count, string expected)
    {
        var message = new RoundScoreUpdated(1, 0, [7, 9], [new(0, 5, 6, 7, 8) { CollectedLucci = count }])
            { CollectedLucciOnly = true };
        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-812", message);
        Assert.Equal(16, body.Length);
        Assert.Equal(expected, Convert.ToHexString(body));
    }

    /// <summary>A later full scoreboard retains the HUD's value without replacing the earned-wallet column.</summary>
    [Theory]
    [InlineData("kr-812", 123)]
    [InlineData("cn-717", 0)]
    [InlineData("cn-324", 0)]
    public void FullScoreboardProjectsOnlyTheAuditedPickupCounter(string build, int expected)
    {
        var row = new RoundScoreboardRow(0, "", 1, 1, new(0, 0, 0, 0, 0) { CollectedLucci = 123 }, null);
        var fields = TestBuilds.Packets.Resolve(build, "ScoreRow");
        var body = RoundResultWire.WriteScoreRowBody(TestBuilds.Packets, build, fields, row);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "ScoreRow", body);
        if (expected > 0) Assert.Equal(expected, decoded["unknown_128"]);
        else Assert.Equal(RoundResultWire.WriteScoreRowBody(TestBuilds.Packets, build, fields,
            row with { Stats = row.Stats with { CollectedLucci = null } }), body);
        if (decoded.ContainsKey("lucci_gained")) Assert.Equal(0, decoded["lucci_gained"]);
    }

    /// <summary>Native evidence is scoped to explicitly named builds, even when a new build inherits KR2015 layouts.</summary>
    [Fact]
    public void UnlistedDescendantDoesNotInheritPickupCounterMeaning()
    {
        var directory = CopyTables();
        File.AppendAllText(Path.Combine(directory, "builds.yaml"), "\n  kr-9999:\n    base: kr-812\n");
        Assert.Null(RoundResultWire.CollectedLucciField(PacketTables.Load(directory), "kr-9999"));
    }

    /// <summary>Malformed audit lists fail before any counter packet is sent.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[kr-812, kr-812]")]
    [InlineData("[kr-9999]")]
    [InlineData("true")]
    public void InvalidMembershipFailsAtTableValidation(string value)
    {
        var directory = CopyTables();
        var path = Path.Combine(directory, "builds.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("collected_lucci_builds: [kr-812]",
            "collected_lucci_builds: " + value, StringComparison.Ordinal));
        Assert.Contains("collected_lucci_builds", Assert.Throws<ConfigException>(() => RoomTables.Load(directory)).Message);
    }

    /// <summary>An alias must resolve to an audited word rather than a named score, string or absent field.</summary>
    [Theory]
    [InlineData("score")]
    [InlineData("nickname")]
    [InlineData("unknown_999999")]
    [InlineData("[]")]
    public void InvalidCounterAliasFailsAtTableValidation(string value)
    {
        var directory = CopyTables();
        var path = Path.Combine(directory, "builds.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("collected_lucci_field: unknown_128",
            "collected_lucci_field: " + value, StringComparison.Ordinal));
        Assert.Throws<ConfigException>(() => RoomTables.Load(directory));
    }

    private static string CopyTables()
    {
        var directory = TestTempDirectories.ForTest("bff-collected-lucci-tables");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return directory;
    }
}
