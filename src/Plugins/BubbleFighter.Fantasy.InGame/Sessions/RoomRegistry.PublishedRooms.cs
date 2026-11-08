using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.RoomCore;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class RoomRegistry
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PublishedRoomSnapshot>> ListPublishedAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(SnapshotListedRooms(matchRoomsOnly: true, cancellationToken));

    internal IReadOnlyList<PublishedRoomSnapshot> SnapshotListedRooms(bool matchRoomsOnly, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshots = new List<PublishedRoomSnapshot>();
        foreach (var room in _rooms.Values)
        {
            ct.ThrowIfCancellationRequested();
            lock (room.Gate)
            {
                if (!room.Listed || room.Phase == RoomPhase.Closed || room.Seats.Count == 0
                    || matchRoomsOnly && !room.IsMatchRoom) continue;
                var master = room.Seats.FirstOrDefault(seat => seat.Slot == room.MasterSlot)?.Nickname ?? string.Empty;
                var entry = new RoomListEntry(room.Id, room.RoomName, master, room.ModeId, room.OccupantCount,
                    room.MaxSeats - room.ClosedSlots.Count, room.Password.Length > 0,
                    CopyListSettings(room.BuildSettings, includeSessionPrefix: !matchRoomsOnly))
                    { MatchInProgress = room.Phase != RoomPhase.Waiting };
                snapshots.Add(new(room.InstanceId, entry,
                    string.IsNullOrEmpty(room.RoundMapKey) ? room.MapKey : room.RoundMapKey, room.Phase.ToString()));
            }
        }
        ct.ThrowIfCancellationRequested();
        return snapshots.AsReadOnly();
    }

    private static RoomBuildSettings? CopyListSettings(RoomBuildSettings? settings, bool includeSessionPrefix) => settings is null ? null :
        new(settings.Lineage, settings.Bytes.ToArray(), settings.Change.ToArray())
        {
            Mode = settings.Mode,
            SessionPrefix = includeSessionPrefix ? settings.SessionPrefix.ToArray() : default,
            RoundSelections = Array.AsReadOnly(settings.RoundSelections.Select(selection =>
                new RoomRoundSelection(selection.Index, selection.Bytes.ToArray())).ToArray()),
        };
}
