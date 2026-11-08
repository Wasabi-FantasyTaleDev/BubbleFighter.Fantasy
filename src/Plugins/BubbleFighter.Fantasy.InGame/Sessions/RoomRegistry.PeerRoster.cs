using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.RoomCore;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class RoomRegistry
{
    string IRoomPeerRoster.Protocol => _options.GameProtocol;

    IReadOnlyList<RoomRosterView> IRoomPeerRoster.Snapshot() =>
        _rooms.Values.Select(PeerSnapshot).OfType<RoomRosterView>().ToArray();

    async ValueTask<bool> IRoomPeerRoster.ExecuteAsync(int roomId,
        Func<RoomRosterView, CancellationToken, ValueTask> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_rooms.TryGetValue(roomId, out var room)) return false;
        var invoked = false;
        try
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                if (!_rooms.TryGetValue(roomId, out var current) || !ReferenceEquals(current, room)
                    || PeerSnapshot(room) is not { } view) return;
                invoked = true;
                await operation(view, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (!invoked) { return false; }
        return invoked;
    }

    RoomRosterView? IRoomRoster.FindBySession(long sessionId) =>
        TryGet(sessionId) is { } room ? PeerSnapshot(room) : null;

    RoomRosterView? IRoomRoster.Find(int roomId) =>
        _rooms.TryGetValue(roomId, out var room) ? PeerSnapshot(room) : null;

    private static RoomRosterView? PeerSnapshot(PlayRoom room)
    {
        lock (room.Gate)
        {
            if (room.Phase == RoomPhase.Closed) return null;
            var stage = room.Phase switch
            {
                RoomPhase.Waiting => RoomStage.Waiting,
                RoomPhase.Starting => RoomStage.Starting,
                RoomPhase.Playing => RoomStage.Playing,
                RoomPhase.Result => RoomStage.Result,
                _ => throw new InvalidOperationException($"Unknown room phase {room.Phase}."),
            };
            var seats = room.Seats.Select(seat => new RoomSeatView(seat.Slot, seat.Team, seat.UserNum, seat.Game,
                seat.RelayGeneration == room.StartGeneration ? seat.Relay : null,
                seat.RelayGeneration == room.StartGeneration && seat.RelayInitialized)).ToArray();
            return new(room.Id, stage, room.StartGeneration, room.ModeId, seats,
                GameMode.Resolve(room.ModeId).Family == GameModeFamily.Horde)
                { IsMatchRoom = room.IsMatchRoom, UdpOverride = room.UdpOverride,
                    InstallationVersion = room.PeerInstallationVersion };
        }
    }
}
