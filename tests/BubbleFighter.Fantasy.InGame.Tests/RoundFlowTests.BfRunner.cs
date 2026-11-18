using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>
    /// Only the host's report for a team in the room decides the race; every player hears the winner once and the round
    /// ends with that team's win after the client's own pause.
    /// </summary>
    [Fact]
    public async Task BfRunnerHostGoalDecidesTheRace()
    {
        var (rig, context) = CaptainResultRig();
        var ended = Assert.IsType<Sink<RoundEnded>>(Assert.Single(context.Services.GetAll<IMessageSender<RoundEnded>>()));
        var (_, finished) = AttachBfRunner(rig);
        try
        {
            var room = await StartBfRunnerRoundAsync(rig);
            await rig.Round.HandleBfRunnerGoalAsync(rig.Guest, new(2), default);
            await rig.Round.HandleBfRunnerGoalAsync(rig.Host, new(3), default);
            Assert.Empty(finished.To(rig.Host));

            await rig.Round.HandleBfRunnerGoalAsync(rig.Host, new(2), default);
            await rig.Round.HandleBfRunnerGoalAsync(rig.Host, new(1), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(new RoundBfRunnerFinished(2), Assert.Single(finished.To(member)));
            lock (room.Gate) Assert.Equal(2, room.BfRunner.WinnerTeam);

            for (var i = 0; i < 500 && ended.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            // The host sits on team 1 and the guest on team 2.
            Assert.Equal(RoundOutcome.Lost, Assert.Single(ended.To(rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Won, Assert.Single(ended.To(rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A new checkpoint reaches every player once; a repeat, or a report after the goal, changes nothing.</summary>
    [Fact]
    public async Task BfRunnerCheckpointsReachEveryPlayer()
    {
        var (rig, context) = CaptainResultRig();
        var (reached, _) = AttachBfRunner(rig);
        try
        {
            var room = await StartBfRunnerRoundAsync(rig);
            int guest;
            lock (room.Gate) guest = room.FindByGame(rig.Guest.Id)!.Slot;
            await rig.Round.HandleBfRunnerCheckpointAsync(rig.Guest, new(4), default);
            await rig.Round.HandleBfRunnerCheckpointAsync(rig.Guest, new(4), default);
            await rig.Round.HandleBfRunnerCheckpointAsync(rig.Guest, new(5), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal([new RoundBfRunnerCheckpointReached(guest, 4), new RoundBfRunnerCheckpointReached(guest, 5)],
                    reached.To(member));

            await rig.Round.HandleBfRunnerGoalAsync(rig.Host, new(1), default);
            await rig.Round.HandleBfRunnerCheckpointAsync(rig.Guest, new(6), default);
            Assert.Equal(2, reached.To(rig.Host).Length);
            await rig.Round.HandleBfRunnerProgressAsync(rig.Guest, new([new(guest, 50000)]), default);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Without the build's exchange mode 21 is an ordinary team round: the reports are not evidence.</summary>
    [Fact]
    public async Task BfRunnerNeedsTheExchange()
    {
        var (rig, context) = CaptainResultRig();
        try
        {
            var room = await StartBfRunnerRoundAsync(rig);
            await rig.Round.HandleBfRunnerGoalAsync(rig.Host, new(1), default);
            await rig.Round.HandleBfRunnerCheckpointAsync(rig.Guest, new(4), default);
            lock (room.Gate)
            {
                Assert.Null(room.BfRunner.WinnerTeam);
                Assert.Null(room.DecidedWinnerTeam);
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    private static (Sink<RoundBfRunnerCheckpointReached> Reached, Sink<RoundBfRunnerFinished> Finished) AttachBfRunner(Rig rig,
        Sink<RoundBfRunnerItemGranted>? granted = null)
    {
        var reached = new Sink<RoundBfRunnerCheckpointReached>();
        var finished = new Sink<RoundBfRunnerFinished>();
        rig.Round.BfRunnerCheckpointReached = reached;
        rig.Round.BfRunnerFinished = finished;
        rig.Round.BfRunnerItemGranted = granted ?? new Sink<RoundBfRunnerItemGranted>();
        rig.Round.BfRunnerItems = new([(146, 1)]);
        return (reached, finished);
    }

    /// <summary>
    /// A runner holds one item: the first box fills slot 0 and tells every player, a further box while it is held gives
    /// nothing (the client cannot switch slots in mode 21, so a second slot could never be used), and after the ordinary
    /// item use the next box fills slot 0 again. A box report with a booster's region shape is not a box.
    /// </summary>
    [Fact]
    public async Task BfRunnerItemBoxFillsTheSingleHeldSlot()
    {
        var (rig, context) = CaptainResultRig();
        var granted = new Sink<RoundBfRunnerItemGranted>();
        AttachBfRunner(rig, granted);
        try
        {
            var room = await StartBfRunnerRoundAsync(rig);
            int host;
            lock (room.Gate) host = room.FindByGame(rig.Host.Id)!.Slot;
            for (var box = 0; box < 5; box++)
                await rig.Round.HandleBfRunnerItemAsync(rig.Host, new(0, box), default);
            await rig.Round.HandleBfRunnerItemAsync(rig.Host, new(0, -1), default);
            Assert.Equal(new RoundBfRunnerItemGranted(host, 146, 0), Assert.Single(granted.To(rig.Host)));
            Assert.Equal(granted.To(rig.Host), granted.To(rig.Guest));

            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Equal(146, Assert.Single(rig.ItemUsed.To(rig.Guest)).Kind);
            await rig.Round.HandleBfRunnerItemAsync(rig.Host, new(0, 9), default);
            Assert.Equal(new RoundBfRunnerItemGranted(host, 146, 0), granted.To(rig.Guest)[^1]);
            Assert.Equal(2, granted.To(rig.Guest).Length);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A speed pad applies its direct effect to the runner on every client; a pad report naming a region does not.</summary>
    [Fact]
    public async Task BfRunnerSpeedPadBoostsTheRunnerEverywhere()
    {
        var (rig, context) = CaptainResultRig();
        AttachBfRunner(rig);
        try
        {
            var room = await StartBfRunnerRoundAsync(rig);
            int guest;
            lock (room.Gate) guest = room.FindByGame(rig.Guest.Id)!.Slot;
            await rig.Round.HandleBfRunnerItemAsync(rig.Guest, new(101, 4), default);
            Assert.Empty(rig.CountedItemUsed.To(rig.Host));
            await rig.Round.HandleBfRunnerItemAsync(rig.Guest, new(101, -1), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(new RoundCountedItemUsed(guest, 101), Assert.Single(rig.CountedItemUsed.To(member)));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>
    /// The pool is the transposed ItemSpecBFRunner sheet: enabled items with a positive drop weight, picked by weight.
    /// </summary>
    [Fact]
    public void BfRunnerItemPoolReadsEnabledWeightedItems()
    {
        IReadOnlyList<string>[] rows =
        [
            ["ItemSpecBFRunner:H"],
            ["[Field]", "[Desc]", "booster", "shield", "magnet", "banana"],
            ["id", "item id", "101", "102", "126", "128"],
            ["isEnabled", "appears in the box", "1", "1", "0", "1"],
            ["itemWeight", "drop chance", "15", "7000", "10", "0"],
        ];
        var pool = Data.BfRunnerItemPool.Parse(rows);
        Assert.Equal([(101, 15d), (102, 7000d)], pool.Items);
        Assert.Equal(101, pool.Pick(0));
        Assert.Equal(102, pool.Pick(15 / 7015d));
        Assert.Equal(102, pool.Pick(0.999999));
        Assert.Throws<InvalidDataException>(() => Data.BfRunnerItemPool.Parse(rows[..4]));
    }

    private static async Task<PlayRoom> StartBfRunnerRoundAsync(Rig rig)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("race room", "", (int)GameModeId.BfRunner, "map"), default);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(roomId, ""), default);
        await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), default);
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), default);
        var room = rig.Registry.Find(roomId)!;
        lock (room.Gate)
        {
            room.FindByGame(rig.Host.Id)!.Team = 1;
            room.FindByGame(rig.Guest.Id)!.Team = 2;
        }
        foreach (var member in new[] { rig.Host, rig.Guest })
            await rig.Round.HandleLoadReportedAsync(member, new(), default);
        foreach (var member in new[] { rig.Host, rig.Guest })
            await rig.WaitStartedAsync(member);
        return room;
    }
}
