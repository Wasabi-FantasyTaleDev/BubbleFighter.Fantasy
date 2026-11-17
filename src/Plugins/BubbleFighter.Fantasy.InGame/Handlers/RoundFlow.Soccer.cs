using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>
    /// Counts a soccer goal the round's host reports. Only the host's client tests the ball against the goal boxes
    /// (kr-1256 sub_6E6F90 runs that check when kr-1256 sub_9EBF60 finds the local seat is the host) and drives the
    /// goal, countdown and restart frames the other players receive through the feed, so the round only keeps the
    /// score. The first box is the map's Goalpost_T1, team 1's own goal: a ball there scores for team 2.
    /// </summary>
    internal async ValueTask HandleSoccerGoalAsync(ISession session, RoundSoccerGoalReported report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(_ =>
        {
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.ModeId != (int)GameModeId.EventSoccer)
                    return ValueTask.CompletedTask;
                if (room.FindByGame(session.Id)?.Slot != room.MasterSlot || report.GoalIndex is not (0 or 1))
                {
                    _logger.LogWarning("Session {Id} soccer goal {Goal} in room {Room} ignored: not the host's or not a goal box.",
                        session.Id, report.GoalIndex, room.Id);
                    return ValueTask.CompletedTask;
                }
                var team = report.GoalIndex == 0 ? 2 : 1;
                room.SoccerGoals[team - 1]++;
                _logger.LogInformation("Room {Room}: team {Team} scored (seat {Scorer} last touched the ball); {First}:{Second}.",
                    room.Id, team, report.ScorerSlot, room.SoccerGoals[0], room.SoccerGoals[1]);
            }
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    // The team with more goals wins when the clock runs out; equal goals are a draw, as the shared result reads them.
    private static void DecideSoccerRound(PlayRoom room)
    {
        if (room.ModeId != (int)GameModeId.EventSoccer || room.DecidedWinnerTeam is not null) return;
        var (first, second) = (room.SoccerGoals[0], room.SoccerGoals[1]);
        room.DecidedWinnerTeam = first > second ? 1 : second > first ? 2 : 0;
    }
}
