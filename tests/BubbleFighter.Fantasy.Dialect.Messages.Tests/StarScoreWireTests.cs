using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins StarTeam held counts and absolute team totals through modern live and complete score publications.</summary>
public sealed class StarScoreWireTests
{
    /// <summary>KR1256 publishes kind 19 and both star-team indices before its second scalar total frame.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public async Task StarTeamLiveBurstPinsNativeCountersAndRemappedOpcode(int shift)
    {
        var native = TestBuilds.Opcodes("kr-1256");
        var table = new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + shift)) }));
        var session = new Session(table.Profile);
        var sender = Sender("kr-1256", table);
        var board = Board(6, 60, 15);
        await sender.SendAsync(session, new RoundScoreUpdated(1, 2, board.TeamTotals,
            board.Rows.Select(row => row.Stats).ToArray(), board), default);
        Assert.Collection(session.Sent,
            first =>
            {
                Assert.Equal(table.S2C("gspUpdateScoreData"), first.Opcode);
                Assert.Equal(29, first.Body.Length);
                Assert.Equal("013C00000101000000133C0001000000130F0002000000013C00020F00", Convert.ToHexString(first.Body));
            },
            second =>
            {
                Assert.Equal(table.S2C("gspUpdateScoreData"), second.Opcode);
                Assert.Equal("020F00FFFF000000000000000000000000", Convert.ToHexString(second.Body));
            });
    }

    /// <summary>A one-seat decrement replaces its count and both totals without inventing an absent second seat.</summary>
    [Fact]
    public void StarLossKeepsAbsoluteValuesAndMissingSeatSentinel()
    {
        var board = Board(6, 20, 7);
        var fields = Assert.IsType<StarScoreFields>(StarScoreFields.Load(TestBuilds.Packets, "kr-1256"));
        var body = RoundResultWire.WriteStarScoreUpdate(TestBuilds.Packets, "kr-1256", fields,
            new RoundScoreUpdated(1, 2, board.TeamTotals, [board.Rows[1].Stats], board));
        Assert.Equal(26, body.Length);
        Assert.Equal("01140001FF010000001307000000000002000000011400020700", Convert.ToHexString(body));
    }

    /// <summary>The native update cannot alias a seat, address outside its table or silently discard a third row.</summary>
    [Theory]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 36 })]
    [InlineData(new[] { 256 })]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 0, 1, 2 })]
    public void InvalidChangedSeatsFailBeforeEncoding(int[] slots)
    {
        var board = Board(6, 20, 7);
        var fields = Assert.IsType<StarScoreFields>(StarScoreFields.Load(TestBuilds.Packets, "kr-1256"));
        var seats = slots.Select(slot => new RoundSeatStats(slot, 0, 0, 0, 0) { Stars = 10 }).ToArray();
        Assert.Throws<ArgumentException>(() => RoundResultWire.WriteStarScoreUpdate(TestBuilds.Packets, "kr-1256", fields,
            new RoundScoreUpdated(1, 1, board.TeamTotals, seats, board)));
    }

    /// <summary>A subsequent full table retains held stars and team stars in both live and final result passes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullBoardDoesNotEraseStarTeamState(bool rewards)
    {
        var table = TestBuilds.Opcodes("kr-1256");
        var board = Board(6, 60, 15) with { WithRewards = rewards };
        var body = RoundResultWire.WriteMaskedScoreboard(TestBuilds.Packets, "kr-1256",
            new("unknown_500", new Dictionary<string, object?> { ["flags_19272"] = 0 }), new Session(table.Profile), null, board);
        var reader = new PacketReader(body);
        var lead = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, "kr-1256", "ScoreDataLead");
        Assert.Equal((byte)2, lead["head_count"]);
        foreach (var expected in new[] { 60, 15 })
        {
            var head = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, "kr-1256", "ScoreHeadEntry");
            Assert.Equal(expected, head["team_total"]);
            Assert.Equal(expected, head["unknown_160"]);
            Assert.Equal(0, head["unknown_308"]);
        }
        var mask = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, "kr-1256", "ScoreDataMask");
        Assert.Equal(3L, mask["presence_mask"]);
        foreach (var expected in new[] { 60, 15 })
        {
            var row = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, "kr-1256", "ScoreRow");
            Assert.Equal(expected, row["unknown_120"]);
            Assert.Equal(0, row["unknown_60"]);
            Assert.Equal(0, row["unknown_64"]);
            Assert.Equal(0, row["unknown_132"]);
        }
        for (var index = 0; index < 4; index++)
            _ = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, "kr-1256", "ScoreGatedWord");
        _ = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, "kr-1256", "ScoreDataTail");
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Unadmitted star projections leave held stars untouched while publishing ordinary combat counters.</summary>
    [Theory]
    [InlineData("kr-1256", 1, "013C0000FF040000001063000003000C04000E02000000000000000000")]
    [InlineData("kr-1256", 5, "013C0000FF040000001063000003000C04000E02000000000000000000")]
    [InlineData("cn-774", 6, "013C0000FF040000001E63000003001904001B02000000000000000000")]
    [InlineData("tw-454", 6, "013C0000FF040000001E63000003001904001B02000000000000000000")]
    public async Task UnmappedStarModeOrBuildPublishesOnlyCombatCounters(string build, int mode, string first)
    {
        var table = TestBuilds.Opcodes(build);
        var session = new Session(table.Profile);
        var board = Board(mode, 60, 15);
        await Sender(build, table).SendAsync(session,
            new RoundScoreUpdated(1, 1, board.TeamTotals, [board.Rows[0].Stats], board), default);
        Assert.Equal(new[] { first, "020F00FFFF000000000000000000000000" },
            session.Sent.Select(frame => Convert.ToHexString(frame.Body)));
    }

    /// <summary>Malformed membership, kinds, modes and destination aliases are rejected during table activation.</summary>
    [Theory]
    [InlineData("star_score_builds: [kr-1256]", "star_score_builds: [kr-1256, kr-1256]")]
    [InlineData("star_report_slot_builds: [kr-1256]", "star_report_slot_builds: [kr-1256, kr-1256]")]
    [InlineData("star_report_slot_builds: [kr-1256]", "star_report_slot_builds: [kr-9999]")]
    [InlineData("star_live_stat_kind: 19", "star_live_stat_kind: 256")]
    [InlineData("star_score_modes: [6]", "star_score_modes: []")]
    [InlineData("star_score_team_modes: [6]", "star_score_team_modes: [5]")]
    [InlineData("star_live_team_total_field: unknown_32", "star_live_team_total_field: unknown_999999")]
    [InlineData("star_scoreboard_count_field: unknown_120", "star_scoreboard_count_field: nickname")]
    [InlineData("star_scoreboard_team_total_field: unknown_160", "star_scoreboard_team_total_field: unknown_999999")]
    public void InvalidStarMappingFailsAtActivation(string original, string replacement)
    {
        var directory = TestTempDirectories.ForTest("bff-star-score-mapping");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var path = Path.Combine(directory, "builds.yaml");
        var text = File.ReadAllText(path);
        Assert.Contains(original, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<ConfigException>(() => RoomTables.Load(directory));
    }

    private static RoundResultSender Sender(string build, IOpcodeTable table) => new(build + "/game",
        TestBuilds.Packets, build, _ => table, [6], new("unknown_500", new Dictionary<string, object?>()), null);

    private static RoundScoreboard Board(int mode, int red, int blue) => new(1, 1, [red, blue],
        [new(0, "", 1, 1, new(0, 4, 3, 99, 2) { Stars = red }, null),
         new(1, "", 2, 1, new(1, 2, 1, 88, 4) { Stars = blue }, null)]) { ModeId = mode };

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }
}
