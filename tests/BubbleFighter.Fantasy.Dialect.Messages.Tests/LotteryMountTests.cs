using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins installed native award routes and the open/select/reveal/close envelopes across gameplay builds.</summary>
public sealed class LotteryMountTests
{
    /// <summary>The dialog and selection services use only the reviewed game-space assignments and unload together.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("cn-324", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
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
            new LotteryBinding(protocol, table, TestBuilds.All.Tables, build).Mount(context, fixture.Messages);
            Assert.Single(fixture.Packets.Handlers);
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
            Assert.Equal("0400000002000000FFFFFFFF", Convert.ToHexString(session.Bodies[^1]));
            await selected.SendAsync(session, new(4, 2, true), default);
            Assert.Equal(table.S2C("gspRepAwardSelectFail"), session.Sent[^1]);
            Assert.Empty(session.Bodies[^1]);
            var revealed = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLotteryRevealed>>());
            await revealed.SendAsync(session, new(Enumerable.Range(1000001, 10).ToArray()), default);
            Assert.Equal(table.S2C("gspHordeModeRewardList"), session.Sent[^1]);
            Assert.Equal(44, session.Bodies[^1].Length);
            Assert.Equal(10, BitConverter.ToInt32(session.Bodies[^1]));
            Assert.Equal(1000010, BitConverter.ToInt32(session.Bodies[^1], 40));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLotteryClosed>>()).SendAsync(session, new(), default);
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
