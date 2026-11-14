using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// The round's score and result bodies, pinned byte for byte for
/// a two-team round: the live score, the end, the win, loss and draw outcome
/// words, and the full final scoreboard whose every field the client code member reader
/// <c>sub_417720</c> consumes.
/// </summary>
public sealed class PlayRoundResultWireTests
{
    [Fact]
    public void CompletedRescueReportKeepsVictimAndRescuerSeats()
    {
        var body = new byte[32];
        BitConverter.GetBytes(4).CopyTo(body, 0);
        BitConverter.GetBytes(1).CopyTo(body, 12);
        BitConverter.GetBytes(2).CopyTo(body, 16);
        var report = Assert.IsType<RoundKillCreditReported>(PlayRoomCodec.DecodeWideScoreReport(TestBuilds.Packets, "cn-324", body));
        Assert.True(report.IsRescue);
        Assert.Equal(1, report.Victim);
        Assert.Equal(2, report.ReporterSlot);
    }
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, -1)]
    public void WideFinishingReportsKeepTheirExplicitVictimIncludingNpcReferences(int kind, int victim)
    {
        // cn-324 sub_467E00 writes eight u32; sub_476B90 emits kind 3 at 0x47846E.
        var body = new byte[32];
        BitConverter.GetBytes(kind).CopyTo(body, 0);
        BitConverter.GetBytes(1_100_000).CopyTo(body, 4);
        BitConverter.GetBytes(1_200_000).CopyTo(body, 8);
        BitConverter.GetBytes(victim).CopyTo(body, 12);
        BitConverter.GetBytes(2).CopyTo(body, 16);
        var report = Assert.IsType<RoundFinishingBlowReported>(PlayRoomCodec.DecodeWideScoreReport(TestBuilds.Packets, "cn-324", body));
        Assert.Equal(32, body.Length);
        Assert.Equal(2, report.ReporterSlot);
        Assert.True(report.HasVictimReference);
        Assert.Equal(victim < 0 ? (int?)null : victim, report.Victim);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(17)]
    public void WideNonKillReportsDoNotBecomePangCredits(int kind)
    {
        var body = new byte[32];
        BitConverter.GetBytes(kind).CopyTo(body, 0);
        Assert.Null(PlayRoomCodec.DecodeWideScoreReport(TestBuilds.Packets, "cn-324", body));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void WideScoreReportsRejectAnyOtherLength(int length) =>
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(
            () => PlayRoomCodec.DecodeWideScoreReport(TestBuilds.Packets, "cn-324", new byte[length]));

    // part02 s2c#204, 07:01:31.689: team 2, total 18, slot 0, no victim, four absolute counters.
    private const string LiveScoreBody = "02120000FF040000001D39000015001812000A000000000000";

    // part02 s2c#214, 07:01:46.676: the end, outcome 3.
    private const string GameEndBody = "0000000003000000";

    [Fact]
    public void EmptyLiveScoreKeepsBothMissingSeatSentinelsAndEmptyVectors()
    {
        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-812",
            new RoundScoreUpdated(0, 1, [0, 0], []));
        Assert.Equal("010000FFFF0000000000000000", Convert.ToHexString(body));
        Assert.Equal(13, body.Length);
    }

    [Fact]
    public void LiveScoreClampsIndividualCountersButRejectsUnrepresentableTeamTotal()
    {
        var message = new RoundScoreUpdated(0, 1, [ushort.MaxValue, 0],
            [new RoundSeatStats(0, Kills: int.MaxValue, Knockdowns: -1, Score: int.MaxValue, Deaths: -1)]);
        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-812", message);
        Assert.Equal("01FFFF00FF040000001DFFFF00000018FFFF0A000000000000", Convert.ToHexString(body));
        Assert.Throws<OverflowException>(() => RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-812",
            new RoundScoreUpdated(0, 1, [ushort.MaxValue + 1, 0], [])));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void TheLiveScoreBodyMatchesTheRecordedTick(string build)
    {
        var message = new RoundScoreUpdated(0, 2, [0, 18],
            [new RoundSeatStats(0, Kills: 18, Knockdowns: 21, Score: 57, Deaths: 0)]);

        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, build, message);

        Assert.Equal(25, body.Length);
        Assert.Equal(LiveScoreBody, Convert.ToHexString(body));
    }

    /// <summary>A WeaponMaster seat's level and experience follow its combat counters as kinds 31 to 34 (kr-1256 sub_9E9160).</summary>
    [Fact]
    public void AWeaponMasterSeatCarriesItsLevelAndExperience()
    {
        var message = new RoundScoreUpdated(0, 1, [18, 0],
            [new RoundSeatStats(0, Kills: 18, Knockdowns: 0, Score: 0, Deaths: 7) { WeaponMaster = new(10, 1800, 1800, 1801) }]);

        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-1256", message);

        // u8 team, u16 total, two u8 slots, eight u8 kind + u16 value pairs, then two empty counted lists.
        Assert.Equal(41, body.Length);
        Assert.Equal(8u, BitConverter.ToUInt32(body, 5));
        Assert.Equal("1F0A00" + "200807" + "210807" + "220907", Convert.ToHexString(body, 21, 12));
    }

    /// <summary>Modern seat vectors use each build's named result counters and retain the empty third list.</summary>
    [Theory]
    [InlineData("kr-1256", "0278560307040000001034120056340C45230E674504000000107856009A780C89670EFF7F00000000")]
    [InlineData("cn-774", "0278560307040000001E34120056341945231B6745040000001E7856009A781989671BFF7F00000000")]
    [InlineData("tw-454", "0278560307040000001E34120056341945231B6745040000001E7856009A781989671BFF7F00000000")]
    public void ModernLiveScorePinsBothSeatsNativeCounterKinds(string build, string expected)
    {
        var message = new RoundScoreUpdated(1, 2, [0, 0x5678],
            [new(3, Kills: 0x2345, Knockdowns: 0x3456, Score: 0x1234, Deaths: 0x4567),
             new(7, Kills: 0x6789, Knockdowns: 0x789A, Score: 0x5678, Deaths: 0x7FFF)]);

        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, build, message);

        Assert.Equal(41, body.Length);
        Assert.Equal(expected, Convert.ToHexString(body));
    }

    /// <summary>Absent modern seats leave all three vectors empty and keep both signed-byte sentinels.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernEmptyLiveScorePinsAllThreeVectors(string build)
    {
        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, build,
            new RoundScoreUpdated(1, 1, [0, 0], []));

        Assert.Equal(17, body.Length);
        Assert.Equal("010000FFFF000000000000000000000000", Convert.ToHexString(body));
    }

    /// <summary>Modern identifiers keep the existing counter saturation and checked team-total boundaries.</summary>
    [Theory]
    [InlineData("kr-1256", "01FFFF00FF0400000010FFFF0000000CFFFF0E00000000000000000000")]
    [InlineData("cn-774", "01FFFF00FF040000001EFFFF00000019FFFF1B00000000000000000000")]
    [InlineData("tw-454", "01FFFF00FF040000001EFFFF00000019FFFF1B00000000000000000000")]
    public void ModernLiveScorePinsSaturatedCounters(string build, string expected)
    {
        var message = new RoundScoreUpdated(1, 1, [ushort.MaxValue, 0],
            [new(0, Kills: int.MaxValue, Knockdowns: -1, Score: int.MaxValue, Deaths: -1)]);

        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, build, message);

        Assert.Equal(29, body.Length);
        Assert.Equal(expected, Convert.ToHexString(body));
        Assert.Throws<OverflowException>(() => RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, build,
            message with { TeamTotals = [ushort.MaxValue + 1, 0] }));
    }

    /// <summary>The generic KR1256 projection uses the same held-star kind as the dedicated StarTeam path.</summary>
    [Fact]
    public void ModernHeldStarsUseTheirAuditedKind()
    {
        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-1256",
            new RoundScoreUpdated(1, 1, [0, 0], [new(0, 0, 0, 0, 0) { Stars = 7 }]));

        Assert.Equal(32, body.Length);
        Assert.Equal("01000000FF050000001000000000000C00000E00001307000000000000000000", Convert.ToHexString(body));
    }

    /// <summary>Unreviewed modern star counters cannot accidentally reuse another build's kind.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void UnmappedModernStarsFailBeforeWriting(string build)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, build,
            new RoundScoreUpdated(1, 1, [0, 0], [new(0, 0, 0, 0, 0) { Stars = 7 }])));

        Assert.Contains("stars", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Later clients in the legacy lineage keep their established two-list counter bytes.</summary>
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void LegacyLineageKeepsItsExistingCounterKinds(string build)
    {
        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, build,
            new RoundScoreUpdated(1, 2, [0, 18], [new(0, Kills: 18, Knockdowns: 21, Score: 57, Deaths: 0)]));

        Assert.Equal(25, body.Length);
        Assert.Equal(LiveScoreBody, Convert.ToHexString(body));
    }

    [Fact]
    public void AnUnpairedTickCarriesMinusOneForTheVictimAndAnEmptySecondVector()
    {
        var message = new RoundScoreUpdated(0, 2, [0, 20],
            [new RoundSeatStats(0, Kills: 20, Knockdowns: 22, Score: 62, Deaths: 0)]);

        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-812", message);

        Assert.Equal(0xFF, body[4]);
        Assert.Equal(0u, BitConverter.ToUInt32(body, 21));
    }

    [Fact]
    public void ASecondSeatUpdateFillsTheVictimsVector()
    {
        var message = new RoundScoreUpdated(0, 1, [7, 0],
            [new RoundSeatStats(2, Kills: 3, Knockdowns: 1, Score: 7, Deaths: 4),
             new RoundSeatStats(3, Kills: 0, Knockdowns: 0, Score: 0, Deaths: 2)]);

        var body = RoundResultWire.WriteScoreUpdate(TestBuilds.Packets, "kr-812", message);

        Assert.Equal(37, body.Length);
        Assert.Equal(1, body[0]);
        Assert.Equal(7, BitConverter.ToUInt16(body, 1));
        Assert.Equal(2, body[3]);
        Assert.Equal(3, body[4]);
        Assert.Equal(4u, BitConverter.ToUInt32(body, 5));
        Assert.Equal(29, body[9]);                          // the scorer's first kind
        Assert.Equal(7, BitConverter.ToUInt16(body, 10));
        Assert.Equal(4u, BitConverter.ToUInt32(body, 21));  // the victim's vector follows
        // No recorded tick carries a victim vector, so only its shape is pinned: four absolute
        // counters behind the count, in the same kind order the scorer's vector uses.
        Assert.Equal(29, body[25]);
        Assert.Equal(0, body[26]);
    }

    [Fact]
    public void TheEndBodyCarriesTheOutcomeWord()
    {
        Assert.Equal(8, RoundResultWire.WriteGameEnd(TestBuilds.Packets, "kr-812", RoundOutcome.Won).Length);
        Assert.Equal(GameEndBody, Convert.ToHexString(RoundResultWire.WriteGameEnd(TestBuilds.Packets, "kr-812", RoundOutcome.Won)));
        // 3 sets the client's win flag, 1 and 2 clear it; 0 would leave the result stage inert.
        Assert.Equal(3, RoundResultWire.OutcomeWord(RoundOutcome.Won));
        Assert.Equal(2, RoundResultWire.OutcomeWord(RoundOutcome.Lost));
        Assert.Equal(1, RoundResultWire.OutcomeWord(RoundOutcome.Draw));
        Assert.Equal("0000000002000000", Convert.ToHexString(RoundResultWire.WriteGameEnd(TestBuilds.Packets, "kr-812", RoundOutcome.Lost)));
        Assert.Equal("0000000001000000", Convert.ToHexString(RoundResultWire.WriteGameEnd(TestBuilds.Packets, "kr-812", RoundOutcome.Draw)));
    }

    // SHA-256 of the two recorded scoreboards: part02 s2c#213 (4524 bytes, before the rewards) and
    // s2c#366 (4710 bytes, with them).
    private const string ScoreboardHash = "3EA132F0F53185FBDF0C6EAD8B0E66AB1BE5822BDDD84DCB042BFD53BE768042";
    private const string RewardScoreboardHash = "1BC0241E327323F5C937036B6A4FEC0AAA7C8FD3F44CB550DCB263078D19E967";

    private static readonly string[] RecordedBadges =
        ["Images/GameStage/GameResult/HOTTIME_exp2.smf", "Images/GameStage/GameResult/LXmarid_Bonus.smf"];

    [Fact]
    public void TheScoreboardBeforeTheRewardsIsTheRecordedBodyByteForByte()
    {
        var body = RoundResultWire.WriteScoreboard(TestBuilds.Packets, "kr-812",
            new RoundScoreboard(0, 2, [0, 20], [RecordedRow(expTotal: 1763, expGained: 0, lucciGained: 0, badges: [])]));

        Assert.Equal(4524, body.Length);
        Assert.Equal(ScoreboardHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)));
    }

    [Fact]
    public void TheScoreboardWithTheRewardsIsTheRecordedBodyByteForByte()
    {
        var body = RoundResultWire.WriteScoreboard(TestBuilds.Packets, "kr-812",
            new RoundScoreboard(0, 2, [0, 20], [RecordedRow(expTotal: 1879, expGained: 116, lucciGained: 588, RecordedBadges)]));

        Assert.Equal(4710, body.Length);
        Assert.Equal(RewardScoreboardHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)));
    }

    [Fact]
    public void ASeatNobodySitsInIsWrittenWithTheVacantValues()
    {
        var body = RoundResultWire.WriteScoreboard(TestBuilds.Packets, "kr-812", new RoundScoreboard(0, 0, [0, 0], []));

        // The header, seventeen team rows, sixteen vacant rows of 255 bytes and the sixteen tail pairs.
        Assert.Equal(20 + 17 * 16 + 16 * 255 + 128, body.Length);
        Assert.Equal(-1, BitConverter.ToInt32(body, 12));            // no leading team
        Assert.Equal(-1, BitConverter.ToInt32(body, 292));           // the first row's seat
        Assert.Equal(0, BitConverter.ToInt32(body, 292 + 128));      // the flag a seated row carries as 1
    }

    [Fact]
    public void Cn324ScoreboardIsItsOwnBlockOfEightRows()
    {
        var vacant = RoundResultWire.WriteScoreboard(TestBuilds.Packets, "cn-324", new RoundScoreboard(0, 0, [0, 0], []));
        var seated = RoundResultWire.WriteScoreboard(TestBuilds.Packets, "cn-324",
            new RoundScoreboard(0, 1, [3, 0], [RecordedRow(expTotal: 1763, expGained: 0, lucciGained: 0, badges: [])]));

        // u32 team count, 16 x 3 u32, eight rows of 221 bytes when vacant, eight u32 (reader cn-324 sub_6E9EF0).
        Assert.Equal(4 + 16 * 12 + 8 * 221 + 32, vacant.Length);
        Assert.Equal(16, BitConverter.ToInt32(vacant, 0));
        Assert.Equal(-1, BitConverter.ToInt32(vacant, 196));          // the first row's seat
        Assert.Equal(vacant.Length + 24, seated.Length);              // the 12-character name
        Assert.Equal(3, BitConverter.ToInt32(seated, 4 + 4));         // team 1's total, the second of its three values
        var row = 196;
        Assert.Equal(0, BitConverter.ToInt32(seated, row));           // the seat
        Assert.Equal(2, BitConverter.ToInt32(seated, row + 36));      // the team
        Assert.Equal(22, BitConverter.ToInt32(seated, row + 44));     // the knock-downs
        Assert.Equal(20, BitConverter.ToInt32(seated, row + 48));     // the kills
        Assert.Equal(62, BitConverter.ToInt32(seated, row + 64));     // the score
        Assert.Equal(1763, BitConverter.ToInt32(seated, row + 36 + 4 * 25)); // the experience
        Assert.Equal(1, BitConverter.ToInt32(seated, row + 36 + 4 * 29));    // the flag of a seated row
    }

    [Theory]
    [InlineData("cn-324", RoundOutcome.Won, 0)]
    [InlineData("cn-324", RoundOutcome.Lost, 0)]
    [InlineData("kr-812", RoundOutcome.Won, 0)]
    [InlineData("kr-812", RoundOutcome.Lost, 0)]
    [InlineData("cn-717", RoundOutcome.Won, 0)]
    public void RankDecorationDoesNotChangeWithTheRoundOutcome(string build, RoundOutcome outcome, int expected)
    {
        var (schema, play) = TestBuilds.Of(build);
        var fields = schema.ScoreRow;
        var won = fields.Single(field => field.Name == (build == "cn-324" ? "unknown_116" : "unknown_140"));
        var minimal = new[] { won };
        var row = new RoundScoreboardRow(0, "player", 2, 1, new RoundSeatStats(0, 0, 0, 0, 0), null) { Outcome = outcome };
        var body = RoundResultWire.WriteScoreRowBody(TestBuilds.Packets, build, minimal, row);
        Assert.Equal(4, body.Length);
        Assert.Equal(expected, BitConverter.ToInt32(body));
    }

    [Theory]
    [InlineData(false, "0000000003000000")]
    [InlineData(true, "0300000000000000")]
    public void TheOutcomeWordSitsWhereTheBuildReadsIt(bool outcomeFirst, string body) =>
        Assert.Equal(body, Convert.ToHexString(RoundResultWire.WriteGameEnd(TestBuilds.Packets, "kr-812", RoundOutcome.Won, outcomeFirst)));

    /// <summary>CN2012 horde mode 101 and Boss use the direct outcome; the native mode-10 branch is distinct.</summary>
    [Theory]
    [InlineData(101, 0, RoundOutcome.Won, 3)]
    [InlineData(101, 2, RoundOutcome.Won, 3)]
    [InlineData(101, 1, RoundOutcome.Won, 3)]
    [InlineData(101, 2, RoundOutcome.Lost, 2)]
    [InlineData(101, 1, RoundOutcome.Lost, 2)]
    [InlineData(16, 2, RoundOutcome.Won, 3)]
    [InlineData(16, 2, RoundOutcome.Lost, 2)]
    public void Cn324HordeAndBossRetainDirectOutcomeAcrossRecipientTeams(int mode, int team, RoundOutcome outcome, int expected)
    {
        var shape = TestBuilds.Of("cn-324").Play.Score!;
        Assert.False(shape.NpcTeamOutcome);
        var body = RoundResultWire.WriteGameEndForRecipient(TestBuilds.Packets, "cn-324", shape,
            new RoundEnded(1, outcome) { ModeId = mode, RecipientTeam = team });
        Assert.Equal(8, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body));
        Assert.Equal(expected, BitConverter.ToInt32(body, 4));
    }

    [Fact]
    public void Cn324InstallsTheResultGateFromTheSecondEndWordAfterReaderCleanup()
    {
        // cn-324 sub_408DF0 ret 4 shifts ESP; 0x6F1AE0's esp+0x30 is object +8, not +4.
        var shape = TestBuilds.Of("cn-324").Play.Score!;
        Assert.False(shape.OutcomeFirst);
        Assert.Equal("0000000003000000", Convert.ToHexString(
            RoundResultWire.WriteGameEnd(TestBuilds.Packets, "kr-812", RoundOutcome.Won, shape.OutcomeFirst)));
    }

    [Theory]
    [InlineData(1000000, 0)]
    [InlineData(1000003, 0)]
    [InlineData(1300000, 3)]
    [InlineData(1200053, 2)]
    [InlineData(999999, -1)]
    public void TheClientReferenceConvertsToASeat(int serial, int seat) =>
        Assert.Equal(seat, PlayRoomCodec.SeatOf(serial));

    [Theory]
    [InlineData(1000000, 0)]
    [InlineData(1100000, 1)]
    [InlineData(1200000, 2)]
    [InlineData(1000749, -1)]
    [InlineData(1000003, -1)]
    [InlineData(1200053, -1)]
    [InlineData(999999, -1)]
    public void RevivalDistinguishesAPlayerFromObjectsOwnedByThatPlayer(int serial, int seat) =>
        Assert.Equal(seat, PlayRoomCodec.PlayerSeatOf(serial));

    // The recorded scoreboard's only seated row: seat 0 on team 2, level 12, 22 knock-downs, 20 kills,
    // score 62, and the experience bar from 1737 to 2004.
    private static RoundScoreboardRow RecordedRow(int expTotal, int expGained, int lucciGained, string[] badges) =>
        new(0, "TestPlayer01", 2, 12,
            new RoundSeatStats(0, Kills: 20, Knockdowns: 22, Score: 62, Deaths: 0),
            new RoundRewardRow(12, LevelFloorExp: 1737, TotalExp: expTotal, NextLevelFloorExp: 2004,
                ExpEarned: expGained, LucciEarned: lucciGained, LeveledUp: false, badges));
}

