using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks native series index, attacker and cumulative-win projection independently for each dialect.</summary>
public sealed class NativeSeriesProjectionTests
{
    /// <summary>The entry uses the build's own selectors and rejects an index beyond its decisive round.</summary>
    [Theory]
    [InlineData("kr-812", "unknown_36", "unknown_40", "unknown_44", 2)]
    [InlineData("cn-717", "unknown_36", "unknown_40", "unknown_44", 2)]
    [InlineData("id-400", "unknown_3204", "unknown_3208", "unknown_3212", 2)]
    [InlineData("cn-404", "unknown_4692", "unknown_4696", "unknown_4700", 2)]
    [InlineData("cn-408", "unknown_4740", "unknown_4744", "unknown_4748", 2)]
    [InlineData("cn-706", "unknown_36", "unknown_40", "unknown_44", 2)]
    [InlineData("id-1809", "unknown_36", "unknown_40", "unknown_44", 2)]
    [InlineData("kr-1256", "unknown_76", "unknown_80", "room_state", 3)]
    [InlineData("cn-774", "unknown_76", "unknown_80", "room_state", 3)]
    [InlineData("tw-454", "unknown_76", "unknown_80", "room_state", 3)]
    public void EntryProjectsOwnNativeFields(string build, string indexField, string attackerField, string stateField, int roundsToWin)
    {
        var tables = TestBuilds.Packets;
        var selectors = RoundSeriesFields.Require(tables, build);
        Assert.Equal(roundsToWin, selectors.RoundsToWin);
        for (var index = 0; index <= 2 * (roundsToWin - 1); index++)
        {
            var body = RoundEntryWire.Write(tables, build, 0, "MapA", roomState: 3, series: new(index, 2));
            var entry = PacketCodec.Read(tables, build, "RoomEnterGame", body);
            var fields = entry.TryGetValue("tail", out var tail) ? Assert.IsType<Dictionary<string, object?>>(tail) : entry;
            Assert.Equal(index, Convert.ToInt32(fields[indexField], CultureInfo.InvariantCulture));
            Assert.Equal(2, Convert.ToInt32(fields[attackerField], CultureInfo.InvariantCulture));
            Assert.Equal(3, Convert.ToInt32(fields[stateField], CultureInfo.InvariantCulture));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => RoundEntryWire.Write(tables, build, 0, "MapA",
            series: new(2 * roundsToWin - 1, 1)));
    }

    /// <summary>Masked results retain team kills and write round wins into the separate third head vector.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernSeriesWinsPinTheNativeHeadBytes(string build)
    {
        var table = TestBuilds.Opcodes(build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var board = new RoundScoreboard(7, 1, [23, 41], []) { ModeId = 19, RoundWins = [3, 2], WithRewards = true };
        var packets = TestBuilds.Packets;
        var tail = packets.Resolve(build, "ScoreDataTail").Where(field => field.Name.StartsWith("flags_", StringComparison.Ordinal))
            .ToDictionary(field => field.Name, field => (object?)0);
        var marker = Assert.IsType<string>(packets.ValuesOf(build)["round_score_row_marker"]);
        var body = RoundResultWire.WriteMaskedScoreboard(packets, build, new(marker, tail), session, null, board);
        Assert.Equal("020101000000170000000000000003000000290000000000000002000000", Convert.ToHexString(body.AsSpan(0, 30)));
        var live = RoundResultWire.WriteMaskedScoreboard(packets, build, new(marker, tail), session, null,
            board with { WithRewards = false });
        Assert.Equal("02FF00000000", Convert.ToHexString(live.AsSpan(0, 6)));
        Assert.Equal(body.AsSpan(6, 24).ToArray(), live.AsSpan(6, 24).ToArray());
        Assert.Throws<ArgumentException>(() => RoundResultWire.WriteMaskedScoreboard(packets, build, new(marker, tail), session, null,
            board with { RoundWins = [4, 0] }));
    }
}
