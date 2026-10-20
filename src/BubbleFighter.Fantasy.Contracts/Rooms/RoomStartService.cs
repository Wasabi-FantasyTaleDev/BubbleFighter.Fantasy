namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>
/// Optional service a row can publish to start one of its live rooms by id. The administration API
/// can request a start without a connected client. The room row remains responsible for validating its phase
/// and running the normal start sequence.
/// </summary>
public interface IRoomStartService
{
    /// <summary>
    /// Starts the room with the given id, sending the documented start chain to
    /// every seat. A missing room or a room that is not waiting is an error.
    ///
    /// </summary>
    /// <param name="roomId">The room id handed out at create time.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask StartAsync(int roomId, CancellationToken ct);

    /// <summary>Lists the live room ids and their seat counts.</summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<(int RoomId, int Seats, string Phase)>> ListAsync(CancellationToken ct);
}
