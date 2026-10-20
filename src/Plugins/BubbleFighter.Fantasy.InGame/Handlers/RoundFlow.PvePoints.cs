using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundPvePointsChanged>? _pvePoints;

    private async ValueTask InitializePvePointsAsync(PlayRoom room, RoomSeat[] members, CancellationToken ct)
    {
        if (_pvePoints is null || _options.PvePointModeId == 0 || room.ModeId != _options.PvePointModeId) return;
        // cn-324 sub_6EE890 initializes native ranks independently of the first NPC kill.
        var rows = members.Where(seat => seat.Slot is >= 0 and < 32)
            .Select(seat => new RoundPvePointRow(seat.Slot, 0, 1)).ToArray();
        if (rows.Length == 0) return;
        var initial = new RoundPvePointsChanged(rows[0].Slot, 0, rows);
        foreach (var member in members)
            await _pvePoints.SendAsync(member.Game, initial, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandlePvePangAsync(ISession session, RoundPvePangReported report, CancellationToken ct)
    {
        if (_pvePoints is null || _options.PvePointModeId == 0 || _registry.TryGet(session.Id) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundPvePointsChanged update;
            RoomSeat[] members;
            lock (room.Gate)
            {
                if (room.ModeId != _options.PvePointModeId || room.Phase != RoomPhase.Playing
                    || room.FindByGame(session.Id) is not { Slot: >= 0 and < 32, Team: 2 } player) return;
                // cn-324 sub_532330 sends the local killer's resource index, never a client-chosen seat.
                if (report.UnitIndex < 0 || report.UnitIndex >= _options.PvePointUnitValues.Length)
                {
                    _logger.LogWarning("Room {Room} rejected cooperative score-unit index {Index} from session {Session}.", room.Id, report.UnitIndex, session.Id);
                    return;
                }
                var amount = _options.PvePointUnitValues[report.UnitIndex];
                room.Scoring.AddPvePoints(player.Slot, amount);
                members = [.. room.Seats];
                var slots = members.Where(seat => seat.Slot is >= 0 and < 32).Select(seat => seat.Slot).ToArray();
                var rows = slots.Select(slot => new RoundPvePointRow(slot, room.Scoring.PvePoints(slot),
                    1 + slots.Count(other => room.Scoring.PvePoints(other) > room.Scoring.PvePoints(slot)))).ToArray();
                update = new(player.Slot, amount, rows);
            }
            foreach (var member in members)
                await _pvePoints.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
