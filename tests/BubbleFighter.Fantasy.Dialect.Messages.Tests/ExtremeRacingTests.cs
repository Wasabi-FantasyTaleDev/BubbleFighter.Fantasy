using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-1256 ExtremeRacing frames of packets/extreme-racing.yaml and the checkpoint feed structure, and
/// exercises the guarded exchange's activation, dispatch, refusal and unload.
/// </summary>
public sealed class ExtremeRacingTests
{
    private const string Build = "kr-1256";

    /// <summary>The initial object identities surround two complete counted coin maps, with an explicit unused zero word.</summary>
    [Fact]
    public void InitializationPinsBothCoinMapsAndItsUnusedWord()
    {
        ExtremeRacingBinding.RequireInitializationLayouts(TestBuilds.Packets, Build);
        var body = ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, Build, Initialization());
        Assert.Equal(64, body.Length);
        Assert.Equal("6400000000000000C800000002000000"
            + "02000000C800000000000000C900000000000000"
            + "02000000C800000003000000C900000001000000"
            + "2C01000001000000", Convert.ToHexString(body));
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, "ExtremeRacingInitialization", malformed));
    }

    /// <summary>The native reader counts each map separately and preserves full-width identities independently of semantic policy.</summary>
    [Fact]
    public void InitializationLayoutKeepsIndependentMapCountsAndIdentityWidths()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "ExtremeRacingInitialization", new Dictionary<string, object?>
        {
            ["first_cannon_identity"] = 0xF0000000u, ["unknown_8"] = 0x11223344u,
            ["first_coin_identity"] = 0xF0000100u, ["coin_count"] = 2,
            ["coin_variants"] = new object[] { new Dictionary<string, object?> { ["identity"] = 0xF0000100u, ["variant"] = 7 } },
            ["coin_rounds"] = new object[]
            {
                new Dictionary<string, object?> { ["identity"] = 0xF0000100u, ["round"] = 1 },
                new Dictionary<string, object?> { ["identity"] = 0xF0000101u, ["round"] = 3 },
            },
            ["first_stone_disposer_identity"] = 0xF0000200u, ["stone_disposer_count"] = 1,
        });
        Assert.Equal(56, body.Length);
        Assert.Equal("000000F044332211000100F002000000"
            + "01000000000100F007000000"
            + "02000000000100F001000000010100F003000000"
            + "000200F001000000", Convert.ToHexString(body));
        var decoded = PacketCodec.Read(TestBuilds.Packets, Build, "ExtremeRacingInitialization", body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "ExtremeRacingInitialization", decoded));
    }

    /// <summary>A typed initialization cannot emit missing keys, unsupported variants, overflowed IDs or excessive wire counts.</summary>
    [Fact]
    public void InvalidInitializationAssignmentsFailBeforeWriting()
    {
        foreach (var message in new[]
        {
            Initialization() with { Coins = [new(200, 0, 1), new(202, 0, 1)] },
            Initialization() with { Coins = [new(200, 1, 1)] },
            Initialization() with { Coins = [new(200, 0, 0)] },
            Initialization() with { FirstCoinIdentity = uint.MaxValue, Coins = [new(uint.MaxValue, 0, 1), new(0, 0, 1)] },
            Initialization() with { FirstStoneDisposerIdentity = uint.MaxValue, StoneDisposerCount = 2 },
            Initialization() with { StoneDisposerCount = -1 },
            Initialization() with { Coins = Enumerable.Range(0, 4094).Select(index => new RoundExtremeRacingCoin((uint)index + 200, 0, 1)).ToArray() },
        })
            Assert.Throws<InvalidDataException>(() => ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, Build, message));
    }

    /// <summary>The separately mounted initializer resolves its opcode by name and owns exactly one disposable sender registration.</summary>
    [Fact]
    public async Task InitializationMountIsOptionalAndUnloads()
    {
        const string name = "gspExtremeRacingInitGameData_";
        var native = TestBuilds.Opcodes(Build);
        var table = new OpcodeTable(native.Profile, native.All.Where(entry => entry.Name != name)
            .Append(new OpcodeEntry(1713, PacketDirection.ServerToClient, name, Evidence.Verified, null, null)));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Build + "/game", "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var binding = new ExtremeRacingBinding(Build + "/game", table, TestBuilds.Packets, Build, NullLogger.Instance);
        try
        {
            binding.Mount(context);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingInitialized>>());
            binding.MountInitialization(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingInitialized>>());
            var session = new MatchMessagesMountTests.Session(native.Profile);
            await sender.SendAsync(session, Initialization(), default);
            Assert.Equal((ushort)1713, Assert.Single(session.Sent));
            Assert.Equal(64, Assert.Single(session.Bodies).Length);
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(session, Initialization(), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingInitialized>>());
    }

    private static RoundExtremeRacingInitialized Initialization() => new(100, 200, [new(200, 0, 3), new(201, 0, 1)], 300, 1);

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

    /// <summary>The typed cannon command is exactly 16 bytes: group, identity, weapon and the float scale.</summary>
    [Fact]
    public void CannonCommandWritesSixteenPinnedBytes()
    {
        var body = ExtremeRacingBinding.WriteCannon(TestBuilds.Packets, Build, new(0, 101, 50004, 1.0f));
        Assert.Equal(16, body.Length);
        Assert.Equal("00000000" + "65000000" + "54C30000" + "0000803F", Convert.ToHexString(body));
        Assert.Equal(0u, BitConverter.ToUInt32(body, 0));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, "ExtremeRacingCannon", body[..^1]));
        Assert.Throws<ArgumentNullException>(() => ExtremeRacingBinding.WriteCannon(TestBuilds.Packets, Build, null!));
    }

    /// <summary>The typed cannon shot is exactly 38 bytes and decodes to the typed view while the payload stays unchanged.</summary>
    [Fact]
    public void CannonShotRouteDecodesThirtyEightPinnedBytes()
    {
        const string hex = "65000000" + "54C3" + "0000803F" + "00000040" + "00004040" + "00000000" + "00000000" + "000080BF"
            + "C8000000" + "A1860100";
        var body = Convert.FromHexString(hex);
        Assert.Equal(38, body.Length);
        var table = TestBuilds.Opcodes(Build);
        var game = table.ForConnection(OpcodeConnection.Game);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        var route = Assert.Single(RoundFeedRoute.Load(TestBuilds.Packets, Build, game, relay, extremeRacingCannon: true),
            route => route.ExtremeRacingCannon);
        Assert.Equal(relay.S2C("gppExtremeRacingCannon_"), route.Outbound);
        Assert.Equal((ushort)32902, route.GameInbound);
        Assert.Equal((ushort)32902, route.RelayInbound);
        var decoded = route.Read(TestBuilds.Packets, Build, body);
        Assert.Equal(new RoundExtremeRacingCannonShot(101, 50004, new(1, 2, 3), new(0, 0, -1), 200, 100001),
            decoded.Feed.ExtremeRacingCannonShot);
        Assert.Equal(body, decoded.Feed.Payload.ToArray());
        Assert.False(decoded.Feed.IncludeSender);
        Assert.Equal([101], decoded.Feed.ReportedActors);
        Assert.Null(decoded.Life);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => route.Read(TestBuilds.Packets, Build, malformed));
    }

    /// <summary>A deployment that does not opt in never claims the cannon opcode.</summary>
    [Fact]
    public void CannonShotRouteIsAbsentUnlessEnabled()
    {
        var table = TestBuilds.Opcodes(Build);
        var routes = RoundFeedRoute.Load(TestBuilds.Packets, Build,
            table.ForConnection(OpcodeConnection.Game), table.ForConnection(OpcodeConnection.Relay));
        Assert.DoesNotContain(routes, route => route.ExtremeRacingCannon);
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

    /// <summary>The cannon option mounts the command sender and claims the shot route; it needs initialization, and unloads cleanly.</summary>
    [Fact]
    public async Task CannonOptionMountsTheCommandSenderAndNeedsInitialization()
    {
        var table = TestBuilds.Opcodes(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["extreme_racing"] = true, ["extreme_racing_initialization"] = true,
            ["extreme_racing_cannon"] = true, ["result_scoreboard_modes"] = new[] { 1, 24 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingCannonCommand>>())
                .SendAsync(session, new(0, 101, 50004, 1.0f), default);
            Assert.Equal([table.S2C("gspExtremeRacingCannon_")], session.Sent);
            Assert.Equal("00000000" + "65000000" + "54C30000" + "0000803F", Convert.ToHexString(session.Bodies[0]));
            Assert.Contains(fixture.Packets.Handlers.Keys, key => key.Item1 == Build + "/relay" && key.Item2 == 32902);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingCannonCommand>>());

        using var refused = new MatchMessagesMountTests.Fixture(table);
        var invalid = refused.Context(Build, Build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["extreme_racing"] = true, ["extreme_racing_cannon"] = true,
            ["result_scoreboard_modes"] = new[] { 1, 24 },
        });
        invalid.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var failure = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(invalid, default));
            Assert.Contains("extreme_racing_cannon requires extreme_racing_initialization", failure.Message);
        }
        finally { await invalid.UnwindAsync(); }
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
