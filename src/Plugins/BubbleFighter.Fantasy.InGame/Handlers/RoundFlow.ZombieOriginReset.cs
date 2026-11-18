using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private bool IsZombieOriginMode(PlayRoom room) => ZombieOriginRolls is not null && room.ModeId == (int)GameModeId.ZombieOrigin;

    private sealed record ZombieOriginStamp(ZombieOriginRoundState Round, RoomSeat Seat, ZombieOriginRoundState.Member Member, long Life, ISession? Match);

    // Capture the original seat object and life before entering the executor. A replacement can reuse the same numeric ids.
    private ZombieOriginStamp? CaptureZombieOrigin(PlayRoom room, ISession session)
    {
        if (!IsZombieOriginMode(room) || !IsZombieOriginPlaying(room) || room.ZombieOrigin is not { } round
            || round.Generation != room.StartGeneration || session.Connection.IsClosed) return null;
        var seat = room.Seats.FirstOrDefault(s => ReferenceEquals(s.Game, session)
            || ReferenceEquals(s.Relay, session) && s.RelayInitialized && s.RelayGeneration == room.StartGeneration);
        return seat is not null && !seat.Game.Connection.IsClosed && !room.IsRefereeSeat(seat) && round.MemberFor(seat) is { } member
            ? new(round, seat, member, seat.Life.Generation, MatchLink(room, seat)) : null;
    }

    private bool ZombieOriginResetAdmitted(PlayRoom room, ISession session, RoundSpawnAccepted request, ZombieOriginStamp? stamp) =>
        stamp is not null && stamp == CaptureZombieOrigin(room, session) && ReferenceEquals(stamp.Seat.Game, session)
        && stamp.Match is not null && (ReferenceEquals(stamp.Match, session) || stamp.Seat.RelayInitialized)
        && request.Actor == RoomRegistry.SceneEntity(stamp.Seat.Slot)
        && request.Rules is { Audience: RoundRespawnAudience.Requester, Completion: RoundRespawnCompletion.MatchConfirmation,
            Admission: RoundRespawnAdmission.CleanedDeath }
        && (stamp.Member.Role == ZombieOriginRole.Fighter ? request.TaggedReset is { FirstValue: null, CharacterKind: 211 }
            : request.TaggedReset is { FirstValue: null, CharacterKind: null });

    private Task? RouteZombieOriginResetConfirmation(PlayRoom room, ISession session, RoundFeedSent packet, CancellationToken ct)
    {
        ZombieOriginStamp? stamp;
        ZombieOriginRoundState.ResetGrant? pending;
        lock (room.Gate)
        {
            if (!IsZombieOriginMode(room)) return null;
            stamp = CaptureZombieOrigin(room, session);
            pending = stamp?.Member.Pending;
        }
        if (stamp is null || pending is null || packet.TaggedReset != pending.Values
            || packet.ReportedActors is not [var actor] || actor != RoomRegistry.SceneEntity(stamp.Seat.Slot)
            || !ReferenceEquals(pending.MatchLink, session)) return Task.CompletedTask;
        return room.Executor.ExecuteAsync(async token =>
        {
            await ForwardFeedAsync(room, session, packet, token, () =>
            {
                if (CaptureZombieOrigin(room, session) != stamp || !ReferenceEquals(stamp.Member.Pending, pending)
                    || !ReferenceEquals(MatchLink(room, stamp.Seat), session) || !stamp.Seat.Life.ConfirmsGrant(session)
                    || stamp.Life != pending.Life) return false;
                stamp.Member.Pending = null;
                // kr-1256 sub_5159C0 -> 602 -> 632 -> 32926 -> kr-1256 sub_B530F0: the first reset applies zombini state 20.
                if (pending.Values.CharacterKind is 211)
                    stamp.Member.Confirm(stamp.Seat, ZombieOriginRole.ConvertedFighter, 211);
                stamp.Seat.Life.Revive();
                stamp.Seat.Movement = null;
                return true;
            }).ConfigureAwait(false);
            await CheckZombieOriginOutcomeAsync(room, token).ConfigureAwait(false);
        }, ct);
    }
}
