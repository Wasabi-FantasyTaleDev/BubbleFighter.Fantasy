using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Confirmed score reports and attributed life reports update the same nonlethal counters and scoreboard.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KnockdownsAndRescuesUseCommonAccounting(bool nativeLife, bool rescue)
    {
        await using var f = await CreditFixture.CreateAsync(sameTeam: rescue);
        if (nativeLife)
        {
            await f.Report(RoundLifeEvent.Bubbled);
            await f.Report(rescue ? RoundLifeEvent.Released : RoundLifeEvent.Bubbled, f.HostActor);
            await f.Report(rescue ? RoundLifeEvent.Released : RoundLifeEvent.Bubbled, f.HostActor);
        }
        else
            await f.Rig.Round.HandleKillCreditReportedAsync(f.Rig.Host,
                new(f.Host.Slot, f.Guest.Slot, 1) { IsRescue = rescue, RequiresReporterOwnership = true }, default);
        var expected = new RoundSeatStats(f.Host.Slot, 0, rescue ? 0 : 1, rescue ? 2 : 1, 0) { Rescues = rescue ? 1 : 0 };
        Assert.Equal(expected, f.Room.Scoring.Stats(f.Host.Slot));
        Assert.Equal(new RoundSeatStats(f.Guest.Slot, 0, 0, 0, 0), f.Room.Scoring.Stats(f.Guest.Slot));
        foreach (var recipient in new ISession[] { f.Rig.Host, f.Rig.Guest })
        {
            var update = Assert.Single(f.Scores.To(recipient));
            Assert.Equal(expected, Assert.Single(update.Seats));
            Assert.Equal(expected, update.Scoreboard!.Rows.Single(row => row.Slot == f.Host.Slot).Stats);
            Assert.Equal([0, 0], update.TeamTotals);
        }
    }

    /// <summary>Generic and attributed notifications in either order give one credit, and a new bubble permits a new one.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeCreditSurvivesSplitNotificationsWithoutCountingDuplicates(bool rescue, bool genericFirst)
    {
        await using var f = await CreditFixture.CreateAsync(sameTeam: rescue);
        for (var cycle = 1; cycle <= 2; cycle++)
        {
            if (rescue) await f.Report(RoundLifeEvent.Bubbled);
            var kind = rescue ? RoundLifeEvent.Released : RoundLifeEvent.Bubbled;
            if (genericFirst) await f.Report(kind);
            await f.Report(kind, f.HostActor);
            if (!genericFirst) await f.Report(kind);
            // A repeated rich release remains a release; a repeated basic bubble after a release would be a new event.
            await f.Report(kind, f.HostActor);
            var stats = f.Room.Scoring.Stats(f.Host.Slot);
            Assert.Equal(cycle, rescue ? stats.Rescues : stats.Knockdowns);
            Assert.Equal(cycle, f.Scores.To(f.Rig.Host).Length);
            if (!rescue) await f.Report(RoundLifeEvent.Released);
        }
    }

    /// <summary>A native direct death counts once, shares kill attribution and accepts a new kill after respawn.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectDeathCreditsOnceEvenAfterItsBasicBubbleNotification(bool basicBubble)
    {
        await using var f = await CreditFixture.CreateAsync();
        for (var cycle = 1; cycle <= 2; cycle++)
        {
            if (basicBubble) await f.Report(RoundLifeEvent.Bubbled);
            await f.Report(RoundLifeEvent.Died, f.HostActor);
            await f.Report(RoundLifeEvent.Died, f.HostActor);
            Assert.Equal(SeatLife.Dead, f.Guest.Life.State);
            Assert.Equal(cycle, f.Room.Scoring.Stats(f.Host.Slot).Kills);
            Assert.Equal(cycle, f.Room.Scoring.Stats(f.Guest.Slot).Deaths);
            Assert.Equal([cycle, 0], f.Room.Scoring.TeamTotals);
            Assert.Equal(cycle, f.Scores.To(f.Rig.Host).Length);
            f.Guest.Life.Revive();
        }
    }

    /// <summary>A late attributed death cannot count a bubble pop twice.</summary>
    [Fact]
    public async Task PopAndDirectDeathReportsShareTheVictimsDeathCredit()
    {
        await using var f = await CreditFixture.CreateAsync();
        await f.Report(RoundLifeEvent.Bubbled, f.HostActor);
        await f.Rig.Round.HandleLifeReportedAsync(f.Rig.Host,
            new(RoundLifeEvent.Finished, f.GuestActor) { Initiator = f.HostActor }, default);
        await f.Report(RoundLifeEvent.Died, f.HostActor);
        Assert.Equal(new RoundSeatStats(f.Host.Slot, 1, 1, 3, 0), f.Room.Scoring.Stats(f.Host.Slot));
        Assert.Equal(1, f.Room.Scoring.Stats(f.Guest.Slot).Deaths);
        Assert.Equal([1, 0], f.Room.Scoring.TeamTotals);
        Assert.Equal(2, f.Scores.To(f.Rig.Host).Length);
    }

    /// <summary>Invalid attribution never awards another player's counters or consumes a later valid rescue.</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("unknown")]
    [InlineData("opponent")]
    [InlineData("no-bubble")]
    [InlineData("foreign-reporter")]
    public async Task RescueCreditRequiresARealTeammateAndAnOwnedCompletion(string invalid)
    {
        await using var f = await CreditFixture.CreateAsync(sameTeam: true);
        if (invalid != "no-bubble") await f.Report(RoundLifeEvent.Bubbled);
        var rescuer = invalid switch { "self" => f.GuestActor, "unknown" => 5_000_123, _ => f.HostActor };
        if (invalid == "opponent") f.Host.Team = 2;
        if (invalid == "foreign-reporter")
            await f.Rig.Round.HandleLifeReportedAsync(f.Third,
                new(RoundLifeEvent.Released, f.GuestActor) { Initiator = rescuer }, default);
        else
            await f.Report(RoundLifeEvent.Released, rescuer);
        Assert.Empty(f.Scores.To(f.Rig.Host));
        f.Host.Team = 1;
        if (invalid == "no-bubble") await f.Report(RoundLifeEvent.Bubbled);
        await f.Report(RoundLifeEvent.Released, f.HostActor);
        Assert.Equal(1, f.Room.Scoring.Stats(f.Host.Slot).Rescues);
        Assert.Single(f.Scores.To(f.Rig.Host));
    }

    /// <summary>Unaffiliated damage sources change life without inventing personal or team credit.</summary>
    [Theory]
    [InlineData(RoundLifeEvent.Bubbled)]
    [InlineData(RoundLifeEvent.Died)]
    public async Task UnknownSourceDoesNotBecomeAPlayerCredit(RoundLifeEvent kind)
    {
        await using var f = await CreditFixture.CreateAsync();
        await f.Report(kind, 5_000_123);
        Assert.Empty(f.Scores.To(f.Rig.Host));
        Assert.Equal(kind == RoundLifeEvent.Died ? SeatLife.Dead : SeatLife.Bubble, f.Guest.Life.State);
        Assert.Equal([0, 0], f.Room.Scoring.TeamTotals);
    }

    /// <summary>Each build's real route data delivers native source and rescue identities to common round accounting.</summary>
    [Theory]
    [InlineData("kr-1256", 38282, 38273)]
    [InlineData("cn-774", 38282, 38273)]
    [InlineData("tw-454", 32787, 33282)]
    public async Task NativeRoutesCreditTheDecodedSourceAndRescuer(string build, int knockedOpcode, int rescueOpcode)
    {
        await using var f = await CreditFixture.CreateAsync();
        var packets = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var routes = RoundFeedRoute.Load(packets, build, game, game.ForConnection(OpcodeConnection.Relay));
        var knocked = Convert.FromHexString("E0C8100040420F000000A03F000020C0000070400000000000000000000000000000000000000000");
        var release = Convert.FromHexString("E0C8100040420F000000A03F00");
        foreach (var (opcode, body) in new[] { (knockedOpcode, knocked), (knockedOpcode, knocked), (rescueOpcode, release), (rescueOpcode, release) })
        {
            if (opcode == rescueOpcode) f.Host.Team = f.Guest.Team;
            var decoded = routes.Single(route => route.GameInbound == opcode).Read(packets, build, body);
            Assert.Equal(body, decoded.Feed.Payload.ToArray());
            await f.Rig.Round.HandleLifeReportedAsync(f.Rig.Guest, Assert.IsType<RoundLifeReported>(decoded.Life), default);
        }
        var stats = f.Room.Scoring.Stats(f.Host.Slot);
        Assert.Equal((1, 1, 3), (stats.Knockdowns, stats.Rescues, stats.Score));
        Assert.Equal(2, f.Scores.To(f.Rig.Host).Length);
    }

    /// <summary>Bot lives and attribution belong to their actual controller and use the same counters as human characters.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlledBotsUseTheCommonNativeCreditPath(bool rescue)
    {
        await using var f = await CreditFixture.CreateAsync(sameTeam: rescue);
        f.Room.ResetToWaiting();
        Assert.True(f.Room.PutBot(new RoomMemberView(3, f.Host.UserNum, "bot", 1, 1, true, 1, [])
            { Ai = new RoomAiIdentity(f.Host.Slot, f.Host.UserNum, 1, 1) }));
        f.Room.MarkStarting();
        f.Room.MarkPlaying();
        var botActor = RoomRegistry.SceneEntity(3);
        if (rescue)
        {
            await f.Rig.Round.HandleLifeReportedAsync(f.Rig.Guest, new(RoundLifeEvent.Bubbled, botActor), default);
            Assert.Equal(SeatLife.Alive, f.Room.BotAt(3)!.Life.State);
            await f.Rig.Round.HandleLifeReportedAsync(f.Rig.Host, new(RoundLifeEvent.Bubbled, botActor), default);
            await f.Rig.Round.HandleLifeReportedAsync(f.Rig.Guest,
                new(RoundLifeEvent.Released, botActor) { Initiator = f.GuestActor }, default);
            Assert.Equal(1, f.Room.Scoring.Stats(f.Guest.Slot).Rescues);
            Assert.Equal(SeatLife.Alive, f.Room.BotAt(3)!.Life.State);
        }
        else
        {
            await f.Report(RoundLifeEvent.Bubbled, botActor);
            Assert.Equal(1, f.Room.Scoring.Stats(3).Knockdowns);
            Assert.Equal(0, f.Room.Scoring.Stats(f.Host.Slot).Knockdowns);
            await f.Report(RoundLifeEvent.Died, botActor);
            Assert.Equal(1, f.Room.Scoring.Stats(3).Kills);
            Assert.Equal(1, f.Room.Scoring.Stats(f.Guest.Slot).Deaths);
        }
    }

    /// <summary>A new round clears attribution guards together with its scores and character lives.</summary>
    [Fact]
    public async Task NewRoundAllowsFreshAttributionWithoutCarryingOldCredit()
    {
        await using var f = await CreditFixture.CreateAsync();
        await f.Report(RoundLifeEvent.Bubbled, f.HostActor);
        await f.Report(RoundLifeEvent.Died, f.HostActor);
        f.Room.ResetToWaiting();
        f.Room.MarkStarting();
        f.Room.MarkPlaying();
        Assert.Equal(new RoundSeatStats(f.Host.Slot, 0, 0, 0, 0), f.Room.Scoring.Stats(f.Host.Slot));
        await f.Report(RoundLifeEvent.Bubbled, f.HostActor);
        await f.Report(RoundLifeEvent.Died, f.HostActor);
        Assert.Equal(new RoundSeatStats(f.Host.Slot, 1, 1, 3, 0), f.Room.Scoring.Stats(f.Host.Slot));
    }

    private sealed class CreditFixture : IAsyncDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly PluginContext _context;
        internal Rig Rig { get; }
        internal RecordingSession Third { get; } = new(33);
        internal Sink<RoundScoreUpdated> Scores { get; } = new();
        internal PlayRoom Room => Rig.Registry.Find(Rig.RoomId)!;
        internal RoomSeat Host => Room.FindByGame(Rig.Host.Id)!;
        internal RoomSeat Guest => Room.FindByGame(Rig.Guest.Id)!;
        internal int HostActor => RoomRegistry.SceneEntity(Host.Slot);
        internal int GuestActor => RoomRegistry.SceneEntity(Guest.Slot);

        private CreditFixture()
        {
            _context = new(new PluginRow { Id = "credits", Plugin = "test", Source = "test" }, _services,
                new EventBus(NullLogger.Instance), NullLogger.Instance);
            _context.Effect(_services.RegisterMany<IMessageSender<RoundScoreUpdated>>(Scores));
            _context.Effect(_services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
            _context.Effect(_services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
            _context.Effect(_services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
            Rig = new(resultContext: _context, configure: options => options.NativeLifeEveryMode = true);
            Third.SetLobbyPlayer(33, "third");
        }

        internal static async Task<CreditFixture> CreateAsync(bool sameTeam = false)
        {
            var f = new CreditFixture();
            try
            {
                await f.Rig.StartAsync(f.Third);
                f.Room.MarkPlaying();
                f.Host.Team = 1;
                f.Guest.Team = sameTeam ? (byte)1 : (byte)2;
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal ValueTask Report(RoundLifeEvent kind, int? initiator = null) => Rig.Round.HandleLifeReportedAsync(Rig.Guest,
            new(kind, GuestActor) { Initiator = initiator }, default);

        public async ValueTask DisposeAsync()
        {
            await Rig.Registry.StopAllAsync();
            await _context.UnwindAsync();
        }
    }
}
