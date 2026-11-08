using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Party.Tests;

public sealed partial class PartyPluginTests
{
    /// <summary>A native recruitment document admits directly without manufacturing an invitation token.</summary>
    [Fact]
    public async Task RecruitmentDocumentJoinsOnceAndFollowsItsLeader()
    {
        await using var rig = await Rig.StartAsync();
        using var status = rig.A.RegisterFeature<IPlazaRecruitmentStatus>(new Recruiting());
        await rig.SendAsync(rig.B, new PartyRecruitmentJoinRequested("Al"));
        Assert.Equal(new[] { 1, 2 }, rig.Parties.PartyOf(2)!.Members.Select(member => member.UserNum));
        Assert.Equal(1, Assert.Single(rig.SceneFollows).Request.LeaderUserNum);
        Assert.DoesNotContain(rig.Sent, row => row.Message is PartyInviteNotified);
        await rig.SendAsync(rig.B, new PartyRecruitmentJoinRequested("Al"));
        Assert.Equal(2, rig.Parties.PartyOf(2)!.Members.Count);
        Assert.Single(rig.SceneFollows);
    }

    /// <summary>A stale document cannot admit after cancellation, or admit beyond its accepted capacity.</summary>
    [Fact]
    public async Task RecruitmentDocumentRejectsCanceledAndFullLeaders()
    {
        await using var rig = await Rig.StartAsync();
        var recruitment = new Recruiting { IsRecruiting = false, MemberLimit = 2 };
        using var status = rig.A.RegisterFeature<IPlazaRecruitmentStatus>(recruitment);
        await rig.SendAsync(rig.B, new PartyRecruitmentJoinRequested("Al"));
        Assert.Equal(new PartyVisitFailed(PartyVisitFailure.Unavailable, "Al"), rig.Sent.Last().Message);
        Assert.Null(rig.Parties.PartyOf(2));
        recruitment.IsRecruiting = true;
        await rig.SendAsync(rig.B, new PartyRecruitmentJoinRequested("Al"));
        var third = new Session(3, "Cy", 3);
        rig.Online.Players[3] = third;
        await rig.SendAsync(third, new PartyRecruitmentJoinRequested("Al"));
        Assert.Equal(new PartyVisitFailed(PartyVisitFailure.PartyFull, "Al"), rig.Sent.Last().Message);
        Assert.Null(rig.Parties.PartyOf(3));
    }

    /// <summary>A right-click request on a live recruiter bypasses the host prompt using the native visit-grant exchange.</summary>
    [Fact]
    public async Task RecruitingLeaderAutomaticallyGrantsRightClickVisit()
    {
        await using var rig = await Rig.StartAsync();
        using var status = rig.A.RegisterFeature<IPlazaRecruitmentStatus>(new Recruiting());
        await rig.SendAsync(rig.B, new PartyVisitRequested("Al"));
        Assert.DoesNotContain(rig.Sent, row => row.Message is PartyVisitAsked);
        Assert.Equal(new PartyVisitGranted("Al"), rig.Sent.Last().Message);
        await rig.AcceptAsync();
        Assert.Equal(1, rig.Parties.PartyOf(2)!.Leader.UserNum);
    }

    /// <summary>A previously issued invitation cannot exceed a recruitment capacity filled while the prompt was open.</summary>
    [Fact]
    public async Task DelayedInvitationRechecksRecruitmentCapacity()
    {
        await using var rig = await Rig.StartAsync();
        using var status = rig.A.RegisterFeature<IPlazaRecruitmentStatus>(new Recruiting { MemberLimit = 2 });
        await rig.InviteAsync();
        var third = new Session(3, "Cy", 3);
        rig.Online.Players[3] = third;
        await rig.SendAsync(third, new PartyRecruitmentJoinRequested("Al"));
        await rig.AcceptAsync();
        Assert.Null(rig.Parties.PartyOf(2));
        Assert.Equal(new[] { 1, 3 }, rig.Parties.PartyOf(1)!.Members.Select(member => member.UserNum));
        Assert.Contains(rig.Sent, row => row.Message is PartyInviteReplied { Result: PartyInviteResult.PartyFull });
    }

    private sealed class Recruiting : IPlazaRecruitmentStatus
    {
        public bool IsRecruiting { get; set; } = true;
        public int MemberLimit { get; set; } = 8;
    }
}
