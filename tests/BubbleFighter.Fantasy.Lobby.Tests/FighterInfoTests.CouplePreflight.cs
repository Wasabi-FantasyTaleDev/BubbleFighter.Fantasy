using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    /// <summary>Opening and confirming a selector does not reserve a proposal or grant either player's consent.</summary>
    [Fact]
    public async Task CouplePreflightDoesNotCreateAnInvitationOrBypassRingOwnership()
    {
        var c = await CouplesAsync(annHasRing: false, ringChange: false);
        await c.Flow.InviterStatusAsync(c.Ann, new(), default);
        Assert.Equal(CoupleInviterStatus.Ready, Assert.Single(c.InviterStatus.To(c.Ann)).Status);
        await c.Flow.InitialInviteAsync(c.Ann, new(true, " Bob "), default);
        Assert.Equal("Bob", Assert.Single(c.Confirmation.To(c.Ann)).Target);
        Assert.Empty(c.Delivered.Messages);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.Empty(c.Answered.Messages);
        Assert.Empty(c.Profile.Messages);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, "hello"), default);
        Assert.Equal(CoupleProposalResult.NoRing, Assert.Single(c.Replied.To(c.Ann)).Result);
        Assert.Empty(c.Delivered.Messages);
    }

    /// <summary>Target selection refuses self, unknown and departed targets and never fabricates a confirmation.</summary>
    [Fact]
    public async Task CoupleTargetPreflightResolvesLiveCanonicalPlayers()
    {
        var c = await CouplesAsync();
        await c.Flow.InitialInviteAsync(c.Ann, new(true, "Ann"), default);
        await c.Flow.InitialInviteAsync(c.Ann, new(true, "Nobody"), default);
        c.Online.Remove(c.Bob.Connection.Id);
        await c.Flow.InitialInviteAsync(c.Ann, new(true, "Bob"), default);
        Assert.Equal(new[] { CoupleProposalResult.Self, CoupleProposalResult.NotFound, CoupleProposalResult.Offline },
            c.Replied.To(c.Ann).Select(reply => reply.Result));
        Assert.Empty(c.Confirmation.Messages);
        Assert.Empty(c.Delivered.Messages);
    }

    /// <summary>The preflight sees outgoing proposals and accepted relationships, including builds without ring changes.</summary>
    [Fact]
    public async Task CouplePreflightTracksPendingAndPairedStatesWithoutARingChangeSender()
    {
        var c = await CouplesAsync(ringChange: false);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, "hello"), default);
        await c.Flow.InviterStatusAsync(c.Ann, new(), default);
        Assert.Equal(CoupleInviterStatus.Busy, Assert.Single(c.InviterStatus.To(c.Ann)).Status);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.True(Assert.Single(c.Answered.To(c.Ann)).Accepted);
        await c.Flow.InviterStatusAsync(c.Ann, new(), default);
        Assert.Equal(CoupleInviterStatus.Paired, c.InviterStatus.To(c.Ann)[^1].Status);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => c.Flow.ChangeRingAsync(c.Ann, new(Ring), default).AsTask());
        Assert.Contains("no native ring-change exchange", error.Message, StringComparison.Ordinal);
        c.Clock.Advance(TimeSpan.FromDays(2));
        await c.Flow.BreakUpAsync(c.Ann, new("Bob"), default);
        Assert.Equal(CoupleBreakUpStatus.Done, Assert.Single(c.BrokeUp.To(c.Ann)).Status);
        Assert.Empty(c.RingSent.Messages);
    }

    /// <summary>A retired connection or a fighter in a round cannot open a new native invitation dialog.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CouplePreflightRequiresTheCurrentAvailableSession(bool retired)
    {
        var c = await CouplesAsync();
        if (retired)
        {
            c.Online.Remove(c.Ann.Connection.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Flow.InviterStatusAsync(c.Ann, new(), default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Flow.InitialInviteAsync(c.Ann, new(true, "Bob"), default).AsTask());
            Assert.Empty(c.InviterStatus.Messages);
            Assert.Empty(c.Confirmation.Messages);
            return;
        }
        if (c.Ann.TryFeature<IPlayerSession>(out var player))
        {
            player.MoveTo(SessionPhase.Room);
            player.MoveTo(SessionPhase.Match);
        }
        await c.Flow.InviterStatusAsync(c.Ann, new(), default);
        Assert.Equal(CoupleInviterStatus.Unavailable, Assert.Single(c.InviterStatus.To(c.Ann)).Status);
        await c.Flow.InitialInviteAsync(c.Ann, new(true, "Bob"), default);
        Assert.Equal(CoupleProposalResult.Unavailable, Assert.Single(c.Replied.To(c.Ann)).Result);
        Assert.Empty(c.Confirmation.Messages);
    }
}
