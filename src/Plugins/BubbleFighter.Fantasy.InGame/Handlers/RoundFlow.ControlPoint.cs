using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IControlPointSnapshotSender? ControlPointUpdates { get; set; }
    internal Func<string, int, int>? ControlPointMapMask { get; set; }

    private bool HostsControlPointStage(PlayRoom room, string stage) =>
        room.Tutorial is not null || room.ModeId != (int)GameModeId.ControlPoint || ControlPointUpdates is not { } sender
        || ControlPointMapMask?.Invoke(stage, sender.NodeCount) is > 0;

    private void PrepareControlPoints(PlayRoom room, int generation)
    {
        if (ControlPointUpdates is not { } sender) return;
        string stage;
        lock (room.Gate)
        {
            if (room.ModeId != (int)GameModeId.ControlPoint || room.Tutorial is not null || room.StartGeneration != generation
                || room.ControlPointPlayableNodes != 0 || room.Phase != RoomPhase.Starting) return;
            stage = room.RoundStageKey.Length > 0 ? room.RoundStageKey : room.RoundMapKey;
        }
        var mask = (ControlPointMapMask ?? throw new InvalidOperationException("Missing Control Point map reader."))(stage, sender.NodeCount);
        lock (room.Gate)
            if (room.StartGeneration == generation && room.Phase == RoomPhase.Starting) room.ControlPointPlayableNodes = mask;
    }

    private bool IsControlPoint(PlayRoom room) => ControlPointUpdates is not null && room.Tutorial is null
        && room.ModeId == (int)GameModeId.ControlPoint && room.Phase == RoomPhase.Playing
        && !room.RoundCancelled && !room.IsStopping;

    internal async ValueTask StartControlPointAsync(PlayRoom room, int generation, CancellationToken ct)
    {
        RoomDeadlineClock? previous;
        lock (room.Gate)
        {
            if (!IsControlPoint(room) || room.StartGeneration != generation || room.ControlPoints is not null) return;
            previous = room.ControlPointClock;
        }
        if (previous is not null) { previous.Cancel(); await previous.Completion.ConfigureAwait(false); }
        ControlPointRoundState state;
        lock (room.Gate)
        {
            if (!IsControlPoint(room) || room.StartGeneration != generation || room.ControlPoints is not null) return;
            room.ControlPoints = state = new(ControlPointUpdates!.NodeCount, room.ControlPointPlayableNodes);
            room.ControlPointClock = new(room.Executor, _lifetime, failure =>
                _logger.LogError(failure, "Room {Room}: Control Point clock failed in generation {Generation}.", room.Id, generation));
            ScheduleControlPoint(room, state);
        }
        await SendControlPointsAsync(room, state, state.Snapshot(), ct).ConfigureAwait(false);
    }

    private void ScheduleControlPoint(PlayRoom room, ControlPointRoundState state) =>
        room.ControlPointClock!.Schedule(_options.ControlPointTickMs, ct => TickControlPointAsync(room, state, ct));

    internal async ValueTask HandleControlPointAsync(ISession session, RoundControlPointChanged change, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundControlPointsUpdated snapshot;
            ControlPointRoundState state;
            lock (room.Gate)
            {
                if (!IsControlPoint(room) || room.StartGeneration != generation || room.ControlPoints is not { } current
                    || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)
                    || seat.Game.Connection.IsClosed || room.IsRefereeSeat(seat)
                    || change.Node != -1 && seat.Life.State is not (SeatLife.Alive or SeatLife.Bubble)
                    || !current.Move(seat.Slot, session.Id, seat.Team, change.Node, seat.Life.Generation)) return;
                state = current;
                state.InvalidateSnapshots();
                snapshot = state.Snapshot();
            }
            await SendControlPointsAsync(room, state, snapshot, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask TickControlPointAsync(PlayRoom room, ControlPointRoundState state, CancellationToken ct)
    {
        RoundControlPointsUpdated? snapshot;
        RoundScoreUpdated? score;
        bool ended;
        lock (room.Gate)
        {
            if (!IsControlPoint(room) || !ReferenceEquals(room.ControlPoints, state) || state.Winner.HasValue) return;
            var departed = state.RemoveAbsent((slot, game, team, life) => room.FindByGame(game) is { } seat && seat.Slot == slot
                && seat.Team == team && seat.Life.Generation == life && !seat.Game.Connection.IsClosed
                && seat.Life.State is SeatLife.Alive or SeatLife.Bubble);
            var (nodesChanged, scoreChanged) = state.Tick(_options.ControlPointGaugePerFighter, _options.ControlPointScorePerNode);
            score = scoreChanged ? new(room.Id, 1, state.Totals, [], LiveScoreboard(room)) : null;
            if (nodesChanged || departed) state.InvalidateSnapshots();
            snapshot = room.Seats.Any(s => s.SceneReady && !s.Game.Connection.IsClosed && !state.HasSnapshot(s.Slot, s.Game.Id))
                ? state.Snapshot() : null;
            ended = state.Winner.HasValue;
            if (ended) room.DecidedWinnerTeam = state.Winner;
            else ScheduleControlPoint(room, state);
        }
        try
        {
            if (snapshot is not null) await SendControlPointsAsync(room, state, snapshot, ct).ConfigureAwait(false);
            if (score is not null && _result is { } result)
                await SendObjectiveUpdateAsync(room, state, (s, token) => result.Score.SendAsync(s, score, token), ct).ConfigureAwait(false);
        }
        finally
        {
            if (ended) await EndRoundAsync(room, RoundEndReason.WinCondition, _lifetime).ConfigureAwait(false);
        }
    }

    private ValueTask SendControlPointsAsync(PlayRoom room, ControlPointRoundState state,
        RoundControlPointsUpdated snapshot, CancellationToken ct) =>
        SendObjectiveUpdateAsync(room, state, (s, token) => ControlPointUpdates!.SendAsync(s, snapshot, token), ct, isSnapshot: true);

    private async ValueTask SendObjectiveUpdateAsync(PlayRoom room, ControlPointRoundState state,
        Func<ISession, CancellationToken, ValueTask> send, CancellationToken ct, bool isSnapshot = false)
    {
        RoomSeat[] members;
        lock (room.Gate) members = [.. room.Seats];
        foreach (var seat in members)
        {
            lock (room.Gate)
            {
                if (!IsControlPoint(room) || !ReferenceEquals(room.ControlPoints, state)) return;
                if (!seat.SceneReady || seat.Game.Connection.IsClosed || !ReferenceEquals(room.FindByGame(seat.Game.Id), seat)
                    || isSnapshot && state.HasSnapshot(seat.Slot, seat.Game.Id)) continue;
            }
            try
            {
                await send(seat.Game, ct).ConfigureAwait(false);
                if (isSnapshot) lock (room.Gate) state.SnapshotSent(seat.Slot, seat.Game.Id);
            }
            catch (Exception) when (seat.Game.Connection.IsClosed && !ct.IsCancellationRequested) { }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Room {Room}: Control Point update did not reach seat {Slot}.", room.Id, seat.Slot);
            }
        }
    }
}
