using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal IMessageSender<RoomInventoryChanged>? InventoryChanged { get; set; }

    /// <summary>Retains a dialect-validated temporary selection on the caller's own waiting-room seat.</summary>
    internal async ValueTask HandleInventoryChangeAsync(ISession session, RoomInventoryChangeRequested request, CancellationToken ct)
    {
        var requested = request.Selection;
        if (requested.CharacterSelection is < 0 or > short.MaxValue || requested.WeaponSelections.Count is not (4 or 5)
            || requested.WeaponSelections.Any(value => value is < -1 or > short.MaxValue))
            throw new ArgumentException("Room inventory selection is outside its native selector bounds.", nameof(request));
        var selection = requested with { WeaponSelections = Array.AsReadOnly(requested.WeaponSelections.ToArray()) };
        var sender = InventoryChanged ?? throw new InvalidOperationException("Room inventory changes require their notification sender.");
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async token =>
        {
            RoomSeat[] members;
            RoomInventoryChanged changed;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, session) || room.Phase != RoomPhase.Waiting || seat.Ready)
                {
                    _logger.LogWarning("Session {Id} changed temporary inventory outside an unready waiting-room seat; ignored.", session.Id);
                    return;
                }
                seat.InventorySelection = selection;
                members = [.. room.Seats];
                changed = new(seat.Slot, selection);
            }
            foreach (var member in members)
                await NotifyScenePeerAsync(member.Game, sender, changed, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
