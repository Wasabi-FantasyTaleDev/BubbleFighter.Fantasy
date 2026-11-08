using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // Called under the room gate or its serial executor; every source counter already passed shared event admission.
    private static RoundBattleRecord BattleRecord(PlayRoom room, RoomSeat seat, BattleRecordOutcome outcome)
    {
        var stats = room.Scoring.Stats(seat.Slot);
        return new(room.ModeId, outcome, stats.Knockdowns, stats.Kills, stats.Rescues, stats.Deaths, stats.Score);
    }

    internal async ValueTask RecordDepartureAsync(PlayRoom room, RoomSeat seat, CancellationToken ct)
    {
        if (!_options.RecordBattleStatistics || _progress is null) return;
        RoundSettlementCommand command;
        lock (room.Gate)
        {
            // Initial loading failures are unplayed. Reloading a bomb series retains its played rounds and counters.
            if (room.Tutorial is not null || room.RoundCancelled || room.BattleSettlements is not null || seat.UserNum <= 0
                || room.Phase != RoomPhase.Playing
                && !(room.Phase == RoomPhase.Result && room.BombSeries is { CanContinue: true })
                && !(room.Phase == RoomPhase.Starting && room.BombSeries is { RoundIndex: > 0 }))
                return;
            command = new(room.MatchId, seat.UserNum, 0, stored => stored)
            {
                BattleRecord = BattleRecord(room, seat, BattleRecordOutcome.Escaped),
            };
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var authority = _mutations?.Enter(seat.Game);
                await _progress.SettleRoundAsync(command, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                if (attempt >= _options.SettlementAttempts)
                {
                    _logger.LogError(failure, "Room {Room} player {User}: departure from match {Match} could not be recorded.",
                        room.Id, seat.UserNum, command.MatchId);
                    return;
                }
                _logger.LogWarning(failure, "Match {Match} player {User}: departure recording attempt {Attempt} failed; retrying.",
                    command.MatchId, seat.UserNum, attempt);
            }
        }
    }
}
