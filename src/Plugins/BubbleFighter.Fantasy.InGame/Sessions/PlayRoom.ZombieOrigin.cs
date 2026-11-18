using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    internal ZombieOriginRoundState? ZombieOrigin { get; set; }
}

/// <summary>Eligibility is fixed at round start. A late join or replacement cannot inherit a selected seat.</summary>
internal sealed class ZombieOriginRoundState
{
    private readonly Dictionary<RoomSeat, Prototype> _selected = [];
    private readonly Dictionary<RoomSeat, Member> _members;
    internal int Generation { get; }

    internal ZombieOriginRoundState(int generation, RoomSeat[] candidates, int quota)
    {
        Generation = generation;
        _members = candidates.ToDictionary(s => s, s => new Member(s.Team));
        var count = Math.Min(quota, candidates.Length - 1);
        for (var i = 0; i < count; i++)
        {
            var j = RandomNumberGenerator.GetInt32(i, candidates.Length);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            _selected.Add(candidates[i], new(RandomNumberGenerator.GetInt32(10_000), candidates[i].EquippedItemIds?.Contains(101060) == true));
        }
    }

    internal Prototype? For(RoomSeat seat) => _selected.GetValueOrDefault(seat);

    internal Member? MemberFor(RoomSeat seat) => _members.GetValueOrDefault(seat);

    internal ZombieOriginOutcome Outcome { get; private set; }

    internal bool? AreOpponents(PlayRoom room, int firstSlot, int secondSlot)
    {
        if (room.Seats.FirstOrDefault(s => s.Slot == firstSlot) is not { } first || MemberFor(first) is not { } a
            || room.Seats.FirstOrDefault(s => s.Slot == secondSlot) is not { } second || MemberFor(second) is not { } b)
            return null;
        return (a.Role == ZombieOriginRole.Fighter) != (b.Role == ZombieOriginRole.Fighter);
    }

    // Keep the complete opening roster. An absent seat, including a replacement with the same slot, is not a pop.
    internal bool Decide(PlayRoom room, bool timeExpired)
    {
        if (Outcome != ZombieOriginOutcome.Undecided || room.StartGeneration != Generation
            || room.DecidedWinnerSlots is not null || room.DecidedWinnerTeam is not null) return false;
        var roster = new ZombieOriginParticipant[_members.Count];
        var index = 0;
        foreach (var (seat, member) in _members)
        {
            var present = room.Seats.Contains(seat) && !seat.Game.Connection.IsClosed;
            var role = For(seat) is { Confirmed: false } ? ZombieOriginRole.Unassigned : member.Role;
            var condition = member.PoppedLife == seat.Life.Generation ? ZombieOriginCondition.Popped
                : !present ? ZombieOriginCondition.Absent
                : seat.Life.State is SeatLife.Alive or SeatLife.Bubble ? ZombieOriginCondition.InPlay : ZombieOriginCondition.Unknown;
            roster[index++] = new(role, condition);
        }
        var decided = ZombieOriginVictory.Decide(roster, timeExpired);
        if (decided == ZombieOriginOutcome.Undecided) return false;
        var zombieWin = decided == ZombieOriginOutcome.ZombiesConvertedAllFighters;
        room.DecidedWinnerSlots = _members.Where(p => room.Seats.Contains(p.Key) && !p.Key.Game.Connection.IsClosed
            && (p.Value.Role is ZombieOriginRole.Prototype or ZombieOriginRole.ConvertedFighter) == zombieWin)
            .Select(p => p.Key.Slot).ToHashSet();
        Outcome = decided;
        return true;
    }

    internal void RestoreWaitingTeams(IEnumerable<RoomSeat> seats)
    {
        foreach (var seat in seats)
            if (MemberFor(seat) is { Kind: not null } member) seat.Team = member.WaitingTeam;
    }

    internal sealed class Member(byte waitingTeam)
    {
        internal byte WaitingTeam { get; } = waitingTeam;
        internal ZombieOriginRole Role { get; private set; } = ZombieOriginRole.Fighter;
        internal int? Kind { get; private set; }
        internal ResetGrant? Pending { get; set; }
        internal long? PoppedLife { get; set; }

        internal void Confirm(RoomSeat seat, ZombieOriginRole role, int kind)
        {
            Role = role;
            Kind = kind;
            seat.Team = 34; // kr-1256 sub_B53430 -> kr-1256 sub_B53BF0: character and room seat change together.
        }
    }

    internal sealed record ResetGrant(long Life, ISession MatchLink, RoundTaggedReset Values);

    internal sealed class Prototype(int roll, bool hasForceItem)
    {
        internal int Roll { get; } = roll;
        internal bool HasForceItem { get; } = hasForceItem;
        internal bool Delivered { get; set; }
        internal bool Confirmed { get; set; }
    }
}
