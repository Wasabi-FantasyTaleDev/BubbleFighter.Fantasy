using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundBombStateChanged>? _bombStateChanged;

    // kr-812 sub_7FDE50 removes the departing actor; kr-812 sub_4245B0 consumes operation cancellation.
    private async ValueTask OnBombSeatLeftAsync(PlayRoom room, CancellationToken ct)
    {
        RoundBombStateChanged? canceled;
        RoomSeat[] members;
        bool end;
        bool settle;
        lock (room.Gate)
        {
            if (room.IsStopping || room.RoundCancelled || room.BombSeries is not { } series || room.Seats.Count == 0
                || room.Phase is not (RoomPhase.Starting or RoomPhase.Playing or RoomPhase.Result)) return;
            var first = room.Seats.Any(seat => seat.Team == 1) || room.Bots.Any(bot => bot.Appearance.Team == 1);
            var second = room.Seats.Any(seat => seat.Team == 2) || room.Bots.Any(bot => bot.Appearance.Team == 2);
            if (first != second && series.TryForfeit(first ? 1 : 2))
                room.DecidedWinnerTeam = series.MatchWinner;
            end = series.Forfeited && room.Phase == RoomPhase.Playing;
            settle = series.Forfeited
                && (room.Phase == RoomPhase.Result
                    || room.Phase == RoomPhase.Starting && series.RoundIndex > 0);
            canceled = !series.Forfeited && room.Phase == RoomPhase.Playing
                ? room.Bomb.CancelDepartedOperator(room.Seats) : null;
            members = [.. room.Seats];
        }
        if (settle)
        {
            // A played series is terminal before its next presentation or loading callback. Freeze and
            // settle the remaining roster while this leave still owns the executor, even if its caller closes.
            CancellationTokenSource cancellation;
            int generation;
            lock (room.Gate)
            {
                if (room.IsStopping) return;
                generation = room.StartGeneration;
                cancellation = room.BeginBattleSettlement(_lifetime);
            }
            try
            {
                await GrantRewardsAsync(room, generation, RoundEndReason.WinCondition, cancellation.Token).ConfigureAwait(false);
            }
            finally { await room.ReleaseBattleSettlementAsync(cancellation).ConfigureAwait(false); }
        }
        if (canceled is not null)
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await _bombStateChanged!.SendAsync(member.Game, canceled, ct).ConfigureAwait(false);
        if (end) await EndRoundAsync(room, RoundEndReason.WinCondition, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays client-driven plant, cancel and defuse transitions. The room master reports detonation
    /// and mission completion (kr-812 sub_422850, gated by the master/local-slot comparison kr-812 sub_807260).
    /// Timer alignment stays clear, selecting kr-812 sub_4245B0's native 60000 ms armed countdown.
    /// </summary>
    internal async ValueTask HandleBombStateAsync(ISession session, RoundBombStateReported report,
        IMessageSender<RoundBombStateChanged> sender, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        BombReportStamp stamp;
        lock (room.Gate)
        {
            if (CaptureBombReport(room, session) is not { } captured) return;
            stamp = captured;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            lock (room.Gate)
            {
                if (!BombRound(room, stamp, out var seats))
                {
                    _logger.LogWarning("Session {Id} bomb state {State} rejected in room {Room}.",
                        session.Id, report.State, room.Id);
                    return;
                }
                var reporter = stamp.Reporter;
                if (!room.Bomb.TryApply(report, reporter, reporter.Slot == room.MasterSlot, Environment.TickCount64))
                {
                    _logger.LogWarning("Session {Id} bomb transition {Previous} to {State} with operator {Operator} rejected in room {Room}.",
                        session.Id, room.Bomb.State, report.State, report.Operator, room.Id);
                    return;
                }
                members = seats;
            }
            var change = new RoundBombStateChanged(report.State, report.Operator, report.PositionX,
                report.PositionY, report.PositionZ);
            foreach (var member in members)
                if (!member.Game.Connection.IsClosed)
                    await sender.SendAsync(member.Game, change, operationCt).ConfigureAwait(false);
            _logger.LogInformation("Room {Room} bomb state {State} from operator {Operator} delivered to {Count} members.",
                room.Id, report.State, report.Operator, members.Length);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the round the master reported decided. The report carries the winning team the observing client
    /// computed (the explosion or defuse outcome, or the opposite of an eliminated team,
    /// kr-812 sub_52A640); the round ends through the shared win-condition path and the dialect's native
    /// end presentation, the same one gspGameEndGame drives (its handler cleans the mode manager up through
    /// the manager's virtual 307).
    /// </summary>
    internal async ValueTask HandleBombMissionOverAsync(ISession session, RoundBombMissionOverReported report,
        CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        BombReportStamp stamp;
        lock (room.Gate)
        {
            if (CaptureBombReport(room, session) is not { } captured) return;
            stamp = captured;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                if (!BombRound(room, stamp, out _))
                {
                    _logger.LogWarning("Session {Id} bomb mission over rejected in room {Room}.", session.Id, room.Id);
                    return;
                }
                if (stamp.Reporter.Slot != room.MasterSlot)
                {
                    _logger.LogWarning("Session {Id} is not the bomb mission master in room {Room}.", session.Id, room.Id);
                    return;
                }
                if (!room.Bomb.MatchesResult(report, team => BombTeamEliminated(room, team)))
                {
                    _logger.LogWarning("Session {Id} bomb mission over for team {Team} does not match state {State} or elimination in room {Room}.",
                        session.Id, report.WinningTeam, room.Bomb.State, room.Id);
                    return;
                }
                // kr-812 sub_4245B0 and kr-812 sub_52A640 decide by objective or elimination, not kill totals.
                room.DecidedWinnerTeam = report.WinningTeam;
            }
            _logger.LogInformation("Room {Room} bomb mission over reported by session {Id}: team {Team} wins.",
                room.Id, session.Id, report.WinningTeam);
            await EndRoundAsync(room, RoundEndReason.WinCondition, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static bool BombTeamEliminated(PlayRoom room, int team) =>
        !room.Seats.Any(seat => seat.Team == team && seat.Life.State is SeatLife.Alive or SeatLife.Bubble)
        && !room.Bots.Any(bot => bot.Appearance.Team == team && bot.Life.State is SeatLife.Alive or SeatLife.Bubble);

    private readonly record struct BombReportStamp(int Generation, RoomSeat Reporter);

    // Native bomb reports have no round token; fence queued work to its admitted round and exact connection.
    private static BombReportStamp? CaptureBombReport(PlayRoom room, ISession session) =>
        room.Phase == RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping
        && room.FindByGame(session.Id) is { } reporter && ReferenceEquals(reporter.Game, session)
            ? new(room.StartGeneration, reporter) : null;

    /// <summary>Whether the admitted reporter still owns a seat in the same active bomb round, and its members.</summary>
    private static bool BombRound(PlayRoom room, BombReportStamp stamp, out RoomSeat[] members)
    {
        members = [];
        if (room.Phase != RoomPhase.Playing
            || room.RoundCancelled || room.IsStopping
            || room.StartGeneration != stamp.Generation
            || !ReferenceEquals(room.FindByGame(stamp.Reporter.Game.Id), stamp.Reporter)
            || room.ModeId != (int)GameModeId.Bomb)
            return false;
        members = [.. room.Seats];
        return true;
    }
}
