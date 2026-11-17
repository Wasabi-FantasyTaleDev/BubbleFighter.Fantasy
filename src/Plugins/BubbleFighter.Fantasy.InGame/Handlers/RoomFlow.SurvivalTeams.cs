using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    private bool UsesSurvivalTeams(int mode) => _options?.SurvivalTeamAssignments == true
        && mode is (int)GameModeId.Area32 or (int)GameModeId.FinalZone;

    // kr-1256 sub_6A82A0 and sub_50A4D0 compare team identity even in individual Area32 rounds.
    private bool TryPlanSurvivalTeams(PlayRoom room, int mode, bool reset,
        out IReadOnlyDictionary<int, byte> assignments)
    {
        assignments = new Dictionary<int, byte>();
        if (!UsesSurvivalTeams(mode)) return true;
        var seats = room.Seats.Where(seat => !room.IsRefereeSeat(seat) && seat.Team != 36).ToArray();
        if (mode == (int)GameModeId.Area32)
        {
            assignments = seats.ToDictionary(seat => seat.Slot, seat => checked((byte)(seat.Slot + 1)));
            return true;
        }
        var pairs = new Dictionary<int, int>();
        if (reset)
            foreach (var seat in seats.OrderBy(seat => seat.Slot))
                if (!pairs.ContainsKey(seat.Slot) && SurvivalPartnerSlot(seats, seat.UserNum) is { } partner
                    && !pairs.ContainsKey(partner))
                {
                    pairs.Add(seat.Slot, partner);
                    pairs.Add(partner, seat.Slot);
                }
        var members = seats.Select(seat => new SurvivalTeamMember(seat.Slot, reset ? (byte)0 : seat.Team,
            pairs.TryGetValue(seat.Slot, out var partner) ? partner : null)).ToArray();
        return SurvivalTeams.TryAssign(members, _options!.SurvivalMaximumGroups, out assignments);
    }

    private int? SurvivalPartnerSlot(IReadOnlyList<RoomSeat> seats, int userNum)
    {
        if (_options?.SurvivalPairParties != true || _parties?.PartyOf(userNum) is not { Members.Count: 2 } party
            || party.Members.Count(member => member.UserNum == userNum) != 1)
            return null;
        var other = party.Members.Single(member => member.UserNum != userNum).UserNum;
        if (_parties.PartyOf(other) is not { Members.Count: 2 } reciprocal
            || !party.Members.Select(member => member.UserNum).Order()
                .SequenceEqual(reciprocal.Members.Select(member => member.UserNum).Order()))
            return null;
        return seats.SingleOrDefault(seat => seat.UserNum == other && seat.Team != 36)?.Slot;
    }

    private bool TryPlanSurvivalJoin(PlayRoom room, int userNum, out byte? team)
    {
        team = null;
        if (!UsesSurvivalTeams(room.ModeId) || room.ModeId != (int)GameModeId.FinalZone) return true;
        var seats = room.Seats.Where(seat => !room.IsRefereeSeat(seat) && seat.Team != 36).ToArray();
        // The temporary identity is outside the real seat table; admission chooses the actual free seat.
        var pending = room.MaxSeats;
        var partner = SurvivalPartnerSlot(seats, userNum);
        var members = seats.Select(seat => new SurvivalTeamMember(seat.Slot, seat.Team,
            seat.Slot == partner ? pending : null)).Append(new(pending, 0, partner)).ToArray();
        if (!SurvivalTeams.TryAssign(members, _options!.SurvivalMaximumGroups, out var assignments)) return false;
        // Entry may assign the new seat only. Existing manual choices remain stable for every peer.
        if (seats.Any(seat => assignments[seat.Slot] != seat.Team)) return false;
        team = assignments[pending];
        return true;
    }

    private static void ApplySurvivalTeams(PlayRoom room, IReadOnlyDictionary<int, byte> assignments)
    {
        foreach (var seat in room.Seats)
            if (assignments.TryGetValue(seat.Slot, out var team)) seat.Team = team;
    }

    private void ApplyModeTeams(PlayRoom room, int previousMode, IReadOnlyDictionary<int, byte> assignments)
    {
        if (UsesSurvivalTeams(room.ModeId))
            ApplySurvivalTeams(room, assignments);
        else if (UsesSurvivalTeams(previousMode))
        {
            foreach (var seat in room.Seats.Where(seat => seat.Team != 36)) seat.Team = 0;
            room.AssignSpawnTeams();
        }
    }

    private async ValueTask PublishModeTeamsAsync(PlayRoom room, RoomSeat[] members, CancellationToken ct)
    {
        RoomSeatUpdated[] updates;
        lock (room.Gate)
        {
            var view = View(room);
            updates = room.Seats.Select(seat => new RoomSeatUpdated(room.Id, View(seat))
                { Room = view, Reason = RoomSeatUpdateReason.TeamChanged }).ToArray();
        }
        foreach (var member in members)
            foreach (var update in updates)
                await _seatUpdated.SendAsync(member.Game, update, ct).ConfigureAwait(false);
    }
}
