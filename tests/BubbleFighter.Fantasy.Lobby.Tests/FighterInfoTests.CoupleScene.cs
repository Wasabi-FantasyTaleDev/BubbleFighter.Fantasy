using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    /// <summary>Inspecting a fighter includes their offline partner rather than borrowing the requester's couple state.</summary>
    [Fact]
    public async Task InspectionRetainsTheTargetsOfflinePartnerUntilBreakup()
    {
        var c = await CouplesAsync();
        using var flow = c.Flow;
        await flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        c.Online.Remove(c.Bob.Connection.Id);
        flow.ConnectionClosed(c.Bob.Connection.Id);
        await flow.SweepPresenceAsync(default);
        var observer = new FeatureSession(30);
        c.Online.Attach(observer, new PlayerIdentity(999, "observer", "Observer", 3)).MoveTo(SessionPhase.MainMenu);
        var replies = new Replies();
        await FighterInfoHandler.HandleAsync(c.Online, new Inventory(), replies, observer, new((int)c.AnnNum), default);
        var profile = Assert.Single(replies.Sent).Reply.CouplePartner;
        Assert.NotNull(profile);
        Assert.Equal(new CoupleSceneState(c.BobNum, "Bob"), profile.Partner);
        Assert.False(profile.Relationship.PartnerOnline);
        c.Clock.Advance(TimeSpan.FromHours(24));
        await flow.BreakUpAsync(c.Ann, new("Bob"), default);
        await FighterInfoHandler.HandleAsync(c.Online, new Inventory(), replies, observer, new((int)c.AnnNum), default);
        Assert.Null(replies.Sent[^1].Reply.CouplePartner);
        Assert.NotNull(profile);
    }

    /// <summary>Scene observers see committed identities after feature installation, without presence-only repetitions.</summary>
    [Fact]
    public async Task CoupleSceneChangesTrackRelationshipIdentityIndependentlyOfPresence()
    {
        var events = new EventBus(NullLogger.Instance);
        var changes = new List<CoupleSceneChanged>();
        using var subscription = events.On<CoupleSceneChanged>(change =>
        {
            Assert.Equal(change.Couple, change.Session.Feature<ICoupleProfile>().Scene);
            changes.Add(change);
        });
        var c = await CouplesAsync(events: events);
        using var flow = c.Flow;
        await flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.Equal(2, changes.Count);
        Assert.Equal(new CoupleSceneState(c.BobNum, "Bob"), c.Ann.Feature<ICoupleProfile>().Scene);
        Assert.Equal(new CoupleSceneState(c.AnnNum, "Ann"), c.Bob.Feature<ICoupleProfile>().Scene);

        await flow.RestoreAsync(c.Ann, c.AnnNum, default);
        Assert.Equal(2, changes.Count);
        c.Online.Remove(c.Bob.Connection.Id);
        flow.ConnectionClosed(c.Bob.Connection.Id);
        await flow.SweepPresenceAsync(default);
        Assert.Equal(2, changes.Count);
        Assert.False(c.Ann.Feature<ICoupleProfile>().Current!.PartnerOnline);
        Assert.Equal(new CoupleSceneState(c.BobNum, "Bob"), c.Ann.Feature<ICoupleProfile>().Scene);

        c.Clock.Advance(TimeSpan.FromHours(24));
        await flow.BreakUpAsync(c.Ann, new("Bob"), default);
        Assert.Equal(3, changes.Count);
        Assert.Null(changes[^1].Couple);
        Assert.Null(c.Ann.Feature<ICoupleProfile>().Scene);
    }

    /// <summary>A failed own-profile send cannot leave scene state pointing at the previous relationship.</summary>
    [Fact]
    public async Task CoupleScenePublicationPrecedesTheFallibleOwnProfileSend()
    {
        var events = new EventBus(NullLogger.Instance);
        var changes = new List<CoupleSceneChanged>();
        using var subscription = events.On<CoupleSceneChanged>(changes.Add);
        var c = await CouplesAsync(events: events);
        using var flow = c.Flow;
        await flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        c.Profile.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default).AsTask());
        Assert.Equal(2, changes.Count);
        Assert.Equal(new CoupleSceneState(c.AnnNum, "Ann"), changes.Single(change => ReferenceEquals(change.Session, c.Bob)).Couple);
        Assert.Equal(new CoupleSceneState(c.BobNum, "Bob"), changes.Single(change => ReferenceEquals(change.Session, c.Ann)).Couple);
        Assert.True(Assert.Single(c.Answered.To(c.Ann)).Accepted);

        await flow.RestoreAsync(c.Bob, c.BobNum, default);
        Assert.Equal(2, changes.Count);
        Assert.Equal(new CoupleSceneState(c.BobNum, "Bob"), c.Ann.Feature<ICoupleProfile>().Scene);
    }
}
