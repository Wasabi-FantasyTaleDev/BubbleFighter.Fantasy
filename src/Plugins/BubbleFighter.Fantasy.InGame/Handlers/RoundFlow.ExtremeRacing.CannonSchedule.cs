using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // The server is the only cannon schedule: no client timer fires a cannon, and the only caller of the shot writer is the
    // handler of S2C 718 (kr-1256 sub_67B6E0). That handler makes the receiving client fire one bullet and relay it, so
    // each command goes to exactly one seat, the origin, and the relay route forwards the shot to the others. Sending it
    // to every seat would make every client fire and multiply the bullets.
    //
    // Who the origin is, the cadence (ExtremeRacingCannonCadence) and the weapon order are server policy, not native
    // behaviour. The bullet scale is fixed at 1.0 (the client's default, kr-1256 sub_BF3700) because the relayed shot
    // carries no scale: a command with another scale would change only the origin's cannon and leave the others
    // inconsistent.
    private const float CannonCommandScale = 1.0f;

    // The cannon group word: the client accepts a cannon whose group equals it or any group when it is zero
    // (kr-1256 sub_67B6E0), and the initialization carries no group, so zero is the one value that matches.
    private const uint CannonCommandGroup = 0;

    private static ExtremeRacingInitializationState? CannonScheduleOf(PlayRoom room) =>
        room.RacingInitialization is { CannonLedger: not null, CannonCadence.HasCannons: true, CannonScheduleFailed: false } state
        && state.Generation == room.StartGeneration ? state : null;

    // Creates the generation's cannon clock beside the race clock; the previous generation's clock is drained first.
    private async ValueTask PrepareExtremeRacingCannonClockAsync(PlayRoom room, ExtremeRacingStamp stamp)
    {
        if (!_options.ExtremeRacingCannonFire || _racingCannonCommand is null) return;
        RoomDeadlineClock? previous;
        lock (room.Gate)
        {
            if (!ExtremeRacingIsPlaying(room, stamp) || CannonScheduleOf(room) is null) return;
            previous = room.ExtremeRacingCannonClock;
        }
        if (previous is not null)
        {
            previous.Cancel();
            await previous.Completion.ConfigureAwait(false);
        }
        lock (room.Gate)
        {
            if (!ExtremeRacingIsPlaying(room, stamp) || CannonScheduleOf(room) is not { } state) return;
            room.ExtremeRacingCannonClock = new(room.Executor, _lifetime, (Func<Exception, ValueTask>)(failure =>
            {
                FailExtremeRacingCannons(room, state, failure);
                return ValueTask.CompletedTask;
            }));
        }
    }

    // Arms the cadence once a race round's clock (S2C 711) has been sent, so no cannon fires before the clock runs.
    private void StartExtremeRacingCannons(PlayRoom room, ExtremeRacingStamp stamp)
    {
        lock (room.Gate)
        {
            if (!ExtremeRacingIsPlaying(room, stamp) || room.ExtremeRacingCannonClock is not { } clock
                || CannonScheduleOf(room) is not { } state) return;
            RefreshCannonOrigin(room, state);
            state.CannonCadence!.Begin(Environment.TickCount64);
            ScheduleExtremeRacingCannons(room, stamp, state, clock);
        }
    }

    // Disarms the cadence at the end of a race round and revokes its commands: a shot that arrives after the round closed
    // is not forwarded, and the next round arms the cannons again from its own clock. Callers hold the room's gate.
    private static void StopExtremeRacingCannons(PlayRoom room)
    {
        if (room.RacingInitialization is not { } state) return;
        state.CannonCadence?.End();
        state.CannonLedger?.Revoke();
    }

    // Keeps the origin valid. A departed or disconnected origin is dropped together with its outstanding commands, which
    // are never resent, and another initialized seat is chosen for later commands only. Callers hold the room's gate.
    private RoomSeat? RefreshCannonOrigin(PlayRoom room, ExtremeRacingInitializationState state)
    {
        if (state.CannonOrigin is { } current)
        {
            if (room.Seats.Contains(current) && !current.Game.Connection.IsClosed && state.Recipients.Contains(current.Game))
                return current;
            state.CannonLedger!.Revoke();
            state.CannonOrigin = null;
            _logger.LogInformation("Room {Room}: the racing cannon origin (slot {Slot}) left; its outstanding commands are revoked.",
                room.Id, current.Slot);
        }
        // Lowest slot, so the choice is deterministic and a seat that never received the initialization cannot be chosen.
        return state.CannonOrigin = room.Seats.Where(seat => !seat.Game.Connection.IsClosed && state.Recipients.Contains(seat.Game))
            .OrderBy(seat => seat.Slot).FirstOrDefault();
    }

    // Runs on the executor when a seat leaves: later commands go to a remaining seat, none are resent.
    private void HandOverExtremeRacingCannonOrigin(PlayRoom room)
    {
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || CannonScheduleOf(room) is not { CannonOrigin: not null } state) return;
            RefreshCannonOrigin(room, state);
        }
    }

    // Replacing the deadline supersedes the previous one; a due step rechecks the generation before it touches the ledger.
    private void ScheduleExtremeRacingCannons(PlayRoom room, ExtremeRacingStamp stamp,
        ExtremeRacingInitializationState state, RoomDeadlineClock clock)
    {
        if (state.CannonCadence!.NextDue is not { } due) return;
        var delay = (int)Math.Clamp(due - Environment.TickCount64, 0L, int.MaxValue);
        clock.Schedule(delay, operationCt => IssueExtremeRacingCannonsAsync(room, stamp, state, clock, operationCt));
    }

    private async ValueTask IssueExtremeRacingCannonsAsync(PlayRoom room, ExtremeRacingStamp stamp,
        ExtremeRacingInitializationState state, RoomDeadlineClock clock, CancellationToken ct)
    {
        RoomSeat? origin;
        (uint Identity, uint Weapon)[] commands;
        lock (room.Gate)
        {
            if (!ExtremeRacingIsPlaying(room, stamp) || !ReferenceEquals(room.RacingInitialization, state)
                || state.CannonScheduleFailed || !state.CannonCadence!.Active
                || !ReferenceEquals(room.ExtremeRacingCannonClock, clock)) return;
            origin = RefreshCannonOrigin(room, state);
            var ledger = state.CannonLedger!;
            // With no usable origin the due times still advance; commands are never queued for a later origin.
            commands = [.. state.CannonCadence.TakeDue(Environment.TickCount64, ledger.HasOutstanding)
                .Where(_ => origin is not null)
                .Select(due => (Identity: ledger.CommandIdentity(due.Cannon), Weapon: (uint)due.Weapon))
                .Where(command => ledger.TryIssue(command.Identity, command.Weapon))];
        }
        try
        {
            foreach (var (identity, weapon) in commands)
                await _racingCannonCommand!.SendAsync(origin!.Game, new RoundExtremeRacingCannonCommand(CannonCommandGroup,
                    identity, weapon, CannonCommandScale), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            // A command that may have been partly written cannot be told from one that was not; nothing is retried.
            FailExtremeRacingCannons(room, state, failure);
            return;
        }
        lock (room.Gate)
        {
            if (ExtremeRacingIsPlaying(room, stamp) && ReferenceEquals(room.RacingInitialization, state)
                && !state.CannonScheduleFailed && state.CannonCadence!.Active && ReferenceEquals(room.ExtremeRacingCannonClock, clock))
                ScheduleExtremeRacingCannons(room, stamp, state, clock);
        }
    }

    // Ends this generation's schedule: the cadence stops, outstanding commands are revoked and the clock is cancelled. The
    // race itself continues without cannon traffic; later generations start with a new state.
    private void FailExtremeRacingCannons(PlayRoom room, ExtremeRacingInitializationState state, Exception failure)
    {
        lock (room.Gate)
        {
            if (state.CannonScheduleFailed) return;
            state.CannonScheduleFailed = true;
            state.CannonCadence?.End();
            state.CannonLedger?.Revoke();
            if (ReferenceEquals(room.RacingInitialization, state)) room.ExtremeRacingCannonClock?.Cancel();
        }
        _logger.LogError(failure, "Room {Room}: a racing cannon command failed; the cannon schedule of this round is cancelled.", room.Id);
    }
}
