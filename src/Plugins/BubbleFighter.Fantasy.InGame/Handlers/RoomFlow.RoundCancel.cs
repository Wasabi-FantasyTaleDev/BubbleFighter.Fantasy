using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Cancels a loading or playing round when a player's connection drops or the round never finishes loading:
/// the round ends with no result and every remaining player is sent to the plaza. A voluntary leave keeps the
/// round going.
/// </summary>
/// <remarks>
/// The plaza move is the waiting-room leave (RoomLeft, then the plaza entry that opens with gspGotoLobby), and the
/// client accepts it from the play stage: kr-812 sub_837170 offers each packet to the current stage before its
/// global switch, the game stage's handler (kr-812 sub_53A930, cn-717 sub_66EDB0) declines every packet, and the
/// global gspGotoLobby case (kr-812 sub_81FF00, cn-717 sub_8A6BE0) switches to the lobby stage whenever the game
/// connection is up. The plaza entry clears that packet's party flag, suppressing the party-moved notice.
/// </remarks>
internal sealed partial class RoomFlow
{
    // Under the room's gate: claims the cancel once for a loading or playing round; a settling round keeps its result.
    private static bool TryCancelRoundCore(PlayRoom room)
    {
        if (room.RoundCancelled || room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
            return false;
        room.RoundCancelled = true;
        room.CancelLoadTimeout();
        return true;
    }

    // Sends every connected remaining player to the plaza off the room executor: each return queues its own leave.
    private void ReturnCancelledRound(PlayRoom room, string reason)
    {
        ISession[] players;
        int generation;
        lock (room.Gate)
        {
            generation = room.StartGeneration;
            players = [.. room.Seats.Select(seat => seat.Game).Where(game => !game.Connection.IsClosed)];
        }
        _logger.LogInformation("Room {Room}: round cancelled ({Reason}); {Count} player(s) return to the plaza.",
            room.Id, reason, players.Length);
        room.RoundCancelTask = BackgroundWork.Detached(() => ReturnToPlazaAsync(room, generation, players));
    }

    // The returns run together, so one player's slow scene move does not hold back the others.
    private async Task ReturnToPlazaAsync(PlayRoom room, int generation, ISession[] players)
    {
        await Task.WhenAll(players.Select(async player =>
        {
            try
            {
                await ReturnCancelledPlayerAsync(player, room, generation, _lifetime).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The player's connection, the room or the plugin closed during the return; a close releases the seat.
                _logger.LogInformation("Room {Room}: session {Id} closed while returning to the plaza.", room.Id, player.Id);
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Room {Room}: returning session {Id} to the plaza after a cancelled round failed.",
                    room.Id, player.Id);
            }
        })).ConfigureAwait(false);
    }

    // Watches the round that just began loading; zero load_timeout_ms leaves loading unbounded.
    private void ArmLoadTimeout(PlayRoom room)
    {
        if (_options is not { LoadTimeoutMs: > 0 } options)
            return;
        lock (room.Gate)
        {
            var generation = room.StartGeneration;
            var watch = room.BeginLoadTimeout(_lifetime);
            room.LoadTimeoutTask = BackgroundWork.Detached(() => CancelUnloadedRoundAsync(room, generation, options.LoadTimeoutMs, watch));
        }
    }

    private async Task CancelUnloadedRoundAsync(PlayRoom room, int generation, int timeoutMs, PlayRoom.LoadTimeoutWatch watch)
    {
        try
        {
            await Task.Delay(timeoutMs, watch.Source.Token).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(_ =>
            {
                lock (room.Gate)
                {
                    // An announced countdown means every seat loaded; a later start has its own watch.
                    if (room.StartGeneration != generation || room.CountdownGeneration == generation
                        || room.Phase != RoomPhase.Starting || !TryCancelRoundCore(room))
                        return ValueTask.CompletedTask;
                }
                ReturnCancelledRound(room, $"loading exceeded {timeoutMs} ms");
                return ValueTask.CompletedTask;
            }, watch.Source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Unload, or the room stopped with this check still queued.
        }
        catch (ObjectDisposedException)
        {
            // The room closed while loading.
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: the load timeout failed.", room.Id);
        }
        finally
        {
            await room.ReleaseLoadTimeoutAsync(watch).ConfigureAwait(false);
        }
    }
}
