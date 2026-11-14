using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // A cannon shot answers one server command: the origin and direction come from the firing client's own cannon
    // object (kr-1256 sub_67B6E0), so the server can neither author nor repair a shot. It accepts or drops the frame
    // and forwards the original bytes once; no retry, no serial rewrite and no copy to the sender, whose client queued
    // its own bullet when it sent the frame (kr-1256 sub_7AB190).
    private async ValueTask HandleExtremeRacingCannonShotAsync(PlayRoom room, ISession session, RoundFeedSent feed, CancellationToken ct)
    {
        if (!_options.ExtremeRacingCannon || !IsExtremeRacingMode(room)
            || feed.ExtremeRacingCannonShot is not { } shot) return;
        RoomSeat reporter;
        int generation;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)
                    || ReferenceEquals(seat.Relay, session)) is not { } seat) return;
            reporter = seat;
            generation = room.StartGeneration;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            ISession[] targets;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                    || room.StartGeneration != generation || !room.Seats.Contains(reporter)
                    || !ReferenceEquals(reporter.Game, session) && !ReferenceEquals(reporter.Relay, session)) return;
                var state = room.RacingInitialization;
                var rejection = state is not { CannonLedger: { } ledger } || state.Generation != generation
                    ? "no cannon data was initialized for this generation"
                    : shot.Actor != RoomRegistry.SceneEntity(reporter.Slot) ? "the actor is not the sender's own seat"
                    : !state.Recipients.Contains(reporter.Game) ? "the sender was not initialized"
                    : !IsFinite(shot.Origin) || !IsFinite(shot.Direction) ? "a vector is not finite"
                    : !ledger.TryConsume(shot.CannonIdentity, shot.Weapon) ? "no matching command is outstanding"
                    : null;
                if (rejection is not null)
                {
                    _logger.LogWarning("Room {Room} session {Session}: rejected racing cannon shot of cannon {Cannon} weapon {Weapon}: {Reason}.",
                        room.Id, session.Id, shot.CannonIdentity, shot.Weapon, rejection);
                    return;
                }
                // Only participants that received the initialization hold the cannon objects the bullet needs.
                var recipients = room.Seats.Where(seat => !ReferenceEquals(seat, reporter) && state!.Recipients.Contains(seat.Game)).ToArray();
                // The same link choice as every other peer frame: the seat's match connection, or its game connection off the relay.
                targets = [.. recipients.Select(seat => feed.OnRelay || _options.NativeMatchRelay ? MatchLink(room, seat) : seat.Game)
                    .OfType<ISession>()];
                if (targets.Length != recipients.Length)
                    _logger.LogWarning("Room {Room}: racing cannon shot {Cannon} from slot {Slot} reached {Reached} of {Expected} participants; it is not repeated.",
                        room.Id, shot.CannonIdentity, reporter.Slot, targets.Length, recipients.Length);
            }
            var received = new RoundFeedReceived(feed.Code, feed.Payload);
            foreach (var target in targets)
                await _feed!.SendAsync(target, received, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static bool IsFinite(System.Numerics.Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
