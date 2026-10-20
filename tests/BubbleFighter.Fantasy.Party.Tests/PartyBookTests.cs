using BubbleFighter.Fantasy.Contracts.Party;
using Xunit;

namespace BubbleFighter.Fantasy.Party.Tests;

/// <summary>Party membership rules and the notices each change sends, in the client's order.</summary>
public sealed class PartyBookTests
{
    private static readonly PartyMember Al = new(1, "Al");
    private static readonly PartyMember Bo = new(2, "Bo");
    private static readonly PartyMember Cy = new(3, "Cy");

    [Fact]
    public void AnAcceptedInvitationMakesTheInviterTheLeader()
    {
        var book = Book();
        Assert.Equal(PartyInviteResult.Sent, book.Invite(Al, Bo));
        Assert.True(book.TakeInvite(Al.UserNum, Bo.UserNum));
        Assert.False(book.TakeInvite(Al.UserNum, Bo.UserNum));
        var notices = new List<PartyNotice>();
        Assert.Equal(PartyInviteResult.Sent, book.Join(Al, Bo, notices));
        Assert.Equal([Al, Bo], book.PartyOf(Bo.UserNum)!.Members);
        Assert.Equal(Al, book.PartyOf(Bo.UserNum)!.Leader);
        // Both members get the list, then the join notice.
        Assert.Equal([Al.UserNum, Bo.UserNum, Al.UserNum, Bo.UserNum], notices.Select(n => n.UserNum));
        Assert.All(notices.Take(2), n => Assert.IsType<PartyUpdated>(n.Message));
        Assert.All(notices.Skip(2), n => Assert.Equal(new PartyMemberJoined("Bo"), n.Message));
    }

    [Fact]
    public void InvitationsAreRefusedForSelfSamePartyAndAFullParty()
    {
        var book = Book(maxMembers: 2);
        Assert.Equal(PartyInviteResult.Self, book.Invite(Al, Al));
        book.Invite(Al, Bo);
        book.TakeInvite(1, 2);
        book.Join(Al, Bo, []);
        Assert.Equal(PartyInviteResult.SameParty, book.Invite(Al, Bo));
        Assert.Equal(PartyInviteResult.PartyFull, book.Invite(Al, Cy));
        // A player already in another party cannot be invited.
        Assert.Equal(PartyInviteResult.Refused, book.Invite(Cy, Bo));
    }

    [Fact]
    public void AnInvitationExpires()
    {
        var clock = new ManualClock();
        var book = new PartyBook(8, 16, TimeSpan.FromSeconds(30), clock);
        book.Invite(Al, Bo);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(book.TakeInvite(1, 2));
    }

    [Fact]
    public void PendingInvitationsAreBounded()
    {
        var book = Book(maxPending: 1);
        Assert.Equal(PartyInviteResult.Sent, book.Invite(Al, Bo));
        Assert.Equal(PartyInviteResult.Refused, book.Invite(Al, Cy));
    }

    [Fact]
    public void TheLeaderLeavingHandsThePartyToTheNextMember()
    {
        var book = Party(Al, Bo, Cy);
        var notices = new List<PartyNotice>();
        Assert.True(book.Leave(Al, notices));
        Assert.Equal(new PartyLeft(), notices[0].Message);
        Assert.Equal([Al], Assert.IsType<PartyUpdated>(notices[1].Message).Party.Members);
        Assert.Contains(notices, n => n.UserNum == Bo.UserNum && n.Message.Equals(new PartyLeaderReplaced("Al", "Bo")));
        Assert.Equal([Bo, Cy], book.PartyOf(Cy.UserNum)!.Members);
        Assert.Null(book.PartyOf(Al.UserNum));
    }

    [Fact]
    public void APartyOfOneEnds()
    {
        var book = Party(Al, Bo);
        var notices = new List<PartyNotice>();
        book.Leave(Bo, notices);
        Assert.Contains(notices, n => n.UserNum == Al.UserNum && n.Message.Equals(new PartyMemberLeft("Bo")));
        Assert.Contains(notices, n => n.UserNum == Al.UserNum && n.Message is PartyUpdated { Party.Members.Count: 1 });
        Assert.Null(book.PartyOf(Al.UserNum));
        Assert.False(book.Leave(Bo, []));
    }

