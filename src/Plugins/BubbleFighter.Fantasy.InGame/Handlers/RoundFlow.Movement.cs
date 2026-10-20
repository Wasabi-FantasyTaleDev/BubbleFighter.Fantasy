using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>
    /// Merges a player's movement report into its seat's record and sends the complete record to the other players'
    /// relay connections of this round. The client drops a record that names its own character, so the sender gets
    /// none; a partial report before any full one is dropped rather than filled with made-up coordinates.
    /// </summary>
    public async ValueTask HandleMovementReportedAsync(ISession session, RoundMovementReported report,
        IMessageSender<RoundMovementRelayed> sender, CancellationToken ct)
    {
        if ((report.Mask & ~PlayerMovementState.FullMask) != 0)
            throw new InvalidOperationException($"Movement mask 0x{report.Mask:X2} selects components no client writes.");
        if (_registry.TryGet(session.Id) is not { } room)
            return;
        PlayerMovementState merged;
        ISession[] targets;
        lock (room.Gate)
        {
            if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
                return;
            var seat = room.Seats.FirstOrDefault(seat => seat.Game.Id == session.Id || seat.Relay?.Id == session.Id);
            if (seat is null)
                return;
            if (report.ActorId != RoomRegistry.SceneEntity(seat.Slot))
            {
                _logger.LogWarning("Session {Id} slot {Slot} reported movement for actor {Actor}, which is not its own; dropped.",
                    session.Id, seat.Slot, report.ActorId);
                return;
            }
            if (Merge(seat.Movement, report) is not { } next)
            {
                _logger.LogDebug("Session {Id} slot {Slot} sent partial movement before a full record; dropped.", session.Id, seat.Slot);
                return;
            }
            seat.Movement = merged = next;
            targets = [.. room.Seats.Where(other => other != seat).Select(other => MatchLink(room, other)).OfType<ISession>()];
        }
        var relayed = new RoundMovementRelayed(report.ActorId, merged, report.Tick);
        foreach (var target in targets)
            await sender.SendAsync(target, relayed, ct).ConfigureAwait(false);
    }

    // Only the components the mask selects replace the stored ones; a narrower mask needs a stored record.
    private static PlayerMovementState? Merge(PlayerMovementState? previous, RoundMovementReported report)
    {
        var mask = report.Mask;
        if (previous is null && mask != PlayerMovementState.FullMask)
            return null;
        var merged = new PlayerMovementState(
            (mask & 1) != 0 ? report.X : previous!.X, (mask & 2) != 0 ? report.Y : previous!.Y,
            (mask & 4) != 0 ? report.Z : previous!.Z, (mask & 8) != 0 ? report.Yaw : previous!.Yaw,
            (mask & 16) != 0 ? report.Pitch : previous!.Pitch, (mask & 32) != 0 ? report.State : previous!.State);
        if (!float.IsFinite(merged.X) || !float.IsFinite(merged.Y) || !float.IsFinite(merged.Z))
            throw new InvalidOperationException("Movement coordinates must be finite.");
        return merged;
    }
}
