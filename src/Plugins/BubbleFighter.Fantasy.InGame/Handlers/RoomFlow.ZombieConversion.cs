using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal Func<PlayRoom, CancellationToken, ValueTask>? TeamConvertedDuringRound { get; set; }

    internal async ValueTask<bool> TryPveZombieRevivalAsync(ISession session, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room) return false;
        lock (room.Gate)
            if (room.ModeId != _options?.ZombieConversionModeId || room.FindByGame(session.Id) is not { Team: 15 })
                return false;
        // cn-324 sub_68C290 revives an existing zombie on the same team-15 notification.
        await ConvertZombieAsync(room, session, ct, pveRevival: true).ConfigureAwait(false);
        return true;
    }
    private Task ConvertZombieAsync(PlayRoom room, ISession session, CancellationToken ct, bool pveRevival = false) =>
        room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomTeamConverted change;
            lock (room.Gate)
            {
                // cn-324 sub_68AE20 / kr-812 sub_78BCC0 request conversion after death; cn-324 sub_68C290 and
                // kr-812 sub_78D340 use human 2 and zombie 15. A build whose client times its own respawn
                // (server_revives false) is dead from its death report until its spawn is accepted.
                if (room.Phase != RoomPhase.Playing || room.ModeId != _options?.ZombieConversionModeId
                    || room.FindByGame(session.Id) is not { Team: 2 or 15 } seat
                    || (!seat.AwaitingRespawn && seat.LastDeathActor is null && !(pveRevival && seat.Team == 15)))
                    return;
                seat.Team = 15;
                seat.AwaitingRespawn = false;
                seat.InstantRevivalPending = false;
                seat.LastDeathActor = null;
                change = new(seat.Slot, 15);
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _teamConverted!.SendAsync(member.Game, change, operationCt).ConfigureAwait(false);
            if (TeamConvertedDuringRound is { } converted)
                await converted(room, operationCt).ConfigureAwait(false);
        }, ct);
}
