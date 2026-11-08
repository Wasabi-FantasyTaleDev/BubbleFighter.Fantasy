using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the audited bomb series selectors separately from ordinary entry and kill-score bytes.</summary>
public sealed class BombSeriesWireTests
{
    /// <summary>Every playable round carries the native zero-based index, explicit third-round role and unchanged scene state.</summary>
    [Theory]
    [InlineData("kr-812", 36, 0, 1)]
    [InlineData("kr-812", 36, 1, 2)]
    [InlineData("kr-812", 36, 2, 1)]
    [InlineData("cn-717", 40, 0, 1)]
    [InlineData("cn-717", 40, 1, 2)]
    [InlineData("cn-717", 40, 2, 1)]
    public void EntryPinsIndexAndAttacker(string build, int length, int index, int attacker)
    {
        var body = RoundEntryWire.Write(TestBuilds.Packets, build, 0, "MapA", roomState: 3,
            series: new(index, attacker));
        Assert.Equal(length, body.Length);
        var values = PacketCodec.Read(TestBuilds.Packets, build, "RoomEnterGame", body);
        var tail = Assert.IsType<Dictionary<string, object?>>(values["tail"]);
        Assert.Equal(index, Convert.ToInt32(tail["unknown_36"], CultureInfo.InvariantCulture));
        Assert.Equal(attacker, Convert.ToInt32(tail["unknown_40"], CultureInfo.InvariantCulture));
        Assert.Equal(3, Convert.ToInt32(tail["unknown_44"], CultureInfo.InvariantCulture));
    }

    /// <summary>The series flag preserves unrelated native flag bits and every other setting byte.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void InitialSettingsEnableOnlyTheSeriesFlagAndIndex(string build)
    {
        var packets = TestBuilds.Packets;
        var (schema, _) = TestBuilds.Of(build);
        var selectors = RoundSeriesFields.Require(packets, build);
        var values = new Dictionary<string, object?> { [selectors.SettingsFlags] = 0x40, [selectors.SettingsIndex!] = 7, ["mode"] = 19 };
        var before = PacketCodec.Write(packets, build, "GameSetting", values);
        var after = PlazaRoomWire.ApplyGameSettingSeries(schema, before, selectors, 0);
        values[selectors.SettingsFlags] = 0x41;
        values[selectors.SettingsIndex!] = 0;
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(PacketCodec.Write(packets, build, "GameSetting", values), after);
    }

