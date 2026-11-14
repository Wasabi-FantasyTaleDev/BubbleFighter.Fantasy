using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>Applies a completed local equipment snapshot to its current round without interpreting preset pages as installed weapons.</summary>
    internal async ValueTask OnEquipmentViewPublishedAsync(EquipmentViewPublished publication, CancellationToken ct)
    {
        if (publication.IsSceneReplay || _criticalAuthority is null || publication.Protocol != _options.GameProtocol
            || _registry.TryGame(publication.Session) is not { } owned)
            return;
        var (room, seat) = owned;
        RoundCriticalState state;
        lock (room.Gate)
        {
            if (!ReferenceEquals(seat.Game, publication.Session) || seat.UserNum != publication.View.UserNum
                || seat.Critical is not { } current || !CurrentCritical(room, seat, current)) return;
            state = current;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            PreparedCriticalTransition transition;
            lock (room.Gate)
            {
                if (!CurrentCritical(room, seat, state)) return;
                var current = state.Current;
                MatchSeedEquipment equipment;
                MatchCriticalSeedUpdate candidate;
                try
                {
                    equipment = new MatchSeedEquipment(current.Equipment.ModeId, publication.View.EquippedItemIds,
                        current.Equipment.PrimarySpecification, current.Equipment.SidearmSpecification,
                        publication.View.ItemAttributes);
                    candidate = _criticalAuthority.WithPublishedEquipment(current, equipment);
                    if (candidate == current) return;
                    candidate = candidate with { Revision = checked(current.Revision + 1) };
                    _criticalAuthority.Validate(candidate);
                }
                catch (Exception failure)
                {
                    state.Failure = failure;
                    throw;
                }
                seat.CharacterId = publication.View.CharacterId;
                seat.EquippedItemIds = equipment.WornItemIds;
                seat.ItemAttributes = equipment.ItemAttributes;
                transition = new(seat, state, candidate, state.ActiveUntil);
            }
            await PublishCriticalTransitionAsync(room, transition, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private sealed record PreparedCriticalTransition(RoomSeat Seat, RoundCriticalState State,
        MatchCriticalSeedUpdate Request, long ActiveUntil);

    // The caller holds the room gate and has checked the reporting actor's ownership.
    private PreparedCriticalTransition? PrepareCriticalTransition(PlayRoom room, RoomSeat sender, RoundFeedSent feed)
    {
        if (_criticalAuthority is null || room.Phase != RoomPhase.Playing) return null;
        RoomSeat? target;
        MatchCriticalSeedUpdate next;
        long activeUntil;
        if (feed.CriticalWeaponState is { Slot: 0 or 1 } weapon)
        {
            target = room.Seats.SingleOrDefault(seat => RoomRegistry.SceneEntity(seat.Slot) == weapon.Actor);
            if (!ReferenceEquals(target, sender) || target?.Critical is not { } weaponState || !CurrentCritical(room, target, weaponState))
                return null;
            var current = weaponState.Current;
            if ((weapon.Slot == 0 ? current.EffectivePrimarySpecification : current.EffectiveSidearmSpecification) == weapon.Specification)
                return null;
            // cn-324 sub_469D20 reports the installed specification; applying mode substitution again would change it.
            next = weapon.Slot == 0 ? current with { EffectivePrimarySpecification = weapon.Specification }
                : current with { EffectiveSidearmSpecification = weapon.Specification };
            activeUntil = weaponState.ActiveUntil;
        }
        else if (feed.CriticalEffectActor is { } actor)
        {
            target = room.Seats.SingleOrDefault(seat => RoomRegistry.SceneEntity(seat.Slot) == actor);
            if (target?.Critical is not { } effectState || !CurrentCritical(room, target, effectState)) return null;
            var duration = _criticalAuthority.TimedEffectDuration(effectState.Current.Equipment);
            next = effectState.Current with { TimedEffectActive = duration > 0 };
            activeUntil = duration > 0 ? checked(Environment.TickCount64 + duration) : 0;
            if (duration == 0 && !effectState.Current.TimedEffectActive) return null;
        }
        else return null;
        var state = target.Critical!;
        next = next with { Revision = checked(state.Current.Revision + 1) };
        _criticalAuthority.Validate(next);
        return new(target, state, next, activeUntil);
    }

    // Runs on the room executor after forwarding the native trigger, so the beneficiary's relay receives it before its table.
    private async ValueTask PublishCriticalTransitionAsync(PlayRoom room, PreparedCriticalTransition transition, CancellationToken ct)
    {
        var state = transition.State;
        lock (room.Gate)
        {
            if (!CurrentCritical(room, transition.Seat, state)) return;
            state.Current = transition.Request;
            state.ActiveUntil = transition.ActiveUntil;
        }
        try
        {
            await _criticalAuthority!.SendAsync(state.Relay, transition.Request, ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            lock (room.Gate) state.Failure = failure;
            throw;
        }
        lock (room.Gate)
        {
            if (!CurrentCritical(room, transition.Seat, state)) return;
            if (state.ActiveUntil > 0 && !state.TimerStarted)
            {
                state.TimerStarted = true;
                var token = state.Cancellation.Token;
                state.TimerTask = BackgroundWork.Detached(() => ExpireCriticalEffectAsync(room, transition.Seat, state, token));
            }
            if (state.TimerStarted) state.WakeTimer();
        }
    }

    private static bool CurrentCritical(PlayRoom room, RoomSeat seat, RoundCriticalState state) =>
        !state.Cancellation.IsCancellationRequested && state.OpeningCompleted && state.Failure is null
        && room.Phase == RoomPhase.Playing && room.StartGeneration == state.Opening.Generation
        && ReferenceEquals(room.FindByGame(seat.Game.Id), seat) && ReferenceEquals(seat.Critical, state)
        && ReferenceEquals(MatchLink(room, seat), state.Relay);

    private async Task ExpireCriticalEffectAsync(PlayRoom room, RoomSeat seat, RoundCriticalState state, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                long remaining;
                lock (room.Gate)
                {
                    if (!CurrentCritical(room, seat, state)) return;
                    remaining = state.ActiveUntil == 0 ? Timeout.Infinite : Math.Max(0, state.ActiveUntil - Environment.TickCount64);
                }
                if (remaining != 0 && await state.TimerChanged.WaitAsync(
                    remaining == Timeout.Infinite ? Timeout.Infinite : checked((int)Math.Min(remaining, int.MaxValue)), ct).ConfigureAwait(false))
                    continue;
                await room.Executor.ExecuteAsync(async operationCt =>
                {
                    MatchCriticalSeedUpdate request;
                    lock (room.Gate)
                    {
                        if (!CurrentCritical(room, seat, state) || state.ActiveUntil == 0)
                        {
                            return;
                        }
                        if (state.ActiveUntil > Environment.TickCount64) return;
                        request = state.Current with { Revision = checked(state.Current.Revision + 1), TimedEffectActive = false };
                        _criticalAuthority!.Validate(request);
                        state.Current = request;
                        state.ActiveUntil = 0;
                    }
                    await _criticalAuthority!.SendAsync(state.Relay, request, operationCt).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (Exception failure)
        {
            lock (room.Gate) state.Failure = failure;
            _logger.LogError(failure, "Room {Room} slot {Slot}: timed critical expiry failed for generation {Generation}.",
                room.Id, seat.Slot, state.Opening.Generation);
        }
        finally { state.ReleaseTimer(); }
    }
}
