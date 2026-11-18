using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private sealed class ControlPointSink(int count) : IControlPointSnapshotSender
    {
        internal readonly List<(long Session, RoundControlPointsUpdated Snapshot)> Sent = [];
        internal long? FailOnceFor { get; set; }
        public string Protocol => "test/game";
        public int NodeCount => count;
        public ValueTask SendAsync(ISession session, RoundControlPointsUpdated message, CancellationToken ct)
        {
            if (FailOnceFor == session.Id)
            {
                FailOnceFor = null;
                throw new IOException("Test transport lost one objective update.");
            }
            Sent.Add((session.Id, message));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A stale or unavailable arena never sends its players into loading; they can start again on a supported map.</summary>
    [Fact]
    public async Task ControlPointRefusesMissingObjectivesBeforeRoundLoading()
    {
        var rig = new Rig();
        rig.Round.ControlPointUpdates = new ControlPointSink(5);
        rig.Round.ControlPointMapMask = (stage, _) => stage == "arena" ? 1 : 0;
        rig.Rooms.HostsRoundMap = rig.Round.HostsModeMap;
        rig.Rooms.ValidateRoundMap = rig.Round.ValidateModeMap;
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new("objectives", "", 29, "arena"), default);
            var room = Assert.IsType<PlayRoom>(rig.Registry.TryGet(rig.Host.Id));
            await rig.Rooms.HandleJoinAsync(rig.Guest, new(room.Id, ""), default);
            await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new("ordinary-map"), default);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.True(room.CanStart);
            await rig.Rooms.HandleStartAsync(rig.Host, new("arena"), default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Both seats see one objective, an enemy stops points, and a bubbled opponent can retake it and end the match.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    public async Task ControlPointCaptureContestAndSingleSettlement(int count)
    {
        var rig = new Rig(configure: o =>
        {
            o.ControlPointTickMs = 60000;
            o.ControlPointGaugePerFighter = 60;
            o.ControlPointScorePerNode = 250;
            o.ResultCloseGraceMs = 60000;
        });
        var updates = new ControlPointSink(count) { FailOnceFor = rig.Guest.Id };
        rig.Round.ControlPointUpdates = updates;
        rig.Round.ControlPointMapMask = (_, _) => 1;
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new("objectives", "", 29, "map"), default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            await rig.Rooms.HandleJoinAsync(rig.Guest, new(room.Id, ""), default);
            await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
            foreach (var member in new[] { rig.Host, rig.Guest })
                await rig.Round.HandleLoadReportedAsync(member, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask, default);
            var state = Assert.IsType<ControlPointRoundState>(room.ControlPoints);
            Assert.Equal(rig.Host.Id, Assert.Single(updates.Sent).Session);
            await Step();
            Assert.Equal(new[] { rig.Host.Id, rig.Guest.Id }, updates.Sent.Select(x => x.Session));
            await Move(rig.Host, 1);
            var home = room.FindByGame(rig.Host.Id)!;
            var away = room.FindByGame(rig.Guest.Id)!;
            Assert.NotEqual(home.Team, away.Team);
            Assert.Equal(count, updates.Sent[0].Snapshot.Nodes.Count);
            var sent = updates.Sent.Count;
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.WavesCleared }, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await Move(rig.Host, 1);
            await Move(rig.Guest, count + 1);
            await Move(rig.Guest, 2);
            await Move(new RecordingSession(99), 1);
            Assert.Equal(sent, updates.Sent.Count);
            await Step();
            Assert.Equal(0, state.Snapshot().Nodes[0].Owner);
            await Step();
            Assert.Equal((int)home.Team, state.Snapshot().Nodes[0].Owner);
            await Step();
            Assert.Equal(250, state.Totals[home.Team - 1]);
            lock (room.Gate) Assert.True(away.Life.Bubble());
            await Move(rig.Guest, 1);
            await Step();
            Assert.Equal(250, state.Totals[home.Team - 1]);
            await Move(rig.Host, -1);
            await Step();
            await Step();
            Assert.Equal((int)away.Team, state.Snapshot().Nodes[0].Owner);
            var lastPair = updates.Sent.TakeLast(2).ToArray();
            Assert.Equal(new[] { rig.Host.Id, rig.Guest.Id }.Order(), lastPair.Select(p => p.Session).Order());
            Assert.Same(lastPair[0].Snapshot, lastPair[1].Snapshot);
            await Step();
            await Step();
            await Step();
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.Equal(500, state.Totals[away.Team - 1]);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Guest)).Outcome);
            Assert.Equal(RoundOutcome.Lost, Assert.Single(rig.Ended.To(rig.Host)).Outcome);

            Task Move(ISession s, int n) => rig.Round.HandleControlPointAsync(s, new(n), default).AsTask();
            Task Step() => room.Executor.ExecuteAsync(ct => rig.Round.TickControlPointAsync(room, state, ct), default);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Allies speed capture, moving changes only one membership, and a disconnected or replaced seat cannot earn points.</summary>
    [Fact]
    public void ControlPointMembershipIsUniqueAndSessionBound()
    {
        var state = new ControlPointRoundState(3, 3);
        Assert.True(state.Move(0, 11, 1, 1, 0));
        Assert.True(state.Move(3, 22, 1, 1, 0));
        state.Tick(30, 1);
        Assert.Equal(60, state.Snapshot().Nodes[0].FirstTeamProgress);
        Assert.True(state.Move(3, 22, 1, 2, 0));
        Assert.True(state.RemoveAbsent((slot, session, _, _) => slot == 0 && session == 11));
        state.Tick(30, 1);
        Assert.Equal(90, state.Snapshot().Nodes[0].FirstTeamProgress);
        Assert.Empty(state.Snapshot().Nodes[1].FirstTeamSeats);
        Assert.Equal(0, state.Snapshot().Nodes[1].FirstTeamProgress);
        Assert.False(state.Move(7, 77, 2, 4, 0));
        Assert.True(state.RemoveAbsent((_, _, _, life) => life == 1));
        Assert.Empty(state.Snapshot().Nodes[0].FirstTeamSeats);
        var next = new ControlPointRoundState(3, 1);
        Assert.All(next.Snapshot().Nodes, p => { Assert.Equal(0, p.Owner); Assert.Empty(p.FirstTeamSeats); });
        Assert.Equal(new[] { 0, 0 }, next.Totals);
    }

    /// <summary>Equally fast teams draw; two owned objectives overtake one without awarding points past the winning instant.</summary>
    [Theory]
    [InlineData(false, 0, 500)]
    [InlineData(true, 1, 250)]
    public void ControlPointStopsBothTeamScoresAtTheWinningInstant(bool extra, int expectedWinner, int otherScore)
    {
        var state = new ControlPointRoundState(3, 7);
        Assert.True(state.Move(0, 10, 1, 1, 1));
        Assert.True(state.Move(1, 11, 2, 2, 1));
        if (extra) Assert.True(state.Move(3, 13, 1, 3, 1));
        state.Tick(120, 300);
        state.Tick(120, 300);
        state.Tick(120, 300);
        Assert.Equal(expectedWinner, state.Winner);
        Assert.Equal(new[] { 500, otherScore }, state.Totals);
        Assert.False(state.Tick(120, 300).ScoreChanged);
    }
}