    [Fact]
    public void OnlyTheLeaderKicksAndNeverItself()
    {
        var book = Party(Al, Bo, Cy);
        Assert.Equal(PartyKickResult.NotAllowed, book.Kick(Bo, "Cy", []));
        Assert.Equal(PartyKickResult.Self, book.Kick(Al, "Al", []));
        Assert.Equal(PartyKickResult.AlreadyLeft, book.Kick(Al, "Zed", []));
        var notices = new List<PartyNotice>();
        Assert.Equal(PartyKickResult.Kicked, book.Kick(Al, "Cy", notices));
        Assert.Equal(new PartyKicked("Al"), notices[0].Message);
        Assert.Contains(notices, n => n.UserNum == Bo.UserNum && n.Message.Equals(new PartyMemberKicked("Al", "Cy")));
        Assert.DoesNotContain(notices, n => n.UserNum == Al.UserNum && n.Message is PartyMemberKicked);
        Assert.Equal([Al, Bo], book.PartyOf(Al.UserNum)!.Members);
    }

    [Fact]
    public void TheLeaderHandsOverLeadership()
    {
        var book = Party(Al, Bo, Cy);
        Assert.Equal(PartyLeaderChangeResult.NotLeader, book.ChangeLeader(Bo, "Cy", []));
        Assert.Equal(PartyLeaderChangeResult.NotMember, book.ChangeLeader(Al, "Zed", []));
        Assert.Equal(PartyLeaderChangeResult.AlreadyLeader, book.ChangeLeader(Al, "Al", []));
        var notices = new List<PartyNotice>();
        Assert.Equal(PartyLeaderChangeResult.Changed, book.ChangeLeader(Al, "Cy", notices));
        Assert.Equal([Cy, Al, Bo], book.PartyOf(Bo.UserNum)!.Members);
        Assert.Equal(3, notices.Count(n => n.Message is PartyLeaderChanged));
        Assert.Equal(3, notices.Count(n => n.Message is PartyUpdated));
    }

    [Fact]
    public void ATokenNamesOnePendingInvitationForItsConnectionOnly()
    {
        var book = Book();
        Assert.Equal(PartyInviteResult.Sent, book.Invite(Al, Bo, 20, out var token));
        Assert.NotEqual(0u, token);
        Assert.False(book.TakeInviteByToken(token, Bo.UserNum, 21, out _));       // another connection
        Assert.False(book.TakeInviteByToken(token, Cy.UserNum, 20, out _));       // another player
        Assert.False(book.TakeInviteByToken(token + 1, Bo.UserNum, 20, out _));   // another word
        Assert.True(book.TakeInviteByToken(token, Bo.UserNum, 20, out var inviter));
        Assert.Equal(Al.UserNum, inviter);
        Assert.False(book.TakeInviteByToken(token, Bo.UserNum, 20, out _));       // taken once
    }

    [Fact]
    public void AnInvitationBoundToAConnectionIsNotTakenFromAnother()
    {
        var book = Book();
        book.Invite(Al, Bo, 20);
        Assert.False(book.TakeInvite(Al.UserNum, Bo.UserNum, 21));
        Assert.False(book.TakeInvite(Al.UserNum, Bo.UserNum));
        Assert.True(book.TakeInvite(Al.UserNum, Bo.UserNum, 20));
    }

    [Fact]
    public void ReinvitingReplacesTheTokenAndAnExpiredOneIsRefused()
    {
        var clock = new ManualClock();
        var book = new PartyBook(8, 16, TimeSpan.FromSeconds(30), clock);
        book.Invite(Al, Bo, 20, out var first);
        book.Invite(Al, Bo, 20, out var second);
        Assert.NotEqual(first, second);
        Assert.False(book.TakeInviteByToken(first, Bo.UserNum, 20, out _));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(book.TakeInviteByToken(second, Bo.UserNum, 20, out _));
    }

    private static PartyBook Book(int maxMembers = 8, int maxPending = 16) =>
        new(maxMembers, maxPending, TimeSpan.FromSeconds(30), TimeProvider.System);

    private static PartyBook Party(PartyMember leader, params PartyMember[] members)
    {
        var book = Book();
        foreach (var member in members)
        {
            Assert.Equal(PartyInviteResult.Sent, book.Invite(leader, member));
            Assert.True(book.TakeInvite(leader.UserNum, member.UserNum));
            Assert.Equal(PartyInviteResult.Sent, book.Join(leader, member, []));
        }
        return book;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
