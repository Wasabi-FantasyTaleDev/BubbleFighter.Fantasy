using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal ValueTask HandleInstantRevivalAsync(ISession session, RoundInstantRevivalRequested request, CancellationToken ct) =>
        RequestImmediateSpawnAsync(session, request.Actor, request.ActorSeat, consumeItem: true, ct);

    internal ValueTask HandlePveRevivalAsync(ISession session, RoundPveRevivalRequested request, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room) return ValueTask.CompletedTask;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || !_options.PveRevivalModes.Contains(room.ModeId)
                || room.FindByGame(session.Id) is not { } seat) return ValueTask.CompletedTask;
            // The native death input can precede its timed respawn report on the same connection.
            if (!seat.AwaitingRespawn || seat.LastDeathActor is null)
            {
                seat.PveRevivalRequested = true;
                return ValueTask.CompletedTask;
            }
        }
        return RequestImmediateSpawnAsync(session, null, null, consumeItem: false, ct);
    }

    private async ValueTask RequestImmediateSpawnAsync(ISession session, int? actor, int? actorSeat, bool consumeItem, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat seat;
            int ownedActor;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { AwaitingRespawn: true, InstantRevivalPending: false, LastDeathActor: { } deathActor } dead)
                    return;
                if (consumeItem ? _options.InstantRevivalItemId <= 0 : !_options.PveRevivalModes.Contains(room.ModeId))
                    return;
                if (actor is { } submitted && (submitted != deathActor || !OwnsActor(dead, submitted, actorSeat))) return;
                if (_spawnOffered is null) return;
                seat = dead;
                ownedActor = deathActor;
            }
            if (consumeItem)
            {
                var remaining = await _revivalInventory!.ConsumeItemAsync(seat.UserNum, _options.InstantRevivalItemId, operationCt).ConfigureAwait(false);
                await _revivalInventoryPush!.PushInventoryAsync(session, seat.UserNum, operationCt).ConfigureAwait(false);
                if (remaining is null)
                {
                    _logger.LogWarning("Session {Session} instant revival refused: no available item {Item}.", session.Id, _options.InstantRevivalItemId);
                    return;
                }
                _logger.LogInformation("Session {Session} consumed instant revival item {Item}; remaining={Count}.", session.Id, _options.InstantRevivalItemId, remaining);
            }
            lock (room.Gate) seat.InstantRevivalPending = true;
            if (_reviving.TryRemove(session.Id, out var wait)) wait.Shorten(_options.RespawnDelayMaxMs);
            await _spawnOffered!.SendAsync(session, new RoundSpawnOffered(ownedActor, seat.Slot), operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
