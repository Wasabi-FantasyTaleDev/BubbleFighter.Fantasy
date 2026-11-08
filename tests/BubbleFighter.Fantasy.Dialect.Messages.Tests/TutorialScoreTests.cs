using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the selected tutorial's native report attribution and complete score restatement.</summary>
public sealed class TutorialScoreTests
{
    [Theory]
    [InlineData("cn-406", true)]
    [InlineData("cn-407", false)]
    [InlineData("cn-408", true)]
    [InlineData("cn-705", true)]
    [InlineData("cn-706", true)]
    [InlineData("cn-404", true)]
    [InlineData("kr-812", true)]
    [InlineData("cn-717", true)]
    [InlineData("cn-324", true)]
    [InlineData("cn-279", false)]
    public async Task TutorialScoreMountRequiresExplicitBuildMembershipAndUnloads(string build, bool supported)
    {
        var directory = TestBuilds.PacketsDirectory();
        if (build == "cn-407")
        {
            directory = CopyTables();
            File.AppendAllText(Path.Combine(directory, "builds.yaml"), "\n  cn-407:\n    base: cn-406\n");
        }
        var roomTables = RoomTables.Load(directory);
        if (!supported)
            Assert.Null(PlayRoomCodec.TutorialNpcFinishFields(roomTables.Tables, build));
        var (schema, play) = roomTables.Of(build);
        var table = build == "cn-407"
            ? new OpcodeTable(new ClientProfile(build, "cn", 407, 0, 0, build), TestBuilds.Opcodes("cn-406").All)
            : TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol);
        try
        {
            new PlayRoomCodec(protocol, build, schema, play, fixture.Services.Get<IOpcodeTableRegistry>(),
                NullLogger.Instance, roomTables.Tables).Mount(context);
            Assert.Equal(supported ? 1 : 0, fixture.Services.GetAll<IInboundMessageBinding<TutorialScoreReported>>().Count);
            Assert.Equal(supported ? 1 : 0, fixture.Services.GetAll<IMessageSender<TutorialScoreUpdated>>().Count);
            var narrow = build is "kr-812" or "cn-717" or "cn-705" or "cn-706";
            var body = new byte[narrow ? 16 : 32];
            BitConverter.GetBytes(1).CopyTo(body, 0);
            BitConverter.GetBytes(1_100_000).CopyTo(body, narrow ? 1 : 4);
            BitConverter.GetBytes(1_000_000).CopyTo(body, narrow ? 5 : 8);
            if (build != "cn-279")
            {
                var session = new MatchMessagesMountTests.Session(table.Profile);
                var opcode = table.C2S("gcpGameUpdateScore");
                await fixture.Packets.DispatchAsync(protocol, session, new(opcode, body), default);
                Assert.Single(fixture.Messages.Received.OfType<RoundKillCreditReported>());
                Assert.Equal(supported ? 1 : 0, fixture.Messages.Received.OfType<TutorialScoreReported>().Count());
                if (supported)
                {
                    var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<TutorialScoreUpdated>>());
                    await sender.SendAsync(session, new(0, "", 13, 7, 5), default);
                    Assert.Equal(table.S2C("gspGameBroadcastPlayerScoreData"), Assert.Single(session.Sent));
                    Assert.Equal(build switch
                    {
                        "kr-812" => WriteExpectedKoreanScore(),
                        "cn-717" => WriteExpectedChinese2015Score(),
                        "cn-324" => WriteExpectedEarlyScore(),
                        "cn-404" => WriteExpectedChinese2014Score(),
                        "cn-408" => WriteExpectedChinese2014Score(),
                        "cn-705" => WriteExpectedSeptember2014Score(),
                        "cn-706" => WriteExpectedSeptember2014Score(),
                        _ => WriteExpectedScore(),
                    }, Assert.Single(session.Bodies));
                    if (narrow)
                    {
                        var compact = table.C2S("gcpGameUpdateScoreOptimze");
                        await fixture.Packets.DispatchAsync(protocol, session, new(compact, new byte[] { 2, 0, 0, 0, 0, 0 }), default);
                        Assert.Equal(new TutorialScoreReported(0, TutorialScoreEvent.Pang), fixture.Messages.Received.OfType<TutorialScoreReported>().Last());
                        Assert.Single(fixture.Messages.Received.OfType<RoundFinishingBlowReported>());
                    }
                    else if (build is "cn-324" or "cn-406" or "cn-404" or "cn-408")
                    {
                        var ordinaryBefore = fixture.Messages.Received.Count(message => message is not TutorialScoreReported);
                        var npc = Convert.FromHexString("020000000000000000000000FFFFFFFF00000000000000000000000000000000");
                        await fixture.Packets.DispatchAsync(protocol, session, new(opcode, npc), default);
                        Assert.Equal(new TutorialScoreReported(0, TutorialScoreEvent.Pang), fixture.Messages.Received.OfType<TutorialScoreReported>().Last());
                        Assert.Equal(2, fixture.Messages.Received.OfType<TutorialScoreReported>().Count());
                        var ordinary = fixture.Messages.Received.Where(message => message is not TutorialScoreReported)
                            .Skip(ordinaryBefore).ToArray();
                        if (build == "cn-324")
                            Assert.Equal(new RoundFinishingBlowReported(0) { HasVictimReference = true }, Assert.Single(ordinary));
                        else
                            Assert.Empty(ordinary);
                    }
                }
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<TutorialScoreReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<TutorialScoreUpdated>>());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[cn-406, cn-406]")]
    [InlineData("[cn-9999]")]
    [InlineData("true")]
    public void TutorialScoreBuildListRejectsMalformedOrUnknownMembership(string value)
    {
        var directory = CopyTables();
        var path = Path.Combine(directory, "builds.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("tutorial_score_builds: [cn-406]",
            "tutorial_score_builds: " + value, StringComparison.Ordinal));
        Assert.Contains("tutorial_score_builds", Assert.Throws<ConfigException>(() => RoomTables.Load(directory)).Message);
    }

    private static string CopyTables()
    {
        var directory = TestTempDirectories.ForTest("bff-tutorial-score-tables");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return directory;
    }

    /// <summary>Rejects native help aliases that collide, are absent or are not a word counter.</summary>
    [Theory]
    [InlineData("knock_downs")]
    [InlineData("kills")]
    [InlineData("nickname")]
    [InlineData("absent_counter")]
    [InlineData("[]")]
    public void TutorialHelpFieldRejectsInvalidNativeAliases(string alias)
    {
        var directory = CopyTables();
        var path = Path.Combine(directory, "builds.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("tutorial_score_help_field: unknown_32",
            "tutorial_score_help_field: " + alias, StringComparison.Ordinal));
        Assert.Throws<ConfigException>(() => RoomTables.Load(directory));
    }

    [Theory]
    [InlineData(1, TutorialScoreEvent.Bubble)]
    [InlineData(22, TutorialScoreEvent.Bubble)]
    [InlineData(2, TutorialScoreEvent.Pang)]
    [InlineData(4, TutorialScoreEvent.Help)]
    public void NativeTutorialGoalsUseScorerReferenceInsteadOfNpcSideWords(int kind, TutorialScoreEvent goal)
    {
        var body = new byte[32];
        BitConverter.GetBytes(kind).CopyTo(body, 0);
        BitConverter.GetBytes(1_000_001).CopyTo(body, 4);
        BitConverter.GetBytes(1_000_000).CopyTo(body, 8);
        BitConverter.GetBytes(-2).CopyTo(body, 12);
        BitConverter.GetBytes(-2).CopyTo(body, 16);
        Assert.Equal(new TutorialScoreReported(0, goal),
            PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-406", body));
        BitConverter.GetBytes(1_000_001).CopyTo(body, 8);
        Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-406", body));
        Assert.Throws<PacketFormatException>(() =>
            PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-406", body[..^1]));
        Assert.Throws<PacketFormatException>(() =>
            PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-406", [.. body, 0]));
    }

    /// <summary>Pins the separate NPC finishing envelope and preserves its explicit scorer seat.</summary>
    [Theory]
    [InlineData("cn-324", 0)]
    [InlineData("cn-324", 1)]
    [InlineData("cn-406", 0)]
    [InlineData("cn-406", 1)]
    [InlineData("cn-404", 0)]
    [InlineData("cn-404", 1)]
    [InlineData("cn-408", 0)]
    [InlineData("cn-408", 1)]
    public void NpcTutorialFinishUsesTheNativeSeatWhenBothReferencesAreZero(string build, int slot)
    {
        // cn-406 sub_5E3A90 / cn-324 sub_5E1F60 emit (kind 2, 0, 0, -1, seat, 0, 0, 0).
        var body = Convert.FromHexString("020000000000000000000000FFFFFFFF00000000000000000000000000000000");
        BitConverter.GetBytes(slot).CopyTo(body, 16);
        Assert.Equal(new TutorialScoreReported(slot, TutorialScoreEvent.Pang),
            PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, [.. body, 0]));
    }

    /// <summary>Rejects unrelated events, changed NPC sentinels and invalid seats rather than guessing attribution.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    public void NpcTutorialFinishRejectsOtherEnvelopes(string build)
    {
        foreach (var (offset, value) in new[]
        {
            (0, 1), (0, 4), (4, 1), (8, 1), (12, 0), (16, -1), (16, int.MinValue),
            (20, 1), (24, 1), (28, 1),
        })
        {
            var body = Convert.FromHexString("020000000000000000000000FFFFFFFF00000000000000000000000000000000");
            BitConverter.GetBytes(value).CopyTo(body, offset);
            Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
        }
    }

    /// <summary>Does not give unaudited native builds the NPC-seat report interpretation.</summary>
    [Theory]
    [InlineData("id-400")]
    public void NpcTutorialFinishRequiresAnExplicitNativeField(string build)
    {
        var body = Convert.FromHexString("020000000000000000000000FFFFFFFF00000000000000000000000000000000");
        Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
    }

    /// <summary>The August build uses the compact NPC report and rejects a wide finishing envelope.</summary>
    [Fact]
    public void China705RejectsTheEarlierWideNpcFinishingEnvelope()
    {
        var body = Convert.FromHexString("020000000000000000000000FFFFFFFF00000000000000000000000000000000");
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-705", body));
    }

    /// <summary>Refuses missing or sentinel aliases before publishing any tutorial report route.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{ victim: unknown_16, scorer: unknown_20 }")]
    [InlineData("{ victim: unknown_16, scorer: unknown_16, auxiliary: unknown_28 }")]
    [InlineData("{ victim: kind, scorer: unknown_20, auxiliary: unknown_28 }")]
    [InlineData("{ victim: unknown_16, scorer: unknown_8, auxiliary: unknown_28 }")]
    [InlineData("{ victim: unknown_16, scorer: unknown_12, auxiliary: unknown_28 }")]
    [InlineData("{ victim: unknown_16, scorer: unknown_24, auxiliary: unknown_28 }")]
    [InlineData("{ victim: unknown_16, scorer: unknown_32, auxiliary: unknown_28 }")]
    [InlineData("{ victim: unknown_16, scorer: missing_word, auxiliary: unknown_28 }")]
    [InlineData("{ victim: unknown_16, scorer: unknown_20, auxiliary: unknown_28, extra: unknown_24 }")]
    public void NpcTutorialFinishRejectsInvalidFieldAliases(string alias)
    {
        var directory = CopyTables();
        var path = Path.Combine(directory, "builds.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("tutorial_score_npc_finish_fields: { victim: unknown_16, scorer: unknown_20, auxiliary: unknown_28 }",
            "tutorial_score_npc_finish_fields: " + alias, StringComparison.Ordinal));
        Assert.Throws<ConfigException>(() => RoomTables.Load(directory));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(23)]
    public void UnrelatedReportsNeverAdvanceTutorialGoals(int kind)
    {
        var body = new byte[32];
        BitConverter.GetBytes(kind).CopyTo(body, 0);
        BitConverter.GetBytes(1_000_000).CopyTo(body, 8);
        Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-406", body));
    }

    [Fact]
    public void TutorialRestatementPinsEachNativeGoalFieldAndAllOtherBytes()
    {
        Assert.Equal(WriteExpectedScore(),
            PlayRoomCodec.WriteTutorialScore(TestBuilds.Packets, "cn-406", new(0, "", 13, 7, 5)));
    }

    /// <summary>Checks native narrow attribution independently of reused side-code bytes.</summary>
    [Theory]
    [InlineData("kr-812", 1, TutorialScoreEvent.Bubble)]
    [InlineData("kr-812", 22, TutorialScoreEvent.Bubble)]
    [InlineData("kr-812", 4, TutorialScoreEvent.Help)]
    [InlineData("cn-717", 1, TutorialScoreEvent.Bubble)]
    [InlineData("cn-717", 22, TutorialScoreEvent.Bubble)]
    [InlineData("cn-717", 4, TutorialScoreEvent.Help)]
    [InlineData("cn-706", 1, TutorialScoreEvent.Bubble)]
    [InlineData("cn-706", 22, TutorialScoreEvent.Bubble)]
    [InlineData("cn-706", 4, TutorialScoreEvent.Help)]
    public void NarrowFullGoalsReadTheMixedWidthScorerReference(string build, int kind, TutorialScoreEvent goal)
    {
        var body = new byte[16];
        body[0] = (byte)kind;
        BitConverter.GetBytes(1_000_001).CopyTo(body, 1);
        BitConverter.GetBytes(1_000_000).CopyTo(body, 5);
        body[9] = 254;
        body[10] = 254;
        Assert.Equal(new TutorialScoreReported(0, goal), PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
        body[0] = 2;
        Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
        body[0] = (byte)kind;
        BitConverter.GetBytes(1_000_001).CopyTo(body, 5);
        Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
    }

    /// <summary>Pins the compact finishing envelope and rejects truncation, trailing data and unrelated events.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-706")]
    public void NarrowCompactFinishPinsSixBytesAndRejectsUnrelatedKinds(string build)
    {
        byte[] body = [2, 0, 0, 0, 0, 0];
        Assert.Equal(new TutorialScoreReported(0, TutorialScoreEvent.Pang), PlayRoomCodec.ReadOptimizedTutorialScore(TestBuilds.Packets, build, body));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadOptimizedTutorialScore(TestBuilds.Packets, build, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadOptimizedTutorialScore(TestBuilds.Packets, build, [.. body, 0]));
        foreach (var kind in new byte[] { 1, 3, 4, 6, 7, 13, 15, 16, 17, 22 })
        {
            body[0] = kind;
            Assert.Null(PlayRoomCodec.ReadOptimizedTutorialScore(TestBuilds.Packets, build, body));
        }
    }

    /// <summary>Pins the full Korean tutorial scoreboard, including every vacant row and tail sentinel.</summary>
    [Fact]
    public void KoreanTutorialRestatementPinsAllNativeBytes()
        => Assert.Equal(WriteExpectedKoreanScore(), PlayRoomCodec.WriteTutorialScore(TestBuilds.Packets, "kr-812", new(0, "", 13, 7, 5)));

    /// <summary>Pins the CN2014 header, twelve native rows, tutorial goals and single-word tails.</summary>
    [Fact]
    public void Chinese2014TutorialRestatementPinsAllNativeBytes()
        => Assert.Equal(WriteExpectedChinese2014Score(),
            PlayRoomCodec.WriteTutorialScore(TestBuilds.Packets, "cn-404", new(0, "", 13, 7, 5)));

    /// <summary>The later CN2014 reader retains the same wire width despite different native row offsets.</summary>
    [Fact]
    public void China408TutorialRestatementPreservesEveryCompatibleWireByte()
        => Assert.Equal(WriteExpectedChinese2014Score(),
            PlayRoomCodec.WriteTutorialScore(TestBuilds.Packets, "cn-408", new(0, "", 13, 7, 5)));

    /// <summary>Pins the build's own native row and tail offsets separately from serialized byte positions.</summary>
    [Fact]
    public void China408ScoreMetadataMatchesItsOwnReader()
    {
        var tables = TestBuilds.Packets;
        var row = tables.Resolve("cn-408", "ScoreRow").ToDictionary(field => field.Name);
        Assert.Equal(44, row["knock_downs"].Offset);
        Assert.Equal(48, row["kills"].Offset);
        Assert.Equal(56, row["unknown_56"].Offset);
        Assert.Equal(244, row["unknown_244"].Offset);
        Assert.Equal(304, row["unknown_304"].Offset);
        Assert.Equal(308, row["unknown_308"].Offset);
        Assert.Equal("bool", row["unknown_308"].Type);
        Assert.Equal(316, row["unknown_316"].Offset);
        Assert.Equal(-1, row["unknown_316"].Default);
        Assert.Equal(356, row["unknown_356"].Offset);
        Assert.Equal(4656, tables.Resolve("cn-408", "ScoreData").Single(field => field.Name == "tails").Offset);
        Assert.Equal(4608, tables.Resolve("cn-404", "ScoreData").Single(field => field.Name == "tails").Offset);
    }

    /// <summary>Pins the August and September rows and both tail words independently of native object stride.</summary>
    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void Late2014TutorialRestatementPinsAllNativeBytes(string build)
        => Assert.Equal(WriteExpectedSeptember2014Score(),
            PlayRoomCodec.WriteTutorialScore(TestBuilds.Packets, build, new(0, "", 13, 7, 5)));

    private static byte[] WriteExpectedSeptember2014Score()
    {
        // cn-706 sub_786E90 reads a 292-byte header, twelve 245-byte empty-name rows and twelve tail pairs.
        var expected = new byte[3328];
        BitConverter.GetBytes(17).CopyTo(expected, 0);
        BitConverter.GetBytes(-1).CopyTo(expected, 12);
        BitConverter.GetBytes(1).CopyTo(expected, 292 + 16);
        BitConverter.GetBytes(13).CopyTo(expected, 292 + 20);
        BitConverter.GetBytes(7).CopyTo(expected, 292 + 24);
        BitConverter.GetBytes(5).CopyTo(expected, 292 + 32);
        expected[292 + 128] = 1;
        for (var slot = 0; slot < 12; slot++)
        {
            if (slot > 0) BitConverter.GetBytes(-1).CopyTo(expected, 292 + slot * 245);
            BitConverter.GetBytes(-1).CopyTo(expected, 292 + slot * 245 + 197);
            BitConverter.GetBytes(-1).CopyTo(expected, 3232 + slot * 8 + 4);
        }
        return expected;
    }

    /// <summary>Routes native bubble, rescue and compact NPC goals while rejecting malformed envelopes.</summary>
    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public async Task Late2014RoutesItsOwnTutorialGoalReports(string build)
    {
        var protocol = build + "/game";
        var tables = RoomTables.Load(TestBuilds.PacketsDirectory());
        var (schema, play) = tables.Of(build);
        var opcodes = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(opcodes);
        var context = fixture.Context(build, protocol);
        try
        {
            new PlayRoomCodec(protocol, build, schema, play, fixture.Services.Get<IOpcodeTableRegistry>(),
                NullLogger.Instance, tables.Tables).Mount(context);
            var session = new MatchMessagesMountTests.Session(opcodes.Profile);
            var full = opcodes.C2S("gcpGameUpdateScore");
            foreach (var kind in new byte[] { 1, 22, 4 })
            {
                var body = new byte[16];
                body[0] = kind;
                BitConverter.GetBytes(1_100_000).CopyTo(body, 1);
                BitConverter.GetBytes(1_000_000).CopyTo(body, 5);
                body[9] = 254;
                body[10] = 254;
                byte[] trailing = [.. body, 0];
                await fixture.Packets.DispatchAsync(protocol, session, new(full, body), default);
                await fixture.Packets.DispatchAsync(protocol, session, new(full, body[..^1]), default);
                await fixture.Packets.DispatchAsync(protocol, session, new(full, trailing), default);
            }
            await fixture.Packets.DispatchAsync(protocol, session,
                new(opcodes.C2S("gcpGameUpdateScoreOptimze"), new byte[] { 2, 0, 0, 0, 0, 0 }), default);
            Assert.Equal(new[]
            {
                new TutorialScoreReported(0, TutorialScoreEvent.Bubble),
                new TutorialScoreReported(0, TutorialScoreEvent.Bubble),
                new TutorialScoreReported(0, TutorialScoreEvent.Help),
                new TutorialScoreReported(0, TutorialScoreEvent.Pang),
            }, fixture.Messages.Received.OfType<TutorialScoreReported>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<TutorialScoreReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<TutorialScoreUpdated>>());
    }

    private static byte[] WriteExpectedChinese2014Score()
    {
        // cn-404 sub_7AD7F0 reads 292 header bytes, twelve 241-byte empty-name rows and twelve tail words.
        var expected = new byte[3232];
        BitConverter.GetBytes(17).CopyTo(expected, 0);
        BitConverter.GetBytes(-1).CopyTo(expected, 12);
        BitConverter.GetBytes(1).CopyTo(expected, 292 + 16);
        BitConverter.GetBytes(13).CopyTo(expected, 292 + 20);
        BitConverter.GetBytes(7).CopyTo(expected, 292 + 24);
        BitConverter.GetBytes(5).CopyTo(expected, 292 + 32);
        expected[292 + 128] = 1;
        for (var slot = 0; slot < 12; slot++)
        {
            if (slot > 0) BitConverter.GetBytes(-1).CopyTo(expected, 292 + slot * 241);
            BitConverter.GetBytes(-1).CopyTo(expected, 292 + slot * 241 + 193);
        }
        return expected;
    }

    /// <summary>Uses CN2014's own scorer reference for bubble, finishing and rescue goals.</summary>
    [Theory]
    [InlineData("cn-404", 1, TutorialScoreEvent.Bubble)]
    [InlineData("cn-404", 22, TutorialScoreEvent.Bubble)]
    [InlineData("cn-404", 2, TutorialScoreEvent.Pang)]
    [InlineData("cn-404", 4, TutorialScoreEvent.Help)]
    [InlineData("cn-408", 1, TutorialScoreEvent.Bubble)]
    [InlineData("cn-408", 22, TutorialScoreEvent.Bubble)]
    [InlineData("cn-408", 2, TutorialScoreEvent.Pang)]
    [InlineData("cn-408", 4, TutorialScoreEvent.Help)]
    public void Chinese2014GoalsUseTheFullNativeScorerReference(string build, int kind, TutorialScoreEvent goal)
    {
        var body = new byte[32];
        BitConverter.GetBytes(kind).CopyTo(body, 0);
        BitConverter.GetBytes(1_100_000).CopyTo(body, 4);
        BitConverter.GetBytes(1_000_000).CopyTo(body, 8);
        BitConverter.GetBytes(-2).CopyTo(body, 12);
        BitConverter.GetBytes(-2).CopyTo(body, 16);
        Assert.Equal(new TutorialScoreReported(0, goal),
            PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
        BitConverter.GetBytes(1_000_001).CopyTo(body, 8);
        Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body));
        Assert.Throws<PacketFormatException>(() =>
            PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, body[..^1]));
        Assert.Throws<PacketFormatException>(() =>
            PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, build, [.. body, 0]));
    }

    /// <summary>Preserves native object offsets separately from the shorter serialized row.</summary>
    [Fact]
    public void Chinese2014ScoreMetadataNamesItsOwnNativeOffsets()
    {
        var packets = TestBuilds.Packets;
        var envelope = packets.Resolve("cn-404", "ScoreData").ToDictionary(field => field.Name);
        Assert.Equal(304, envelope["unknown_304"].Offset);
        Assert.Equal(320, envelope["unknown_320"].Offset);
        Assert.Equal(296, envelope["winner_team"].Offset);
        Assert.Equal(300, envelope["unknown_300"].Offset);
        Assert.Equal(336, envelope["rows"].Offset);
        Assert.Equal(4608, envelope["tails"].Offset);
        var row = packets.Resolve("cn-404", "ScoreRow").ToDictionary(field => field.Name);
        Assert.Equal(44, row["knock_downs"].Offset);
        Assert.Equal(48, row["kills"].Offset);
        Assert.Equal(56, row["unknown_56"].Offset);
        Assert.Equal(204, row["badge_images"].Offset);
        Assert.Equal(176, row["unknown_176"].Offset);
        Assert.Equal(240, row["unknown_240"].Offset);
        Assert.Equal(312, row["unknown_312"].Offset);
        Assert.Equal(-1, row["unknown_312"].Default);
        Assert.Equal(352, row["unknown_352"].Offset);
    }

    private static byte[] WriteExpectedKoreanScore()
    {
        // kr-812 sub_417720: 292-byte header, sixteen 255-byte empty-name rows and sixteen tail pairs.
        var expected = new byte[4500];
        BitConverter.GetBytes(17).CopyTo(expected, 0);
        BitConverter.GetBytes(-1).CopyTo(expected, 12);
        BitConverter.GetBytes(1).CopyTo(expected, 292 + 16);
        BitConverter.GetBytes(13).CopyTo(expected, 292 + 20);
        BitConverter.GetBytes(7).CopyTo(expected, 292 + 24);
        BitConverter.GetBytes(5).CopyTo(expected, 292 + 32);
        expected[292 + 128] = 1;
        BitConverter.GetBytes(2).CopyTo(expected, 292 + 197);
        for (var slot = 0; slot < 16; slot++)
        {
            if (slot > 0)
            {
                BitConverter.GetBytes(-1).CopyTo(expected, 292 + slot * 255);
                BitConverter.GetBytes(-1).CopyTo(expected, 292 + slot * 255 + 197);
            }
            BitConverter.GetBytes(-1).CopyTo(expected, 292 + slot * 255 + 201);
            BitConverter.GetBytes(-1).CopyTo(expected, 4372 + slot * 8 + 4);
        }
        return expected;
    }

    /// <summary>Pins the CN2015 score reply without the later Korean header vectors.</summary>
    [Fact]
    public void Chinese2015TutorialRestatementPinsAllNativeBytes()
        => Assert.Equal(WriteExpectedChinese2015Score(), PlayRoomCodec.WriteTutorialScore(TestBuilds.Packets, "cn-717", new(0, "", 13, 7, 5)));

    private static byte[] WriteExpectedChinese2015Score()
    {
        // cn-717 sub_4195B0 reads a 284-byte header, sixteen 255-byte empty-name rows and sixteen tail pairs.
        var expected = new byte[4492];
        BitConverter.GetBytes(17).CopyTo(expected, 0);
        BitConverter.GetBytes(-1).CopyTo(expected, 4);
        BitConverter.GetBytes(1).CopyTo(expected, 284 + 16);
        BitConverter.GetBytes(13).CopyTo(expected, 284 + 20);
        BitConverter.GetBytes(7).CopyTo(expected, 284 + 24);
        BitConverter.GetBytes(5).CopyTo(expected, 284 + 32);
        expected[284 + 128] = 1;
        BitConverter.GetBytes(2).CopyTo(expected, 284 + 197);
        for (var slot = 0; slot < 16; slot++)
        {
            if (slot > 0)
            {
                BitConverter.GetBytes(-1).CopyTo(expected, 284 + slot * 255);
                BitConverter.GetBytes(-1).CopyTo(expected, 284 + slot * 255 + 197);
            }
            BitConverter.GetBytes(-1).CopyTo(expected, 284 + slot * 255 + 201);
            BitConverter.GetBytes(-1).CopyTo(expected, 4364 + slot * 8 + 4);
        }
        return expected;
    }

    /// <summary>Pins CN2012 goals to its own shorter row, including the independent rescue position.</summary>
    [Fact]
    public void EarlyTutorialRestatementPinsAllNativeBytes()
        => Assert.Equal(WriteExpectedEarlyScore(), PlayRoomCodec.WriteTutorialScore(TestBuilds.Packets, "cn-324", new(0, "", 13, 7, 5)));

    /// <summary>Checks the early eight-word report against its own scorer reference and NPC side words.</summary>
    [Theory]
    [InlineData(1, TutorialScoreEvent.Bubble)]
    [InlineData(22, TutorialScoreEvent.Bubble)]
    [InlineData(2, TutorialScoreEvent.Pang)]
    [InlineData(4, TutorialScoreEvent.Help)]
    public void EarlyTutorialGoalsUseFullScorerReference(int kind, TutorialScoreEvent goal)
    {
        var body = new byte[32];
        BitConverter.GetBytes(kind).CopyTo(body, 0);
        BitConverter.GetBytes(1_000_001).CopyTo(body, 4);
        BitConverter.GetBytes(1_000_000).CopyTo(body, 8);
        BitConverter.GetBytes(-2).CopyTo(body, 12);
        BitConverter.GetBytes(-2).CopyTo(body, 16);
        Assert.Equal(new TutorialScoreReported(0, goal), PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-324", body));
        BitConverter.GetBytes(1_000_001).CopyTo(body, 8);
        Assert.Null(PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-324", body));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-324", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadTutorialScore(TestBuilds.Packets, "cn-324", [.. body, 0]));
    }

    private static byte[] WriteExpectedEarlyScore()
    {
        // cn-324 sub_6E9EF0: 196-byte header, eight 221-byte empty-name rows and eight tail words.
        var expected = new byte[1996];
        BitConverter.GetBytes(16).CopyTo(expected, 0);
        BitConverter.GetBytes(1).CopyTo(expected, 196 + 16);
        BitConverter.GetBytes(13).CopyTo(expected, 196 + 20);
        BitConverter.GetBytes(7).CopyTo(expected, 196 + 24);
        BitConverter.GetBytes(5).CopyTo(expected, 196 + 32);
        expected[196 + 128] = 1;
        BitConverter.GetBytes(2).CopyTo(expected, 196 + 193);
        for (var slot = 1; slot < 8; slot++)
        {
            BitConverter.GetBytes(-1).CopyTo(expected, 196 + slot * 221);
            BitConverter.GetBytes(-1).CopyTo(expected, 196 + slot * 221 + 193);
        }
        return expected;
    }

    private static byte[] WriteExpectedScore()
    {
        // cn-406 sub_6FF0A0: header 276 bytes, eight 221-byte empty-name rows, eight tail words.
        var expected = new byte[2076];
        BitConverter.GetBytes(16).CopyTo(expected, 0);
        BitConverter.GetBytes(-1).CopyTo(expected, 12);
        BitConverter.GetBytes(1).CopyTo(expected, 276 + 16);
        BitConverter.GetBytes(13).CopyTo(expected, 276 + 20);
        BitConverter.GetBytes(7).CopyTo(expected, 276 + 24);
        BitConverter.GetBytes(5).CopyTo(expected, 276 + 32);
        expected[276 + 128] = 1;
        for (var slot = 0; slot < 8; slot++)
        {
            if (slot > 0) BitConverter.GetBytes(-1).CopyTo(expected, 276 + slot * 221);
            BitConverter.GetBytes(-1).CopyTo(expected, 276 + slot * 221 + 193);
        }
        return expected;
    }
}
