using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks bounded group allocation, party preservation and manual team selection.</summary>
public sealed class SurvivalTeamsTests
{
    /// <summary>Unassigned solo seats receive separate groups in seat order without changing the input.</summary>
    [Fact]
    public void SoloAllocationIsStableAndDoesNotMutateSeats()
    {
        SurvivalTeamMember[] members = [new(9, 0), new(2, 0), new(5, 0)];
        var before = members.ToArray();
        Assert.True(SurvivalTeams.TryAssign(members, 12, out var assigned));
        Assert.Equal((byte)1, assigned[2]);
        Assert.Equal((byte)2, assigned[5]);
        Assert.Equal((byte)3, assigned[9]);
        Assert.Equal(before, members);
        Assert.True(SurvivalTeams.TryAssign(members.Reverse().ToArray(), 12, out var reversed));
        Assert.Equal(assigned.OrderBy(pair => pair.Key), reversed.OrderBy(pair => pair.Key));
    }

    /// <summary>Valid manual pairs and noncontiguous groups survive allocation of new solo seats.</summary>
    [Fact]
    public void ExistingManualGroupsArePreserved()
    {
        SurvivalTeamMember[] members = [new(0, 4), new(1, 4), new(2, 1), new(3, 0), new(4, 0)];
        Assert.True(SurvivalTeams.TryAssign(members, 12, out var assigned));
        Assert.Equal(new byte[] { 4, 4, 1, 2, 3 }, members.Select(member => assigned[member.Slot]));
    }

    /// <summary>Twelve solo groups fit, but a thirteenth solo is refused instead of being paired silently.</summary>
    [Fact]
    public void ThirteenthSoloDoesNotJoinAnUnrelatedGroup()
    {
        var twelve = Enumerable.Range(0, 12).Select(slot => new SurvivalTeamMember(slot, 0)).ToArray();
        Assert.True(SurvivalTeams.TryAssign(twelve, 12, out var assigned));
        Assert.Equal(Enumerable.Range(1, 12).Select(team => (byte)team),
            twelve.Select(member => assigned[member.Slot]));
        Assert.False(SurvivalTeams.TryAssign([.. twelve, new(12, 0)], 12, out var rejected));
        Assert.Empty(rejected);
    }

    /// <summary>A verified two-person party shares one group while unrelated solo seats remain separate.</summary>
    [Fact]
    public void ReciprocalPartyUsesOneGroup()
    {
        SurvivalTeamMember[] members = [new(9, 0, 2), new(5, 0), new(2, 0, 9), new(1, 0)];
        Assert.True(SurvivalTeams.TryAssign(members, 12, out var assigned));
        Assert.Equal((byte)1, assigned[1]);
        Assert.Equal((byte)2, assigned[2]);
        Assert.Equal(assigned[2], assigned[9]);
        Assert.Equal((byte)3, assigned[5]);
    }

    /// <summary>An existing partner's group is reserved before unrelated new solos consume empty groups.</summary>
    [Fact]
    public void IncomingPartyMemberJoinsAssignedPartner()
    {
        SurvivalTeamMember[] members = [new(0, 0), new(2, 0, 9), new(9, 1, 2)];
        Assert.True(SurvivalTeams.TryAssign(members, 2, out var assigned));
        Assert.Equal((byte)2, assigned[0]);
        Assert.Equal((byte)1, assigned[2]);
        Assert.Equal((byte)1, assigned[9]);
    }

    /// <summary>Explicitly paired seats already sharing a valid group retain that assignment.</summary>
    [Fact]
    public void ExistingPartyGroupRemainsUnchanged()
    {
        Assert.True(SurvivalTeams.TryAssign([new(1, 7, 6), new(6, 7, 1)], 12, out var assigned));
        Assert.Equal((byte)7, assigned[1]);
        Assert.Equal((byte)7, assigned[6]);
    }

    /// <summary>A party request cannot overwrite two existing manual group choices.</summary>
    [Fact]
    public void ConflictingAssignedPartyFailsWithoutPartialOutput()
    {
        Assert.False(SurvivalTeams.TryAssign([new(0, 0), new(1, 1, 6), new(6, 2, 1)], 12,
            out var assigned));
        Assert.Empty(assigned);
    }

    /// <summary>A verified partner cannot enter a group already occupied by two participants.</summary>
    [Fact]
    public void IncomingPartnerCannotOverfillManualGroup()
    {
        Assert.False(SurvivalTeams.TryAssign([new(1, 1, 6), new(6, 0, 1), new(9, 1)], 12,
            out var assigned));
        Assert.Empty(assigned);
    }

    /// <summary>Eight verified pairs fit the existing sixteen-seat room without creating extra groups.</summary>
    [Fact]
    public void SixteenSeatsFitWhenPartyMembershipIsExplicit()
    {
        var members = Enumerable.Range(0, 16)
            .Select(slot => new SurvivalTeamMember(slot, 0, slot ^ 1)).ToArray();
        Assert.True(SurvivalTeams.TryAssign(members, 12, out var assigned));
        Assert.Equal(8, assigned.Values.Distinct().Count());
        Assert.All(assigned.GroupBy(pair => pair.Value), group => Assert.Equal(2, group.Count()));
    }

