using System.Collections.ObjectModel;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Describes one participant's current group and an optional verified party partner.</summary>
internal readonly record struct SurvivalTeamMember(int Slot, byte Team, int? PairSlot = null);

/// <summary>Plans bounded solo or duo groups without changing room seats.</summary>
internal static class SurvivalTeams
{
    // kr-1256 sub_97D7B0 creates group controls 1..12.
    internal const int MaximumGroups = 12;

    // kr-1256 sub_9DBFD0 renders the native failure for teams of three or more.
    internal const int MaximumMembersPerGroup = 2;

    private static readonly IReadOnlyDictionary<int, byte> EmptyAssignments =
        new ReadOnlyDictionary<int, byte>(new Dictionary<int, byte>());

    /// <summary>Preserves existing groups and assigns each pending solo or verified pair to an empty group.</summary>
    internal static bool TryAssign(IReadOnlyList<SurvivalTeamMember> members, int maximumGroups,
        out IReadOnlyDictionary<int, byte> assignments)
    {
        assignments = EmptyAssignments;
        if (!TryReadMembers(members, maximumGroups, out var ordered, out var counts)) return false;

        var proposed = ordered.ToDictionary(member => member.Slot, member => member.Team);

        // Reserve an assigned partner's remaining place before allocating unrelated solo groups.
        foreach (var member in ordered)
        {
            if (member.PairSlot is not { } partnerSlot || member.Slot > partnerSlot) continue;
            var team = proposed[member.Slot];
            var partnerTeam = proposed[partnerSlot];
            if (team != 0 && partnerTeam != 0)
            {
                if (team != partnerTeam) return false;
                continue;
            }
            if (team == 0 && partnerTeam == 0) continue;
            var assignedTeam = team != 0 ? team : partnerTeam;
            if (counts[assignedTeam] == MaximumMembersPerGroup) return false;
            proposed[team == 0 ? member.Slot : partnerSlot] = assignedTeam;
            counts[assignedTeam]++;
        }

        foreach (var member in ordered)
        {
            if (proposed[member.Slot] != 0) continue;
            var team = 1;
            while (team <= maximumGroups && counts[team] != 0) team++;
            if (team > maximumGroups) return false;
            proposed[member.Slot] = checked((byte)team);
            counts[team] = 1;
            if (member.PairSlot is not { } partnerSlot) continue;
            proposed[partnerSlot] = checked((byte)team);
            counts[team]++;
        }

        assignments = new ReadOnlyDictionary<int, byte>(proposed);
        return true;
    }

    /// <summary>Finds the next nonfull group for one manual move, without moving its party partner.</summary>
    internal static byte? NextAvailableGroup(IReadOnlyList<SurvivalTeamMember> members, int slot,
        int maximumGroups)
    {
        if (!TryReadMembers(members, maximumGroups, out var ordered, out var counts)) return null;
        var index = Array.FindIndex(ordered, member => member.Slot == slot);
        if (index < 0) throw new ArgumentException("The moving seat must be present in the roster.", nameof(slot));
        var current = ordered[index].Team;
        if (current != 0) counts[current]--;
        for (var offset = 1; offset <= maximumGroups; offset++)
        {
            var next = (current + offset - 1) % maximumGroups + 1;
            if (counts[next] < MaximumMembersPerGroup) return checked((byte)next);
        }
        return null;
    }

    private static bool TryReadMembers(IReadOnlyList<SurvivalTeamMember> members, int maximumGroups,
        out SurvivalTeamMember[] ordered, out int[] counts)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (maximumGroups is < 1 or > MaximumGroups)
            throw new ArgumentOutOfRangeException(nameof(maximumGroups), maximumGroups,
                "The group limit must be between one and twelve.");

        ordered = [];
        counts = new int[maximumGroups + 1];
        if (members.Count > maximumGroups * MaximumMembersPerGroup) return false;
        var bySlot = new Dictionary<int, SurvivalTeamMember>(members.Count);
        foreach (var member in members)
        {
            if (member.Slot < 0)
                throw new ArgumentException("Seat identities cannot be negative.", nameof(members));
            if (!bySlot.TryAdd(member.Slot, member))
                throw new ArgumentException("Seat identities must be unique.", nameof(members));
        }
        foreach (var member in bySlot.Values)
        {
            if (member.PairSlot is not { } partnerSlot) continue;
            if (partnerSlot == member.Slot || !bySlot.TryGetValue(partnerSlot, out var partner) ||
                partner.PairSlot != member.Slot)
                throw new ArgumentException("A party partner must name a different, reciprocal seat.", nameof(members));
        }
        foreach (var member in bySlot.Values)
        {
            if (member.Team > maximumGroups) return false;
            if (member.Team != 0 && ++counts[member.Team] > MaximumMembersPerGroup) return false;
        }
        ordered = bySlot.Values.OrderBy(member => member.Slot).ToArray();
        return true;
    }
}
