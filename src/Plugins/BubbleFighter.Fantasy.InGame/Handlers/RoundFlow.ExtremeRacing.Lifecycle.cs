using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private sealed record ExtremeRacingStamp(int Generation, ExtremeRacingRoundState Race,
        ExtremeRacingInitializationState? Initialization = null);

    private static bool OwnsExtremeRacing(PlayRoom room, ExtremeRacingStamp stamp) =>
        room.StartGeneration == stamp.Generation && ReferenceEquals(room.ExtremeRacing, stamp.Race)
        && (stamp.Initialization is null || ReferenceEquals(room.RacingInitialization, stamp.Initialization));

    private static bool ExtremeRacingIsPlaying(PlayRoom room, ExtremeRacingStamp stamp) =>
        OwnsExtremeRacing(room, stamp) && room.Phase == RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping;

    private async ValueTask RunExtremeRacingTransitionAsync(PlayRoom room, ExtremeRacingStamp stamp,
        Func<ValueTask> transition)
    {
        try
        {
            await transition().ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CancelExtremeRacingAsync(room, stamp).ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask CancelExtremeRacingAsync(PlayRoom room, ExtremeRacingStamp stamp)
    {
        lock (room.Gate)
        {
            if (!OwnsExtremeRacing(room, stamp) || room.Phase is RoomPhase.Waiting or RoomPhase.Closed)
                return ValueTask.CompletedTask;
            if (stamp.Race.CleanupTask is { } existing) return new(existing);
            if (room.IsStopping) return ValueTask.CompletedTask;
            room.RoundCancelled = true;
            room.ExtremeRacingClock?.Cancel();
            room.ExtremeRacingCannonClock?.Cancel();
            IConnection[] connections = [.. room.Seats.SelectMany(seat => new[] { seat.Game.Connection, seat.Relay?.Connection })
                .OfType<IConnection>().Distinct<IConnection>(ReferenceEqualityComparer.Instance)];
            // A partially published stage cannot be replayed; one task owns this generation's bounded connection set.
            var cleanup = BackgroundWork.Detached(() => CloseExtremeRacingConnectionsAsync(room, stamp, connections));
            room.ExtremeRacingCleanupTask = stamp.Race.CleanupTask = cleanup;
            return new(cleanup);
        }
    }

    private async Task CloseExtremeRacingConnectionsAsync(PlayRoom room, ExtremeRacingStamp stamp, IConnection[] connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                ValueTask closing;
                lock (room.Gate)
                {
                    if (!OwnsExtremeRacing(room, stamp) || room.Phase is RoomPhase.Waiting or RoomPhase.Closed) return;
                    if (connection.IsClosed || !room.Seats.Any(seat => ReferenceEquals(seat.Game.Connection, connection)
                        || ReferenceEquals(seat.Relay?.Connection, connection))) continue;
                    closing = connection.CloseAsync("Racing state could not be synchronized; round cancelled.");
                }
                await closing.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Room {Room}: connection {Connection} failed to close after racing cancellation.",
                    room.Id, connection.Id);
            }
        }
    }
}