    /// <summary>The fourth team word carries round wins while the second retains kill totals and the unknown outcome flag stays untouched.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void RoundWinsDoNotReplaceKillsOrUnknownOutcomeFlag(string build)
    {
        var board = new RoundScoreboard(1, 1, [23, 41], []) { ModeId = 19, RoundWins = [2, 1] };
        var body = RoundResultWire.WriteScoreboard(TestBuilds.Packets, build, board);
        var ordinary = RoundResultWire.WriteScoreboard(TestBuilds.Packets, build, board with { RoundWins = null });
        Assert.Equal(ordinary.Length, body.Length);
        var values = PacketCodec.Read(TestBuilds.Packets, build, "ScoreData", body);
        var teams = Assert.IsType<object?[]>(values["teams"]);
        var first = Assert.IsType<Dictionary<string, object?>>(teams[0]);
        var second = Assert.IsType<Dictionary<string, object?>>(teams[1]);
        Assert.Equal(23, Convert.ToInt32(first["total"], CultureInfo.InvariantCulture));
        Assert.Equal(41, Convert.ToInt32(second["total"], CultureInfo.InvariantCulture));
        Assert.Equal(2, Convert.ToInt32(first["unknown_228"], CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(second["unknown_228"], CultureInfo.InvariantCulture));
        Assert.Equal(false, values["unknown_300"]);
    }

    /// <summary>Unaudited lineages reject series projection even when they can enter ordinary rounds.</summary>
    [Theory]
    [InlineData("cn-406")]
    [InlineData("cn-705")]
    [InlineData("cn-324")]
    [InlineData("cn-735")]
    public void UnsupportedBuildRejectsSeries(string build)
    {
        Assert.Null(RoundSeriesFields.Load(TestBuilds.Packets, build));
        Assert.Throws<ConfigException>(() => RoundEntryWire.Write(TestBuilds.Packets, build, 0, "MapA", series: new(0, 1)));
    }

    /// <summary>A selector whose native field width changed cannot advertise usable series support.</summary>
    [Theory]
    [InlineData("GameSetting", "unknown_172")]
    [InlineData("GameSetting", "unknown_252")]
    [InlineData("RoomEnterGameTail", "unknown_36")]
    [InlineData("RoomEnterGameTail", "unknown_40")]
    [InlineData("ScoreTeam", "unknown_228")]
    public void SeriesMappingRejectsWrongFieldWidth(string structure, string field)
    {
        var directory = structure == "ScoreTeam" ? CopyTables()
            : PacketTableEdits.WithFieldType(structure, field, "u32", "u16");
        if (structure == "ScoreTeam")
        {
            // The same field name also occurs in a nested build override; alter only the baseline row.
            var path = Path.Combine(directory.FullName, "score.yaml");
            var text = File.ReadAllText(path);
            var start = text.IndexOf("  ScoreTeam:", StringComparison.Ordinal);
            var end = text.IndexOf("\n  ScoreTail:", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            var block = text[start..end];
            const string original = "\n    - unknown_228: u32";
            Assert.Equal(2, block.Split(original, StringSplitOptions.None).Length);
            File.WriteAllText(path, text[..start] + block.Replace(original, "\n    - unknown_228: u16", StringComparison.Ordinal) + text[end..]);
        }
        var tables = PacketTables.Load(directory.FullName);
        Assert.Throws<ConfigException>(() => RoundSeriesFields.Load(tables, "kr-812"));
    }

    /// <summary>Malformed selector mappings fail before they can write another native field.</summary>
    [Theory]
    [InlineData("settings_flags", "not_a_native_field")]
    [InlineData("settings_index", "unknown_172")]
    [InlineData("entry_attacker", "unknown_36")]
    [InlineData("team_wins", "")]
    public void SeriesMappingRejectsMissingOrOverlappingFields(string key, string value)
    {
        var directory = CopyTables().FullName;
        var fields = Assert.IsType<Dictionary<string, object?>>(TestBuilds.Packets.ValuesOf("kr-812")["round_series_fields"]);
        var path = Path.Combine(directory, "builds.yaml");
        var text = File.ReadAllText(path);
        var original = $"      {key}: {fields[key]}";
        Assert.Contains(original, text);
        File.WriteAllText(path, text.Replace(original, $"      {key}: '{value}'", StringComparison.Ordinal));
        var tables = PacketTables.Load(directory);
        Assert.Throws<ConfigException>(() => RoundSeriesFields.Load(tables, "kr-812"));
    }

    private static DirectoryInfo CopyTables()
    {
        var directory = new DirectoryInfo(TestTempDirectories.ForTest("bff-bomb-series-mapping"));
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory.FullName, Path.GetFileName(file)));
        return directory;
    }

    /// <summary>Only the three playable round indices and two native team values can enter the series wire.</summary>
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(3, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    public void EntryRejectsInvalidSeriesSelectors(int index, int attacker) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RoundEntryWire.Write(TestBuilds.Packets, "kr-812", 0,
            "MapA", series: new(index, attacker)));

    /// <summary>Wins are neither negative scores nor an unbounded collection of team counters.</summary>
    [Theory]
    [InlineData(19, -1, 0)]
    [InlineData(19, 3, 0)]
    [InlineData(1, 1, 0)]
    public void ScoreboardRejectsInvalidRoundWins(int mode, int first, int second)
    {
        var board = new RoundScoreboard(1, 1, [0, 0], []) { ModeId = mode, RoundWins = [first, second] };
        Assert.Throws<ArgumentException>(() => RoundResultWire.WriteScoreboard(TestBuilds.Packets, "kr-812", board));
    }
}