    /// <summary>The deployment's smaller group limit still separates solos and permits verified pairs.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    public void ConfiguredLimitBoundsAllocation(int maximumGroups)
    {
        var solos = Enumerable.Range(0, maximumGroups + 1)
            .Select(slot => new SurvivalTeamMember(slot, 0)).ToArray();
        Assert.False(SurvivalTeams.TryAssign(solos, maximumGroups, out var rejected));
        Assert.Empty(rejected);
        var pairs = Enumerable.Range(0, maximumGroups * 2)
            .Select(slot => new SurvivalTeamMember(slot, 0, slot ^ 1)).ToArray();
        Assert.True(SurvivalTeams.TryAssign(pairs, maximumGroups, out var assigned));
        Assert.Equal(maximumGroups, assigned.Values.Distinct().Count());
    }

    /// <summary>Malformed or out-of-range group assignments are rejected instead of normalized.</summary>
    [Theory]
    [InlineData(13, 12)]
    [InlineData(2, 1)]
    [InlineData(255, 12)]
    public void InvalidExistingGroupFails(byte team, int maximumGroups)
    {
        Assert.False(SurvivalTeams.TryAssign([new(0, team)], maximumGroups, out var assigned));
        Assert.Empty(assigned);
        Assert.Null(SurvivalTeams.NextAvailableGroup([new(0, team)], 0, maximumGroups));
    }

    /// <summary>Three existing members cannot be retained in a native duo group.</summary>
    [Fact]
    public void ExistingOversizedGroupFails()
    {
        SurvivalTeamMember[] members = [new(0, 3), new(1, 3), new(2, 3)];
        Assert.False(SurvivalTeams.TryAssign(members, 12, out var assigned));
        Assert.Empty(assigned);
        Assert.Null(SurvivalTeams.NextAvailableGroup(members, 0, 12));
    }

    /// <summary>Identity and affinity contract errors fail loudly before a caller can publish assignments.</summary>
    [Fact]
    public void InvalidIdentityOrAffinityThrows()
    {
        SurvivalTeamMember[][] invalid =
        [
            [new(-1, 0)],
            [new(1, 0), new(1, 0)],
            [new(1, 0, 1)],
            [new(1, 0, 9)],
            [new(1, 0, 2), new(2, 0)],
            [new(1, 0, 2), new(2, 0, 3), new(3, 0, 2)]
        ];
        foreach (var members in invalid)
        {
            Assert.Throws<ArgumentException>(() => SurvivalTeams.TryAssign(members, 12, out _));
            Assert.Throws<ArgumentException>(() => SurvivalTeams.NextAvailableGroup(members, 1, 12));
        }
    }

    /// <summary>Invalid deployment limits are programming or configuration errors for both operations.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void InvalidConfiguredLimitThrows(int maximumGroups)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SurvivalTeams.TryAssign([], maximumGroups, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurvivalTeams.NextAvailableGroup([], 0, maximumGroups));
    }

    /// <summary>An empty room has no assignments and callers cannot select a nonexistent seat.</summary>
    [Fact]
    public void EmptyRosterHasNoAssignmentOrMovingSeat()
    {
        Assert.True(SurvivalTeams.TryAssign([], 12, out var assigned));
        Assert.Empty(assigned);
        Assert.Throws<ArgumentException>(() => SurvivalTeams.NextAvailableGroup([], 0, 12));
    }

    /// <summary>The team button skips full groups and may join an existing solo.</summary>
    [Fact]
    public void ManualChangeSkipsFullGroupAndJoinsSolo()
    {
        SurvivalTeamMember[] members = [new(0, 1), new(1, 2), new(2, 2), new(3, 3)];
        var before = members.ToArray();
        Assert.Equal((byte)3, SurvivalTeams.NextAvailableGroup(members, 0, 12));
        Assert.Equal(before, members);
    }

    /// <summary>The team button wraps at the configured limit instead of using an unavailable group.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public void ManualChangeWrapsWithinConfiguredGroups(byte maximumGroups)
    {
        Assert.Equal((byte)1, SurvivalTeams.NextAvailableGroup([new(0, maximumGroups)], 0,
            maximumGroups));
    }

    /// <summary>A full set of other groups leaves an assigned member in its current group.</summary>
    [Fact]
    public void ManualChangeRetainsCurrentGroupWhenOthersAreFull()
    {
        SurvivalTeamMember[] members = [new(0, 1), new(1, 1), new(2, 2), new(3, 2)];
        Assert.Equal((byte)1, SurvivalTeams.NextAvailableGroup(members, 0, 2));
    }

    /// <summary>A manual move changes only the requesting seat even when a verified party affinity is present.</summary>
    [Fact]
    public void ManualChangeDoesNotMovePartyPartner()
    {
        SurvivalTeamMember[] members = [new(0, 1, 1), new(1, 1, 0)];
        Assert.Equal((byte)2, SurvivalTeams.NextAvailableGroup(members, 0, 12));
        Assert.Equal((byte)1, members[1].Team);
    }

    /// <summary>An unassigned member receives the first nonfull group for an explicit manual selection.</summary>
    [Fact]
    public void UnassignedManualChangeMayJoinSolo()
    {
        Assert.Equal((byte)1, SurvivalTeams.NextAvailableGroup([new(0, 0), new(1, 1)], 0, 12));
    }

    /// <summary>A roster exceeding native capacity cannot return a partial assignment or a team move.</summary>
    [Fact]
    public void ExcessRosterCannotBeAllocatedOrMoved()
    {
        SurvivalTeamMember[] members = [new(0, 0), new(1, 1), new(2, 1)];
        Assert.False(SurvivalTeams.TryAssign(members, 1, out var assigned));
        Assert.Empty(assigned);
        Assert.Null(SurvivalTeams.NextAvailableGroup(members, 0, 1));
    }
}
