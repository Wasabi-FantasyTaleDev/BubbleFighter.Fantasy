using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks live-score identifier admission and isolation between immutable packet snapshots.</summary>
public sealed class LiveScoreKindsTests
{
    private const string BaselineKinds = "{ score: 29, knockdowns: 0, kills: 24, deaths: 10, stars: 35, collected_lucci: 13 }";
    private const string BaselineEntry = "    live_score_kinds: " + BaselineKinds;

    /// <summary>A result sender cannot start when the build has no counter-identifier map.</summary>
    [Fact]
    public void MissingMapFailsBeforeAResultSenderIsAvailable()
    {
        var directory = CopyTables();
        ReplaceEntry(directory, "");
        var tables = PacketTables.Load(directory);

        var error = Assert.Throws<ConfigException>(() => Sender(tables));

        Assert.Contains("live_score_kinds", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Scalar maps, absent names and unknown names are refused during room-table activation.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("[29, 0, 24, 10, 35, 13]")]
    [InlineData("{ score: 29, knockdowns: 0, kills: 24, stars: 35, collected_lucci: 13 }")]
    [InlineData("{ score: 29, knockdowns: 0, kills: 24, deaths: 10, collected_lucci: 13 }")]
    [InlineData("{ score: 29, knockdowns: 0, kills: 24, deaths: 10, stars: 35, lucci: 13 }")]
    [InlineData("{ score: 29, knockdowns: 0, kills: 24, deaths: 10, stars: 35, collected_lucci: 13, assists: 1 }")]
    public void InvalidMapShapeFailsAtActivation(string mapping)
    {
        var directory = WithKinds(mapping);

        var error = Assert.Throws<ConfigException>(() => RoomTables.Load(directory));

        Assert.Contains("live_score_kinds", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Required identifiers must be unsigned decimal bytes, including when malformed YAML values remain parseable.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("256")]
    [InlineData("1.5")]
    [InlineData("0x1D")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[29]")]
    [InlineData("{ value: 29 }")]
    public void InvalidRequiredIdentifierFailsAtActivation(string value)
    {
        var directory = WithKinds(BaselineKinds.Replace("score: 29", "score: " + value, StringComparison.Ordinal));

        var error = Assert.Throws<ConfigException>(() => RoomTables.Load(directory));

        Assert.Contains("live_score_kinds.score", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Two counters cannot overwrite the same client field, including optional counter aliases.</summary>
    [Theory]
    [InlineData("deaths: 10", "deaths: 24")]
    [InlineData("stars: 35", "stars: 29")]
    [InlineData("collected_lucci: 13", "collected_lucci: 35")]
    public void DuplicateIdentifiersFailAtActivation(string original, string replacement)
    {
        var directory = WithKinds(BaselineKinds.Replace(original, replacement, StringComparison.Ordinal));

        var error = Assert.Throws<ConfigException>(() => RoomTables.Load(directory));

        Assert.Contains("distinct", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An admitted pickup counter must have an identifier before table activation or sender construction.</summary>
    [Fact]
    public void AdmittedCoinCounterCannotHaveANullIdentifier()
    {
        var directory = WithKinds(BaselineKinds.Replace("collected_lucci: 13", "collected_lucci: null", StringComparison.Ordinal));
        var tables = PacketTables.Load(directory);

        var senderError = Assert.Throws<ConfigException>(() => Sender(tables));
        var tableError = Assert.Throws<ConfigException>(() => RoomTables.Load(tables));

        Assert.Contains("live_score_kinds.collected_lucci", senderError.Message, StringComparison.Ordinal);
        Assert.Contains("live_score_kinds.collected_lucci", tableError.Message, StringComparison.Ordinal);
    }

    /// <summary>Explicit nulls clear optional inherited identifiers without changing the required counters.</summary>
    [Fact]
    public void OptionalNullOverridesAreRetainedInTheResolvedBuild()
    {
        var directory = CopyTables();
        File.AppendAllText(Path.Combine(directory, "builds.yaml"),
            "\n  kr-9999:\n    base: kr-812\n    live_score_kinds: { stars: null, collected_lucci: null }\n");
        var tables = PacketTables.Load(directory);

        var parent = LiveScoreKinds.Require(tables, "kr-812");
        var descendant = LiveScoreKinds.Require(tables, "kr-9999");

        Assert.Null(descendant.Stars);
        Assert.Null(descendant.CollectedLucci);
        Assert.Equal(parent.Score, descendant.Score);
        Assert.Equal(parent.Knockdowns, descendant.Knockdowns);
        Assert.Equal(parent.Kills, descendant.Kills);
        Assert.Equal(parent.Deaths, descendant.Deaths);
        Assert.NotNull(parent.Stars);
        Assert.NotNull(parent.CollectedLucci);
    }

    /// <summary>Cached identifiers stay with their original snapshot while a changed file creates a distinct mapping.</summary>
    [Fact]
    public void ChangedDataCreatesANewCacheWithoutMutatingTheAdmittedSnapshot()
    {
        var directory = CopyTables();
        var original = PacketTables.Load(directory);
        var originalKinds = LiveScoreKinds.Require(original, "kr-812");
        var returnedValues = Assert.IsType<Dictionary<string, object?>>(original.ValuesOf("kr-812")["live_score_kinds"]);
        returnedValues["score"] = "126";
        ReplaceEntry(directory, "    live_score_kinds: " + BaselineKinds.Replace("score: 29", "score: 127", StringComparison.Ordinal));

        var updated = PacketTables.Load(directory);
        var updatedKinds = LiveScoreKinds.Require(updated, "kr-812");

        Assert.NotSame(original, updated);
        Assert.NotSame(originalKinds, updatedKinds);
        Assert.Same(originalKinds, LiveScoreKinds.Require(original, "kr-812"));
        Assert.Same(updatedKinds, LiveScoreKinds.Require(updated, "kr-812"));
        Assert.Equal((byte)29, originalKinds.Score);
        Assert.Equal((byte)127, updatedKinds.Score);
    }

    /// <summary>Two builds in the same snapshot resolve their own overrides rather than sharing one build's cache entry.</summary>
    [Fact]
    public void CachedIdentifiersRemainScopedToTheirBuild()
    {
        var directory = CopyTables();
        File.AppendAllText(Path.Combine(directory, "builds.yaml"),
            "\n  kr-9999:\n    base: kr-812\n    live_score_kinds: { score: 127 }\n");
        var tables = PacketTables.Load(directory);

        var parent = LiveScoreKinds.Require(tables, "kr-812");
        var descendant = LiveScoreKinds.Require(tables, "kr-9999");

        Assert.Equal((byte)29, parent.Score);
        Assert.Equal((byte)127, descendant.Score);
        Assert.Same(parent, LiveScoreKinds.Require(tables, "kr-812"));
        Assert.Same(descendant, LiveScoreKinds.Require(tables, "kr-9999"));
    }

    /// <summary>Identifier admission also rejects element layouts that cannot carry one byte and one unsigned word.</summary>
    [Theory]
    [InlineData("kind", "u8", "u16")]
    [InlineData("value", "u16", "u32")]
    public void IncompatibleElementWidthsFailBeforeSending(string field, string original, string replacement)
    {
        var directory = PacketTableEdits.WithFieldType("RoundLiveScoreStat", field, original, replacement);
        var tables = PacketTables.Load(directory.FullName);

        var error = Assert.Throws<ConfigException>(() => Sender(tables));

        Assert.Contains("RoundLiveScoreStat." + field, error.Message, StringComparison.Ordinal);
    }

    private static RoundResultSender Sender(PacketTables tables) => new("kr-812/game", tables, "kr-812",
        _ => throw new InvalidOperationException("Opcode lookup is not part of sender admission."), TestBuilds.Of("kr-812").Play.Score!);

    private static string WithKinds(string mapping)
    {
        var directory = CopyTables();
        ReplaceEntry(directory, "    live_score_kinds: " + mapping);
        return directory;
    }

    private static string CopyTables()
    {
        var directory = TestTempDirectories.ForTest("bff-live-score-kinds");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return directory;
    }

    private static void ReplaceEntry(string directory, string replacement)
    {
        var path = Path.Combine(directory, "builds.yaml");
        var text = File.ReadAllText(path);
        Assert.Contains(BaselineEntry, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(BaselineEntry, replacement, StringComparison.Ordinal));
    }
}
