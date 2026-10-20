using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // The ultra's item request names no scene object (cn-324 sub_667CC0 passes -1 to cn-324 sub_4672B0).
    private const int ZombieKingUltraObject = -1;

    /// <summary>
    /// Answers the zombie king's ultra: the client checks its own gauge before it sends the request, so the round
    /// only checks that the sender is this round's king and confirms the item effect to that player alone.
    /// </summary>
    private async ValueTask HandleZombieKingUltraAsync(PlayRoom room, ISession session, CancellationToken ct)
    {
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            int slot;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !_options.ZombiePvpModeIds.Contains(room.ModeId)
                    || room.FindByGame(session.Id) is not { } seat || seat.Slot != room.ZombieKingSlot)
                {
                    _logger.LogInformation("Session {Id} zombie king ultra request outside its king's round ignored.", session.Id);
                    return;
                }
                slot = seat.Slot;
            }
            var sender = _countedItemUsed
                ?? throw new InvalidOperationException("The zombie king's ultra requires the build's direct item-effect sender.");
            await sender.SendAsync(session, new RoundCountedItemUsed(slot, _options.ZombieKingUltraItemKind), operationCt).ConfigureAwait(false);
            _logger.LogInformation("Room {Room}: the zombie king (seat {Seat}) used its ultra.", room.Id, slot);
        }, ct).ConfigureAwait(false);
    }
}
