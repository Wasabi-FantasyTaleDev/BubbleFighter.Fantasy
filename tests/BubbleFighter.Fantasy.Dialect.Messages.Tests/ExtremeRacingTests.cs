using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-1256 ExtremeRacing frames of packets/extreme-racing.yaml and the checkpoint feed structure, and
/// exercises the guarded exchange's activation, dispatch, refusal and unload.
/// </summary>
public sealed class ExtremeRacingTests
{
    private const string Build = "kr-1256";

    /// <summary>The race refuses a result configuration that would silently omit its final scoreboard.</summary>
    [Fact]
    public async Task EnabledRaceRequiresItsScoreboardMode()
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(Build));
        var context = fixture.Context(Build, Build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["extreme_racing"] = true, ["result_scoreboard_modes"] = new[] { 1 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var failure = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("result_scoreboard_modes", failure.Message);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundExtremeRacingGoalReported>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The clock frame is three words, the round frame two, the goal one.</summary>
    [Fact]
    public void ClockRoundAndGoalFramesPinTheirWords()
    {
        Assert.Equal("50340300E093040001000000", Convert.ToHexString(ExtremeRacingBinding.WriteTimer(TestBuilds.Packets, Build,
            new(RoundExtremeRacingTimer.RaceRunning, 300_000, true))));
        Assert.Equal("C4090000E803000000000000", Convert.ToHexString(ExtremeRacingBinding.WriteTimer(TestBuilds.Packets, Build,
            new(RoundExtremeRacingTimer.RoundOver, 1000, false))));
        Assert.Equal("204E00000000000000000000", Convert.ToHexString(ExtremeRacingBinding.WriteTimer(TestBuilds.Packets, Build,
            new(RoundExtremeRacingTimer.FinishCountdown, 0, false))));
        Assert.Equal("0000000003000000", Convert.ToHexString(ExtremeRacingBinding.WriteRound(TestBuilds.Packets, Build, new(3))));
        Assert.Equal(new RoundExtremeRacingGoalReported(2),
            ExtremeRacingBinding.ReadGoal(TestBuilds.Packets, Build, Convert.FromHexString("02000000")));
        foreach (var malformed in new[] { new byte[3], new byte[5] })
            Assert.Throws<PacketFormatException>(() => ExtremeRacingBinding.ReadGoal(TestBuilds.Packets, Build, malformed));
    }

    /// <summary>
    /// The record frame is the scope word, a counted record list and, per record, a two-byte seat and a counted list of
    /// one-byte field ids with four-byte values (kr-1256 sub_10C1850, kr-1256 sub_CE4330, kr-1256 sub_CE71F0).
    /// </summary>
    [Fact]
    public void RecordFramePinsItsNestedCounts()
    {
        var body = ExtremeRacingBinding.WriteRecords(TestBuilds.Packets, Build, new(
        [
            new(0, [new(RoundExtremeRacingRecordField.RoundFirst, 10), new(RoundExtremeRacingRecordField.TotalFirst, 20)]),
            new(5, []),
        ]));
        Assert.Equal("01000000" + "02000000"
            + "0000" + "02000000" + "01" + "0A000000" + "00" + "14000000"
            + "0500" + "00000000", Convert.ToHexString(body));
        Assert.Equal(30, body.Length);
    }

    /// <summary>The checkpoint touch relayed on the round feed is three words: character, round and marker.</summary>
    [Fact]
    public void CheckpointFeedStructureIsThreeWords()
    {
        var fields = TestBuilds.Packets.Resolve(Build, "ExtremeRacingCheckPoint");
        Assert.Equal(["actor:u32", "round:u32", "marker:u32"], fields.Select(field => $"{field.Name}:{field.Type}"));
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "ExtremeRacingCheckPoint", new Dictionary<string, object?>
        {
            ["actor"] = 101, ["round"] = 3, ["marker"] = 7,
        });
        Assert.Equal("650000000300000007000000", Convert.ToHexString(body));
        var table = TestBuilds.Opcodes(Build);
        var route = Assert.Single(RoundFeedRoute.Load(TestBuilds.Packets, Build,
            table.ForConnection(OpcodeConnection.Game), table.ForConnection(OpcodeConnection.Relay)), route => route.ExtremeRacingCheckpoint);
        var decoded = route.Read(TestBuilds.Packets, Build, body);
        Assert.Equal(new RoundExtremeRacingCheckpoint(101, 3, 7), decoded.Feed.ExtremeRacingCheckpoint);
        Assert.Equal(body, decoded.Feed.Payload.ToArray());
        Assert.True(decoded.Feed.IncludeSender);
        Assert.Null(decoded.Life);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => route.Read(TestBuilds.Packets, Build, malformed));
    }

    /// <summary>The enabled exchange dispatches the goal touch, writes all three senders and unloads every effect.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledExchangeDispatchesAndUnloads(bool remap)
    {
        var native = TestBuilds.Opcodes(Build);
        var table = remap ? new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : native;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["extreme_racing"] = true, ["result_scoreboard_modes"] = new[] { 1, 24 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(native.Profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            await fixture.Packets.DispatchAsync(Build + "/game", session,
                new(table.C2S("gcpExtremeRacingGoal_"), Convert.FromHexString("01000000")), default);
            Assert.Equal(new RoundExtremeRacingGoalReported(1), Assert.Single(fixture.Messages.Received));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingTimerChanged>>())
                .SendAsync(session, new(RoundExtremeRacingTimer.FinishCountdown, 10_000, true), default);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingRoundChanged>>()).SendAsync(session, new(2), default);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingRecordsUpdated>>()).SendAsync(session, new([]), default);
            Assert.Equal([table.S2C("gspExtremeRacingTimer_"), table.S2C("gspExtremeRacingRound_"),
                table.S2C("gspExtremeRacingUpdatePlayRecord_")], session.Sent);
            Assert.Equal("204E00001027000001000000", Convert.ToHexString(session.Bodies[0]));
            Assert.Equal("0000000002000000", Convert.ToHexString(session.Bodies[1]));
            Assert.Equal("0100000000000000", Convert.ToHexString(session.Bodies[2]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Packets.DispatchAsync(Build + "/game",
                session, new(table.C2S("gcpExtremeRacingGoal_"), Convert.FromHexString("01000000")), default));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundExtremeRacingGoalReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingTimerChanged>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingRecordsUpdated>>());
    }

    /// <summary>Without the option nothing is mounted; builds without the audited capability refuse it.</summary>
    [Theory]
    [InlineData("kr-1256", false, true)]
    [InlineData("cn-774", true, false)]
    [InlineData("tw-454", true, false)]
    public async Task OptionNeedsTheBuildCapability(string build, bool enabled, bool activates)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["extreme_racing"] = enabled, ["result_scoreboard_modes"] = new[] { 1, 24 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            if (activates)
            {
                await new RoomsPlugin().ActivateAsync(context, default);
                Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundExtremeRacingGoalReported>>());
            }
            else
            {
                var failure = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
                Assert.Contains("extreme_racing", failure.Message);
                Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundExtremeRacingGoalReported>>());
            }
        }
        finally { await context.UnwindAsync(); }
    }
}
