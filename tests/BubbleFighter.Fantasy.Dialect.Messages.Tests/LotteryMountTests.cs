using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins installed native award routes and the open/select/reveal/close envelopes across gameplay builds.</summary>
public sealed class LotteryMountTests
{
    /// <summary>An enabled modern lottery cannot be skipped by a partial room surface.</summary>
    [Theory]
    [InlineData("waiting_rooms_only")]
    [InlineData("plaza_only")]
    [InlineData("menu_only")]
    [InlineData("browser_only")]
    public async Task ModernLotteryRequiresCompleteRounds(string option)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256"));
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms", extra: new()
            { ["modern_rooms"] = true, ["horde_lottery"] = true, [option] = true });
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() => new RoomsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("horde_lottery", error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A deferred close cannot silently discard the only native transition out of the award dialog.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredCloseRequiresItsProtocolRoomReturnSender(bool otherProtocol)
    {
        const string build = "kr-1256";
        var profile = new ClientProfile(build, "kr", 1256, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        try
        {
            if (otherProtocol)
                new RoomCommandsBinding("other/game", table, TestBuilds.All.Tables, build, NullLogger.Instance,
                    roundsBound: true).Mount(context);
            var initialHandlers = fixture.Packets.Handlers.Count;
            var error = Assert.Throws<ConfigException>(() =>
                new LotteryBinding(build + "/game", table, TestBuilds.All.Tables, build).Mount(context, fixture.Messages));
            Assert.Contains("room-return sender", error.Message, StringComparison.Ordinal);
            Assert.Equal(initialHandlers, fixture.Packets.Handlers.Count);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryOpened>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryClosed>>());
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundLotteryRequested>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The modern dialog requires its scoreboard award word before any lottery service is registered.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredCloseRejectsMissingOrRetypedAwardField(bool missing)
    {
        const string build = "kr-1256";
        var edited = PacketTableEdits.WithFieldType("ScoreRow@kr-1256", "award_display_id", "u32", "u16");
        if (missing)
        {
            var path = Path.Combine(edited.FullName, "score.yaml");
            var lines = File.ReadAllLines(path);
            var field = Assert.Single(Enumerable.Range(0, lines.Length),
                index => lines[index].Trim() == "- award_display_id: u16");
            lines[field] = lines[field].Replace("award_display_id", "unknown_476", StringComparison.Ordinal);
            File.WriteAllLines(path, lines);
        }
        var packets = PacketTables.Load(edited.FullName);
        var profile = new ClientProfile(build, "kr", 1256, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        try
        {
            new RoomCommandsBinding(protocol, table, packets, build, NullLogger.Instance, roundsBound: true).Mount(context);
            var initialHandlers = fixture.Packets.Handlers.Count;
            var error = Assert.Throws<ConfigException>(() =>
                new LotteryBinding(protocol, table, packets, build).Mount(context, fixture.Messages));
            Assert.Contains("ScoreRow.award_display_id", error.Message, StringComparison.Ordinal);
            Assert.Contains(missing ? "does not state it" : "table states u16", error.Message, StringComparison.Ordinal);
            Assert.Equal(initialHandlers, fixture.Packets.Handlers.Count);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundLotteryRequested>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryOpened>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryClosed>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotterySelected>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryRevealed>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The dialog and selection services use only the reviewed game-space assignments and unload together.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-717", false)]
    [InlineData("cn-324", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
    [InlineData("cn-279", false)]
    [InlineData("cn-279", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-406", true)]
    [InlineData("cn-705", false)]
    [InlineData("cn-705", true)]
    [InlineData("cn-706", false)]
    [InlineData("cn-706", true)]
    public async Task AwardCycleUsesNativeBodiesAndProtocolScopedRoutes(string build, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            if (build == "kr-1256")
                new RoomCommandsBinding(protocol, table, TestBuilds.All.Tables, build, NullLogger.Instance, roundsBound: true).Mount(context);
            var initialHandlers = fixture.Packets.Handlers.Count;
            new LotteryBinding(protocol, table, TestBuilds.All.Tables, build).Mount(context, fixture.Messages);
            Assert.Equal(initialHandlers + 1, fixture.Packets.Handlers.Count);
            var body = Convert.FromHexString("0400000002000000");
            async Task Request(byte[] value) => await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpReqSelectAward"), value), default);
            await Request(body);
            Assert.Equal(new RoundLotteryRequested(4, 2), Assert.Single(fixture.Messages.Received));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(body[..^1]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(new byte[12]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(body));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLotteryOpened>>()).SendAsync(session, new(), default);
            Assert.Equal(table.S2C("gspGameAwardDialog"), session.Sent[^1]);
            Assert.Empty(session.Bodies[^1]);
            var selected = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLotterySelected>>());
            await selected.SendAsync(session, new(4, 2), default);
            Assert.Equal(table.S2C("gspRepAwardSelectSuccess"), session.Sent[^1]);
            Assert.Equal(build == "cn-279" ? "0400000002000000" : "0400000002000000FFFFFFFF", Convert.ToHexString(session.Bodies[^1]));
            await selected.SendAsync(session, new(4, 2, true), default);
            Assert.Equal(table.S2C("gspRepAwardSelectFail"), session.Sent[^1]);
            Assert.Empty(session.Bodies[^1]);
            var revealed = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLotteryRevealed>>());
            await revealed.SendAsync(session, new(Enumerable.Range(1000001, 10).ToArray()), default);
            Assert.Equal(table.S2C("gspHordeModeRewardList"), session.Sent[^1]);
            Assert.Equal(44, session.Bodies[^1].Length);
            Assert.Equal(10, BitConverter.ToInt32(session.Bodies[^1]));
            Assert.Equal(1000010, BitConverter.ToInt32(session.Bodies[^1], 40));
            var beforeClose = session.Sent.Count;
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLotteryClosed>>()).SendAsync(session, new(), default);
            if (build == "kr-1256")
            {
                Assert.Equal(beforeClose, session.Sent.Count);
                var room = new RoomView(7, "test", 101, "test", 4, 0, false, [], []);
                await Assert.Single(fixture.Services.GetAll<IMessageSender<RoomEntered>>()).SendAsync(session,
                    new RoomEntered(room, 0) { Reason = RoomEntryReason.RoundReturned }, default);
                Assert.Equal(new[] { table.S2C("gspUdpStop"), table.S2C("gspGameReturnAfterAward_") },
                    session.Sent.Skip(beforeClose));
                Assert.Single(session.Sent, opcode => opcode == table.S2C("gspGameReturnAfterAward_"));
                Assert.Empty(session.Bodies[^2]);
            }
            else Assert.Equal(beforeClose + 1, session.Sent.Count);
            Assert.Equal(table.S2C("gspGameReturnAfterAward_"), session.Sent[^1]);
            Assert.Empty(session.Bodies[^1]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundLotteryRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryOpened>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotterySelected>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryRevealed>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLotteryClosed>>());
    }
}
