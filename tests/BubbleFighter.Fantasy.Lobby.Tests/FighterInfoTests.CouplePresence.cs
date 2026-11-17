using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    /// <summary>Offline is presence, not breakup: the stored ring and date survive and reconnect updates both fighters.</summary>
    [Fact]
    public async Task PartnerDisconnectPreservesTheRelationshipAndReconnectRestoresPresence()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        var original = c.Bob.Feature<ICoupleProfile>().Current;
        Assert.NotNull(original);
        Assert.Equal(Ring + 20, original.DisplayRingItemId);
        Assert.True(original.PartnerOnline);
        c.Online.Remove(c.Ann.Connection.Id);
        c.Flow.ConnectionClosed(c.Ann.Connection.Id);
        await c.Flow.SweepPresenceAsync(default);
        Assert.False(c.Ann.TryFeature<ICoupleProfile>(out _));
        Assert.Equal(original with { PartnerOnline = false }, c.Bob.Feature<ICoupleProfile>().Current);
        Assert.Equal(new CouplePartnerPublished(false, c.AnnNum, "Ann"), c.Partner.To(c.Bob)[^1]);

        var reconnect = new FeatureSession(3);
        c.Online.Attach(reconnect, new PlayerIdentity(checked((int)c.AnnNum), "ann", "Ann", 5)).MoveTo(SessionPhase.MainMenu);
        await c.Flow.RestoreAsync(reconnect, c.AnnNum, default);
        Assert.Equal(original, c.Bob.Feature<ICoupleProfile>().Current);
        Assert.Equal(original, reconnect.Feature<ICoupleProfile>().Current);
        Assert.Equal(new CouplePartnerPublished(true, c.AnnNum, "Ann"), c.Partner.To(c.Bob)[^1]);

        // A late close from the replaced connection cannot mark the new login offline.
        c.Flow.ConnectionClosed(c.Ann.Connection.Id);
        await c.Flow.SweepPresenceAsync(default);
        Assert.True(c.Bob.Feature<ICoupleProfile>().Current!.PartnerOnline);
        c.Flow.Dispose();
        Assert.False(c.Bob.TryFeature<ICoupleProfile>(out _));
        Assert.False(reconnect.TryFeature<ICoupleProfile>(out _));
    }

    /// <summary>Breakup clears the stable session feature, so subsequent shop or paint updates cannot resurrect it.</summary>
    [Fact]
    public async Task BreakupClearsTheOwnProfileAndItsLaterRefreshSource()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        var feature = c.Ann.Feature<ICoupleProfile>();
        Assert.NotNull(feature.Current);
        c.Clock.Advance(TimeSpan.FromHours(24));
        await c.Flow.BreakUpAsync(c.Ann, new("Bob"), default);
        Assert.Same(feature, c.Ann.Feature<ICoupleProfile>());
        Assert.Null(feature.Current);
        Assert.Null(c.Bob.Feature<ICoupleProfile>().Current);
        Assert.Null(c.Profile.To(c.Ann)[^1].Profile);
        Assert.Null(c.Profile.To(c.Bob)[^1].Profile);
    }
}
