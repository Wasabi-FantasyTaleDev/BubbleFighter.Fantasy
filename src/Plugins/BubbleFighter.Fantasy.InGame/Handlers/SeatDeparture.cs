using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Removes a player from a room and tells the members who stay, without reinstalling the room.
/// Runs inside the room's executor operation, so the departure and its announcements see one
/// member table.
/// </summary>
internal static class SeatDeparture
{
    /// <summary>
    /// Detaches <paramref name="sessionId"/> from <paramref name="room"/>. In the waiting phase each
    /// remaining member receives the vacated seat (S2C 511); when the master left they then receive
    /// the new master's seat index (S2C 502) and that seat's record with ready cleared (S2C 503).
        /// Outside the waiting phase the departure is deferred and announced by
    /// <see cref="AnnounceDeferredAsync"/> once the room returns to waiting.
    /// </summary>
    internal static async ValueTask LeaveAsync(RoomRegistry registry, PlayRoom room, long sessionId,
        IReadOnlyList<int> modelItemIds, ILogger logger, CancellationToken ct)
    {
        int slot;
        bool wasMaster;
        RoomPhase phase;
        lock (room.Gate)
        {
            var seat = room.FindByGame(sessionId);
            if (seat is null)
            {
                registry.Detach(sessionId);
                return;
            }
            slot = seat.Slot;
            wasMaster = slot == room.MasterSlot;
            phase = room.Phase;
        }

        registry.Detach(sessionId);
        if (phase != RoomPhase.Waiting)
        {
            lock (room.Gate)
            {
                if (room.Seats.Count > 0)
                    room.DeferDeparture(slot, wasMaster);
            }
            logger.LogInformation("Room {Room} slot {Slot} left during {Phase}; announced when the room returns to waiting.",
                room.Id, slot, phase);
            return;
        }
        await AnnounceAsync(room, [(slot, wasMaster)], modelItemIds, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Announces the seats vacated during the round that just closed, after S2C 620 has returned the
    /// clients to the room. Every departure announcement follows S2C 620,
    /// so the members see the same S2C 511 / 502 / 503
    /// they would have seen had the player left the waiting room.
    /// </summary>
    internal static ValueTask AnnounceDeferredAsync(PlayRoom room, IReadOnlyList<int> modelItemIds, ILogger logger,
        CancellationToken ct)
    {
        (int Slot, bool WasMaster)[] departures;
        lock (room.Gate)
            departures = room.TakeDeferredDepartures();
        return departures.Length == 0
            ? ValueTask.CompletedTask
            : AnnounceAsync(room, departures, modelItemIds, logger, ct);
    }

    private static async ValueTask AnnounceAsync(PlayRoom room, (int Slot, bool WasMaster)[] departures,
        IReadOnlyList<int> modelItemIds, ILogger logger, CancellationToken ct)
    {
        RoomSeat[] remaining;
        int newMaster;
        byte[]? masterRecord = null;
        var masterLeft = departures.Any(departure => departure.WasMaster);
        lock (room.Gate)
        {
            remaining = [.. room.Seats];
            if (remaining.Length == 0)
                return;
            newMaster = room.MasterSlot;
            if (masterLeft)
            {
                // The promoted master never counts as ready (0x96DD20 exempts the master; 0x9DA3C0
                // clears the bit on the client).
                var master = room.Seats.First(seat => seat.Slot == newMaster);
                master.Ready = false;
                masterRecord = GameToClient.CreateRoomReply(newMaster, master.ToPlayerInfo(),
                    RoomPush.ProfileEntries(room, modelItemIds));
            }
        }

        var vacated = departures.Select(departure => GameToClient.SeatVacated(departure.Slot)).ToArray();
        var masterChanged = masterLeft ? GameToClient.MasterChanged(newMaster) : null;
        foreach (var seat in remaining)
        {
            foreach (var record in vacated)
                await seat.Game.SendAsync(InGameOpcodes.S2CSeatRecord, record, ct).ConfigureAwait(false);
            if (masterChanged is null)
                continue;
            await seat.Game.SendAsync(InGameOpcodes.S2CMasterChanged, masterChanged, ct).ConfigureAwait(false);
            await seat.Game.SendAsync(InGameOpcodes.S2CCreateRoomReply, masterRecord!, ct).ConfigureAwait(false);
        }
        logger.LogInformation(
            "Room {Room} slot(s) {Slots} left; {Count} member(s) told with S2C 511{Master}.",
            room.Id, string.Join(",", departures.Select(departure => departure.Slot)), remaining.Length,
            masterLeft ? $", master moved to slot {newMaster} (S2C 502/503)" : "");
    }
}
