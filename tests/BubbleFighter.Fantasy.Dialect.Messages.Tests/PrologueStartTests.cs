using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises the KR2015 prologue mode and its one-seat loading release through the real tutorial handler.</summary>
public sealed partial class PrologueStartTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public async Task WaveTableReachesHostAndPeerExactlyOnceInNativeOrder(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        var host = new TutorialSession(build);
        var peer = new TutorialSession(build);
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play, new TutorialTables(table),
            NullLogger.Instance, TestBuilds.Packets);
        var router = new TutorialLifecycleRouter(peer, codec);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(table), NullLogger.Instance,
            oneSeatWaves: play.OneSeatWaves, messages: router);
        // The bounded table is forwarded without reinterpretation.
        var body = Convert.FromHexString("010000000100030000000400000000000000040000002E00440061006F0000000000000000000101F4010000040000002E00440061006F0000000000000000000102E8030000040000002E00440061006F0000000000000000000103DC050000040000002E00440061006F0000000000000000000104");
        await handler.HandleWaveAllSpawnMobsAsync(host, new(908, body), default);
        Assert.Equal(118, body.Length);
        foreach (var session in new[] { host, peer })
        {
            Assert.Equal(new ushort[] { 908, 909 }, session.Sent.Select(item => item.Opcode));
            Assert.Empty(session.Sent[0].Payload);
            Assert.Equal(body, session.Sent[1].Payload);
        }
        var report = Assert.IsType<RoundNpcReported>(Assert.Single(router.Messages));
        Assert.Equal(RoundNpcOperation.SpawnTable, report.Operation);
        Assert.Equal(body, report.Payload.ToArray());
    }

    [Theory]
    [InlineData("kr-812", 3)]
    [InlineData("cn-717", 3)]
    [InlineData("cn-324", 2)]
    [InlineData("cn-279", 2)]
    [InlineData("id-400", 2)]
    [InlineData("id-1809", 3)]
    [InlineData("cn-705", 3)]
    [InlineData("cn-706", 3)]
    public async Task SharedStartSenderPreservesTutorialGatesAndOrdinaryDefaults(string build, int fields)
    {
        var session = new TutorialSession();
        var table = new TutorialTable();
        await RoundStartSender.SendCountdownAsync(TestBuilds.Packets, build, table, true, false, session,
            new RoundCountdownStarted(1, 4000) { IsTutorial = true }, default);
        await RoundStartSender.SendStartedAsync(TestBuilds.Packets, build, table, session,
            new RoundStarted(1, 99999000) { IsTutorial = true }, default);
        Assert.Equal(new ushort[] { 702, 703, 704 }, session.Sent.Select(item => item.Opcode));
        Assert.Equal(1, BitConverter.ToInt32(session.Sent[0].Payload));
        Assert.Equal(fields * 4, session.Sent[0].Payload.Length);
        Assert.Equal(4000, BitConverter.ToInt32(session.Sent[1].Payload));
        Assert.Equal(fields == 3 ? new[] { 99999000, 0, 99999000 } : new[] { 99999000, 0 },
            Enumerable.Range(0, fields).Select(i => BitConverter.ToInt32(session.Sent[2].Payload, i * 4)));

        session.Sent.Clear();
        await RoundStartSender.SendCountdownAsync(TestBuilds.Packets, build, table, true, false, session,
            new RoundCountdownStarted(1, 3000), default);
        await RoundStartSender.SendStartedAsync(TestBuilds.Packets, build, table, session,
            new RoundStarted(1, 240000), default);
        Assert.Equal(new ushort[] { 702, 705, 703, 704 }, session.Sent.Select(item => item.Opcode));
        Assert.All(session.Sent[0].Payload, value => Assert.Equal(0, value));
        Assert.Equal(fields == 3 ? new[] { 240000, 0, 0 } : new[] { 240000, 0 },
            Enumerable.Range(0, fields).Select(i => BitConverter.ToInt32(session.Sent[3].Payload, i * 4)));

        // With the relay offered, the first word selects it unless this seat fell back to its game connection.
        session.Sent.Clear();
        await RoundStartSender.SendCountdownAsync(TestBuilds.Packets, build, table, true, true, session,
            new RoundCountdownStarted(1, 3000), default);
        Assert.Equal(1, BitConverter.ToInt32(session.Sent[0].Payload));
        Assert.All(session.Sent[0].Payload.Skip(4), value => Assert.Equal(0, value));
        session.Sent.Clear();
        await RoundStartSender.SendCountdownAsync(TestBuilds.Packets, build, table, true, true, session,
            new RoundCountdownStarted(1, 3000) { MatchOverGame = true }, default);
        Assert.All(session.Sent[0].Payload, value => Assert.Equal(0, value));
    }

    /// <summary>
    /// A numbered tutorial-room stage (MapMeta stageNum above 0) starts in mode 15, the SinglePlayGameStage mode
    /// (kr-812 sub_8E1240), and finishes on its own stage end instead of running the prologue's waves.
    /// </summary>
    [Fact]
    public async Task Kr812MissionStageStartsAsASinglePlayStage()
    {
        var (schema, play) = TestBuilds.Of("kr-812");
        var table = new TutorialTable();
        var session = new TutorialSession();
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, "kr-812", schema, play.Tutorial, play.MatchStart,
            play.Wire, new TutorialTables(table), NullLogger.Instance, oneSeatWaves: play.OneSeatWaves,
            messages: lifecycle, sharedLifecycle: true, resultMs: 200);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        const int clock = 90000;
        for (var i = 0; i < 13; i++) writer.WriteInt32(i == 8 ? clock : 0);
        for (var i = 0; i < 6; i++) writer.WriteBool32(false);
        writer.WriteWideStringPrefixed("rel_mission_wall");
        writer.WriteWideStringPrefixed("");
        writer.WriteWideStringPrefixed("sin_mission_wall_lv_01");
        writer.WriteInt32(1);

        await handler.HandlePlayTutorialAsync(session, new InboundPacket(107, buffer.WrittenMemory), default);

        var room = Assert.Single(session.Sent, item => item.Opcode == 110);
        Assert.Equal(15, BitConverter.ToInt32(room.Payload, 28));
        Assert.Equal(new ushort[] { 110, 505, 503, 510 }, session.Sent.Select(item => item.Opcode));
        var opened = Assert.IsType<TutorialRoundOpened>(lifecycle.Messages[0]);
        Assert.Equal(15, opened.ModeId);
        Assert.Equal("sin_mission_wall_lv_01", opened.MapKey);
        Assert.True(opened.NativeSupplies);
        session.Sent.Clear();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await handler.TryHandleEndStageAsync(session, new InboundPacket(724, new byte[] { 1, 0, 0, 0 }), default));
        // The result stays up for the configured time before the menu replaces the stage.
        Assert.True(timer.ElapsedMilliseconds >= 190);
        // Game end, then the scoreboard that shows the stage's clear panel, then back to the tutorial room; never the
        // gspGameShowResult_ that opens the 3D result ceremony (kr-812 sub_803DF0, sub_5ACB20, sub_5AD030).
        Assert.Equal(new ushort[] { 711, 732, 110 }, session.Sent.Select(item => item.Opcode));
        // The clear reaches the round owner before the round is closed, so it can be timed and stored.
        var cleared = lifecycle.Messages.FindIndex(message => message is TutorialStageCleared { MapKey: "sin_mission_wall_lv_01" });
        Assert.True(cleared >= 0);
        Assert.IsType<TutorialRoundClosed>(lifecycle.Messages[cleared + 1]);
        Assert.DoesNotContain(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
    }

    /// <summary>The stage list carries the player's stored clears as stage, best count and best seconds.</summary>
    [Fact]
    public async Task Kr812StageListCarriesTheStoredClears()
    {
        var (schema, play) = TestBuilds.Of("kr-812");
        var stages = new StoredStages(12, [new(4, 25, 0), new(16, 0, 95)]);
        var session = new TutorialSession();
        using var handler = new TutorialHandler(TestBuilds.Packets, "kr-812", schema, play.Tutorial, play.MatchStart,
            play.Wire, new TutorialTables(new TutorialTable()), NullLogger.Instance, oneSeatWaves: play.OneSeatWaves, stages: stages);
        await handler.HandleSingleStageInfoAsync(session, new InboundPacket(920, ReadOnlyMemory<byte>.Empty), default);
        var body = Assert.Single(session.Sent).Payload;
        Assert.Equal(TutorialWire.WriteSingleStageInfo(TestBuilds.Packets, "kr-812",
            [new SingleStageClearInfo(4, 25, 0), new SingleStageClearInfo(16, 0, 95)]), body);
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void EveryWaveTutorialBuildKeepsThePrologueAsItsOnlyWaveMap(string build)
    {
        var (_, play) = TestBuilds.Of(build);
        if (play.OneSeatWaves)
            Assert.Equal("rel_prologue_01", play.Tutorial.WaveMapKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kr812PrologueUsesNpcModeAndReleasesItsOwnLoadingGate(bool sharedLifecycle)
    {
        var (schema, play) = TestBuilds.Of("kr-812");
        var table = new TutorialTable();
        var session = new TutorialSession();
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, "kr-812", schema, play.Tutorial, play.MatchStart,             play.Wire, new TutorialTables(table), NullLogger.Instance, oneSeatWaves: play.OneSeatWaves,
            messages: lifecycle, sharedLifecycle: sharedLifecycle);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        const int clock = 99999000;
        for (var i = 0; i < 13; i++) writer.WriteInt32(i == 8 ? clock : 0);
        for (var i = 0; i < 6; i++) writer.WriteBool32(false);
        writer.WriteWideStringPrefixed("rel_prologue_01");
        writer.WriteWideStringPrefixed("prologue");
        writer.WriteWideStringPrefixed("rel_prologue_01");
        writer.WriteInt32(1);

        await handler.HandlePlayTutorialAsync(session, new InboundPacket(107, buffer.WrittenMemory), default);

        var room = Assert.Single(session.Sent, item => item.Opcode == 110);
        Assert.Equal(103, BitConverter.ToInt32(room.Payload, 28));
        Assert.Equal(new ushort[] { 110, 505, 503, 510 }, session.Sent.Select(item => item.Opcode));
        Assert.Equal(58, session.Sent[^1].Payload.Length);
        var messagesBeforeWave = lifecycle.Messages.Count;
        var framesBeforeWave = session.Sent.Count;
        await handler.HandleWaveAllSpawnMobsAsync(session, new(908, new byte[] { 1 }), default);
        Assert.Equal(messagesBeforeWave, lifecycle.Messages.Count);
        Assert.Equal(new ushort[] { 908, 909 }, session.Sent.Skip(framesBeforeWave).Select(item => item.Opcode));
        Assert.Empty(session.Sent[^2].Payload);
        Assert.Equal(new byte[] { 1 }, session.Sent[^1].Payload);
        session.Sent.RemoveRange(framesBeforeWave, 2);
        var loading = new InboundPacket(701, new byte[16]);
        Assert.True(await handler.TryHandleMapLoadingCompleteAsync(session, loading, default));
        if (sharedLifecycle)
        {
            var opened = Assert.IsType<TutorialRoundOpened>(lifecycle.Messages[0]);
            Assert.Equal(103, opened.ModeId);
            Assert.Equal("rel_prologue_01", opened.MapKey);
            Assert.Equal(clock, opened.TimeLimitMs);
            Assert.Equal(4000, opened.CountdownMs);
            Assert.False(opened.NativeSupplies);
            Assert.IsType<RoundLoadReported>(lifecycle.Messages[1]);
            Assert.Equal(4, session.Sent.Count);
            Assert.False(await handler.TryHandleMapLoadingCompleteAsync(session, loading, default));
            await handler.ClearTutorialStateAsync(session, default);
            Assert.IsType<TutorialRoundClosed>(lifecycle.Messages[2]);
            return;
        }
        Assert.Equal(new ushort[] { 701, 702, 703, 704 }, session.Sent.TakeLast(4).Select(item => item.Opcode));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, session.Sent[^3].Payload);
        Assert.Equal(new[] { clock, 0, clock },
            Enumerable.Range(0, 3).Select(i => BitConverter.ToInt32(session.Sent[^1].Payload, i * 4)));
        Assert.False(await handler.TryHandleMapLoadingCompleteAsync(session, loading, default));
    }

    /// <summary>An explicitly configured callback waits for its matching map before opening the shared tutorial round.</summary>
    [Theory]
    [InlineData("cn-705", 103, 54, 8, 4)]
    public async Task ConfiguredCallbackTutorialAdmitsOnlyAtCallbackAndSharesLoadingAndCompletion(
        string build, int mode, int enterBytes, int suffixOffset, int suffixWord)
    {
        var (schema, play) = TestBuilds.Of(build);
        Assert.Equal(mode == 103, play.OneSeatWaves);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            TutorialMatchStart.RepAutoStart, play.Wire, new TutorialTables(new TutorialTable()),
            NullLogger.Instance, oneSeatWaves: play.OneSeatWaves, messages: lifecycle, sharedLifecycle: true);
        const string map = "rel_prologue_01";
        var playBody = PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest",
            new Dictionary<string, object?>
            {
                ["time_attack_time_limit_ms"] = 99999000,
                ["difficulty"] = 1,
            }.Concat(TestBuilds.Packets.Resolve(build, "TutorialPlayRequest")
                .Where(field => field.Type == "wstr")
                .Select(field => new KeyValuePair<string, object?>(field.Name, map)))
                .ToDictionary(pair => pair.Key, pair => pair.Value));
        await handler.HandlePlayTutorialAsync(session, new(107, playBody), default);
        Assert.Empty(lifecycle.Messages);
        Assert.Equal(new ushort[] { 110, 505, 503, 520 }, session.Sent.Select(item => item.Opcode));
        Assert.Equal(mode, BitConverter.ToInt32(session.Sent[1].Payload));
        Assert.False(await handler.TryHandleMapLoadingCompleteAsync(session, new(701, new byte[16]), default));
        var wrongMap = PacketCodec.Write(TestBuilds.Packets, build, "RoomEnterGameRequest",
            new Dictionary<string, object?> { ["map_key"] = "another_map" });
        await Assert.ThrowsAsync<PacketFormatException>(async () =>
            await handler.TryHandleEnterGameAsync(session, new(503, wrongMap), default));
        Assert.Empty(lifecycle.Messages);
        var enter = new InboundPacket(503, PacketCodec.Write(TestBuilds.Packets, build, "RoomEnterGameRequest",
            new Dictionary<string, object?> { ["map_key"] = map }));
        Assert.True(await handler.TryHandleEnterGameAsync(session, enter, default));
        var opened = Assert.IsType<TutorialRoundOpened>(Assert.Single(lifecycle.Messages));
        Assert.Equal(mode, opened.ModeId);
        Assert.Equal(map, opened.MapKey);
        Assert.Equal(!play.OneSeatWaves, opened.NativeSupplies);
        Assert.Equal(99999000, opened.TimeLimitMs);
        Assert.Equal(510, session.Sent[^1].Opcode);
        Assert.Equal(enterBytes, session.Sent[^1].Payload.Length);
        // cn-406 sub_700680 ends at the first suffix word; cn-705/cn-706 keep the third word and one bool32.
        Assert.Equal(suffixWord, BitConverter.ToInt32(session.Sent[^1].Payload, session.Sent[^1].Payload.Length - suffixOffset));
        Assert.True(await handler.TryHandleEnterGameAsync(session, enter, default));
        Assert.Equal(5, session.Sent.Count);
        Assert.Single(lifecycle.Messages);
        Assert.True(await handler.TryHandleMapLoadingCompleteAsync(session, new(701, new byte[16]), default));
        Assert.IsType<RoundLoadReported>(lifecycle.Messages[1]);
        Assert.Equal(5, session.Sent.Count);
        if (play.OneSeatWaves)
        {
            Assert.True(await handler.TryHandleEndStageAsync(session, new(724, new byte[4]), default));
            Assert.Single(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
            Assert.Single(lifecycle.Messages, message => message is TutorialRoundClosed);
            Assert.False(await handler.TryHandleEnterGameAsync(session, enter, default));
            return;
        }
        Assert.True(await handler.TryHandleEndStageAsync(session, new(724, new byte[4]), default));
        Assert.IsType<TutorialRoundClosed>(lifecycle.Messages[2]);
        // A single-play stage end shows the stage's own clear or fail panel before the tutorial room, never the ceremony.
        Assert.Equal(new ushort[] { 711, 732, 110 }, session.Sent.TakeLast(3).Select(item => item.Opcode));
        Assert.DoesNotContain(session.Sent, item => item.Opcode == 733);
        Assert.False(await handler.TryHandleEnterGameAsync(session, enter, default));
    }

    /// <summary>Leaving an explicitly configured callback tutorial removes the pending admission.</summary>
    [Theory]
    [InlineData("cn-705")]
    public async Task LeavingConfiguredCallbackTutorialClearsItsPendingCallback(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            TutorialMatchStart.RepAutoStart, play.Wire, new TutorialTables(new TutorialTable()),
            NullLogger.Instance, messages: lifecycle, sharedLifecycle: true);
        var fields = TestBuilds.Packets.Resolve(build, "TutorialPlayRequest");
        var values = fields.Where(field => field.Type == "wstr")
            .ToDictionary(field => field.Name, _ => (object?)"rel_prologue_01");
        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);
        Assert.True(await handler.TryHandleLeaveAsync(session, new(407, ReadOnlyMemory<byte>.Empty), default));
        Assert.Equal(new ushort[] { 110, 108 }, session.Sent.TakeLast(2).Select(item => item.Opcode));
        Assert.IsType<TutorialRoundClosed>(Assert.Single(lifecycle.Messages));
        Assert.False(await handler.TryHandleEnterGameAsync(session, new(503, ReadOnlyMemory<byte>.Empty), default));
    }

    /// <summary>CN2011 reports successful practice before cleanup; dismissal alone never reports a clear.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cn279PracticeClearIsSeparateFromDismissedOnboarding(bool completed)
    {
        const string build = "cn-279";
        var (schema, play) = TestBuilds.Of(build);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(new TutorialTable()), NullLogger.Instance,
            scoreRowFields: schema.ScoreRow, score: play.Score, messages: lifecycle, sharedLifecycle: true);
        var body = PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest",
            new Dictionary<string, object?> { ["map_key"] = "rel_Tutorial_01" });
        await handler.HandlePlayTutorialAsync(session, new(107, body), default);
        Assert.True(await handler.TryHandleEndStageAsync(session, new(723, BitConverter.GetBytes(completed ? 1 : 0)), default));
        Assert.Single(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
        var clear = lifecycle.Messages.FindIndex(message => message is TutorialStageCleared { MapKey: "rel_Tutorial_01" });
        var closed = lifecycle.Messages.FindIndex(message => message is TutorialRoundClosed);
        if (completed) Assert.InRange(clear, 0, closed - 1);
        else Assert.Equal(-1, clear);
    }

    /// <summary>Leaving the initial tutorial early records it as done, so the next login does not offer it again.</summary>
    [Theory]
    [InlineData("cn-324")]
    public async Task LeavingTheInitialTutorialEndsItsOffer(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        Assert.NotEqual(TutorialMatchStart.RepAutoStart, play.MatchStart);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(new TutorialTable()),
            NullLogger.Instance, outcomeFirst: play.Score.OutcomeFirst,
            scoreRowFields: schema.ScoreRow, score: play.Score, messages: lifecycle, sharedLifecycle: true);
        var values = TestBuilds.Packets.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr")
            .ToDictionary(field => field.Name, _ => (object?)play.Tutorial.OldMapKey);
        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);
        Assert.DoesNotContain(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
        // The ordinary room flow still owns this build's leave; the tutorial only records the end of its offer.
        Assert.False(await handler.TryHandleLeaveAsync(session, new(407, ReadOnlyMemory<byte>.Empty), default));
        Assert.Single(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
    }

    /// <summary>Audited immediate-entry clients open the prologue after the room burst and share loading admission.</summary>
    [Theory]
    [InlineData("cn-406", 2118)]
    [InlineData("id-400", 2302)]
    [InlineData("id-1809", 54)]
    [InlineData("cn-705", 54)]
    [InlineData("cn-706", 54)]
    public async Task PrologueStartsWithoutTheAutomaticStartCountdown(string build, int enterLength)
    {
        var (schema, play) = TestBuilds.Of(build);
        Assert.Equal(TutorialMatchStart.RoomEnterGame, play.MatchStart);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(new TutorialTable()), NullLogger.Instance,
            oneSeatWaves: play.OneSeatWaves, messages: lifecycle, sharedLifecycle: true);
        var values = new Dictionary<string, object?>
        {
            ["time_attack_time_limit_ms"] = 99999000,
            ["difficulty"] = 1,
        };
        foreach (var field in TestBuilds.Packets.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr"))
            values[field.Name] = "rel_prologue_01";

        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);

        Assert.Equal(new ushort[] { 110, 505, 503, 510 }, session.Sent.Select(item => item.Opcode));
        Assert.Equal(103, BitConverter.ToInt32(session.Sent[1].Payload));
        Assert.Equal(enterLength, session.Sent[^1].Payload.Length);
        var opened = Assert.IsType<TutorialRoundOpened>(Assert.Single(lifecycle.Messages));
        Assert.Equal(103, opened.ModeId);
        Assert.Equal(99999000, opened.TimeLimitMs);
        Assert.True(await handler.TryHandleMapLoadingCompleteAsync(session, new(701, new byte[16]), default));
        Assert.IsType<RoundLoadReported>(lifecycle.Messages[1]);
    }

    /// <summary>The August client's selected tutorial opens immediately and records one stage clear through the shared lifecycle.</summary>
    [Fact]
    public async Task China705SelectedTutorialEntersLoadsAndClearsWithoutACallback()
    {
        const string build = "cn-705";
        const string map = "sin_mission_wall_lv_01";
        var (schema, play) = TestBuilds.Of(build);
        Assert.Equal(TutorialMatchStart.RoomEnterGame, play.MatchStart);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(new TutorialTable()), NullLogger.Instance,
            oneSeatWaves: play.OneSeatWaves, messages: lifecycle, sharedLifecycle: true, resultMs: 1);
        var values = new Dictionary<string, object?>
        {
            ["time_attack_time_limit_ms"] = 90000,
            ["difficulty"] = 1,
        };
        foreach (var field in TestBuilds.Packets.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr"))
            values[field.Name] = map;
        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);
        Assert.Equal(new ushort[] { 110, 505, 503, 510 }, session.Sent.Select(frame => frame.Opcode));
        var opened = Assert.IsType<TutorialRoundOpened>(Assert.Single(lifecycle.Messages));
        Assert.Equal(15, opened.ModeId);
        Assert.Equal(map, opened.MapKey);
        Assert.Equal(90000, opened.TimeLimitMs);
        Assert.True(opened.NativeSupplies);
        Assert.True(await handler.TryHandleMapLoadingCompleteAsync(session, new(701, new byte[16]), default));
        Assert.IsType<RoundLoadReported>(lifecycle.Messages[1]);
        Assert.False(await handler.TryHandleMapLoadingCompleteAsync(session, new(701, new byte[16]), default));
        session.Sent.Clear();
        Assert.True(await handler.TryHandleEndStageAsync(session, new(724, new byte[] { 1, 0, 0, 0 }), default));
        Assert.Equal(new ushort[] { 711, 732, 110 }, session.Sent.Select(frame => frame.Opcode));
        Assert.Single(lifecycle.Messages, message => message is TutorialStageCleared { MapKey: map });
        Assert.Single(lifecycle.Messages, message => message is TutorialRoundClosed);
        Assert.DoesNotContain(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
        Assert.False(await handler.TryHandleEndStageAsync(session, new(724, new byte[] { 1, 0, 0, 0 }), default));
        Assert.False(await handler.TryHandleLeaveAsync(session, new(407, ReadOnlyMemory<byte>.Empty), default));
    }

    /// <summary>Native wave completion and confirmed exit finish onboarding once without clearing a numbered stage.</summary>
    [Theory]
    [InlineData("cn-406", 0)]
    [InlineData("cn-406", 1)]
    [InlineData("cn-406", 2)]
    [InlineData("kr-812", 0)]
    [InlineData("id-400", 0)]
    [InlineData("id-1809", 0)]
    [InlineData("cn-705", 0)]
    [InlineData("cn-705", 1)]
    [InlineData("cn-705", 2)]
    [InlineData("cn-706", 0)]
    [InlineData("cn-706", 1)]
    [InlineData("cn-706", 2)]
    public async Task WaveTutorialCompletionAndExitEndTheInitialOfferOnce(string build, int exitKind)
    {
        var (schema, play) = TestBuilds.Of(build);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        var profile = session.Profile;
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(table), NullLogger.Instance,
            oneSeatWaves: play.OneSeatWaves, outcomeFirst: play.Score.OutcomeFirst,
            messages: lifecycle, sharedLifecycle: true);
        var values = new Dictionary<string, object?> { ["time_attack_time_limit_ms"] = 99999000, ["difficulty"] = 1 };
        foreach (var field in TestBuilds.Packets.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr"))
            values[field.Name] = "rel_prologue_01";
        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);
        session.Sent.Clear();
        if (exitKind == 0)
            await handler.HandleEndAllWaveAsync(session, new(905, ReadOnlyMemory<byte>.Empty), default);
        else if (exitKind == 1)
            Assert.True(await handler.TryHandleEndStageAsync(session, new(724, new byte[4]), default));
        else
            Assert.True(await handler.TryHandleLeaveAsync(session, new(407, ReadOnlyMemory<byte>.Empty), default));
        Assert.Single(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
        Assert.Single(lifecycle.Messages, message => message is TutorialRoundClosed);
        Assert.DoesNotContain(lifecycle.Messages, message => message is TutorialStageCleared);
        Assert.Equal(new[] { table.S2C("gspGameEndGame"), table.S2C("gspGotoRoom") }, session.Sent.Select(frame => frame.Opcode));
        Assert.Equal(exitKind == 0 ? 3 : 2, BitConverter.ToInt32(session.Sent[0].Payload, play.Score.OutcomeFirst ? 0 : 4));
        Assert.False(await handler.TryHandleEndStageAsync(session, new(724, new byte[] { 1, 0, 0, 0 }), default));
        Assert.False(await handler.TryHandleLeaveAsync(session, new(407, ReadOnlyMemory<byte>.Empty), default));
        Assert.Single(lifecycle.Messages, message => message is BubbleFighter.Fantasy.Contracts.Lobby.InitialTutorialCompleted);
    }

    /// <summary>The menu a tutorial returns to seats the worn character, not the Dao placeholder.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public async Task LeavingTutorialReturnsTheWornCharacterToTheMenu(string build)
    {
        const int lavi = 300012;
        var (schema, play) = TestBuilds.Of(build);
        var session = new TutorialSession(build);
        var bag = new WornBag([new(12, InventoryBag.CharacterBag, 1, lavi, 1, InventorySlotRecord.WornFlagBit),
            new(12, InventoryBag.CharacterBag, 2, 201001, 1, InventorySlotRecord.WornFlagBit)]);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(new TutorialTable()), NullLogger.Instance,
            oneSeatWaves: play.OneSeatWaves, messages: new TutorialLifecycleRouter(), inventory: bag,
            sharedLifecycle: true);
        var values = new Dictionary<string, object?>
        {
            ["time_attack_time_limit_ms"] = 99999000,
            ["difficulty"] = 1,
        };
        foreach (var field in TestBuilds.Packets.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr"))
            values[field.Name] = "rel_prologue_01";
        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);
        session.Sent.Clear();

        if (play.MatchStart == TutorialMatchStart.RepAutoStart)
            Assert.True(await handler.TryHandleLeaveAsync(session, new(407, ReadOnlyMemory<byte>.Empty), default));
        else
            await handler.HandleEndAllWaveAsync(session, new(911, ReadOnlyMemory<byte>.Empty), default);

        var menu = Assert.Single(session.Sent, item => item.Opcode == 110).Payload;
        Assert.Contains(BitConverter.GetBytes(lavi), Windows(menu));
        Assert.DoesNotContain(BitConverter.GetBytes(300001), Windows(menu));
    }

    private static IEnumerable<byte[]> Windows(byte[] body) =>
        Enumerable.Range(0, body.Length - 3).Select(i => body[i..(i + 4)]);

    /// <summary>
    /// A tutorial bypasses the ordinary local queue (kr-812 sub_7FF340, cn-717 sub_882D40, id-1809 sub_7A9BD0) and
    /// still requires its group acknowledgement.
    /// </summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    public async Task QueuedBuildTutorialRetainsSpawnGroupEchoUntilItLeavesTheTutorial(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        var session = new TutorialSession(build);
        var lifecycle = new TutorialLifecycleRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(new TutorialTable()), NullLogger.Instance,
            oneSeatWaves: play.OneSeatWaves, messages: lifecycle, sharedLifecycle: true);
        var values = new Dictionary<string, object?> { ["time_attack_time_limit_ms"] = 99999000, ["difficulty"] = 1 };
        foreach (var field in TestBuilds.Packets.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr"))
            values[field.Name] = "rel_prologue_01";
        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);
        session.Sent.Clear();
        var group = new InboundPacket(32857, NpcWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 2));
        await handler.HandleCreateMobsFromSpawnGroupAsync(session, group, default);
        Assert.Equal((ushort)32857, Assert.Single(session.Sent).Opcode);
        Assert.Equal(group.Payload.ToArray(), session.Sent[0].Payload);
        await handler.ClearTutorialStateAsync(session, default);
        session.Sent.Clear();
        await handler.HandleCreateMobsFromSpawnGroupAsync(session, group, default);
        Assert.Empty(session.Sent);
    }

    private sealed class WornBag(IReadOnlyList<InventorySlotRecord> rows) : IInventoryRepository
    {
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(bag == InventoryBag.CharacterBag ? rows : []);
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            throw new NotSupportedException();
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TutorialLifecycleRouter(TutorialSession? peer = null,
        IMessageSender<RoundNpcAcknowledged>? npc = null) : IMessageRouter
    {
        public List<IMessage> Messages { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public async ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Assert.Equal(session.Profile.Id + "/game", protocol);
            Messages.Add(message);
            if (peer is not null && message is RoundFeedSent feed)
                await peer.SendAsync(feed.Code, feed.Payload, ct);
            if (peer is not null && npc is not null && message is RoundNpcReported report)
            {
                var reply = new RoundNpcAcknowledged(report.Operation, report.Payload);
                await npc.SendAsync(session, reply, ct);
                await npc.SendAsync(peer, reply, ct);
            }
            return true;
        }
    }

    private sealed class StoredStages(long user, BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear[] clears)
        : BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore
    {
        public ValueTask<IReadOnlyList<BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear>> ListAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear>>(userNum == user ? clears : []);
        public ValueTask<bool> RecordClearAsync(long userNum, BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear clear, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TutorialSession(string build = "kr-812") : ISessionWithFeatures
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new(build, build.Split('-')[0], int.Parse(build.Split('-')[1]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features { get; } = new PlayerFeatures();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PlayerFeatures : ISessionFeatures
    {
        private readonly IPlayerSession _player = new PlayerSession(new PlayerIdentity(12, "account", "tester", 1));
        public PlayerFeatures() => _player.MoveTo(SessionPhase.MainMenu);
        public int Capacity => 1;
        public int Count => 1;
        public bool IsClosed => false;
        public IDisposable Register<T>(T feature) where T : class => throw new NotSupportedException();
        public T Get<T>() where T : class => TryGet<T>(out var value) ? value : throw new InvalidOperationException();
        public bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class
        {
            feature = typeof(T) == typeof(IPlayerSession) ? (T)_player : null;
            return feature is not null;
        }
    }

    private sealed class TutorialTables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class TutorialTable : IOpcodeTable
    {
        public ClientProfile Profile => throw new NotSupportedException();
        public OpcodeConnection Connection => OpcodeConnection.Game;
        public IEnumerable<OpcodeEntry> All => new (ushort Code, string Name)[]
        {
            (110, "gspGotoRoom"), (505, "gspRoomChangeGameSetting"), (503, "gspRoomSetSlot"),
            (502, "gspRoomSetMasterSlotIdx"), (510, "gspRoomEnterGame"), (701, "gspGameMapLoadingComplete"),
            (702, "gspGameEverybodyLoadingComplete"), (703, "gspGameCountDown"), (704, "gspGameStartGame"),
            (705, "gspGamePreCountDown"), (520, "gspRepAutoStart"),
            (711, "gspGameEndGame"), (108, "gspGotoMenu"),
            (32857, "gppCreateMobsFromSpawnGroup"),
            (908, "Unknown908"), (909, "gspRepWaveAllSpawnMobs"), (922, "gspResSingleStageInfo"), (732, "gspGameTimeOverEnd"), (733, "gspGameShowResult_"),
        }.Select(item => new OpcodeEntry(item.Code, PacketDirection.ServerToClient, item.Name,
            Evidence.Verified, null, null));
        public IOpcodeTable ForConnection(OpcodeConnection connection) => this;
        public bool TryGet(PacketDirection direction, ushort value, out OpcodeEntry? entry)
        { entry = All.FirstOrDefault(item => item.Direction == direction && item.Value == value); return entry is not null; }
        public string Describe(PacketDirection direction, ushort value) => value.ToString();
    }
}
