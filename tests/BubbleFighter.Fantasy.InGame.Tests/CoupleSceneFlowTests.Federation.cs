using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class CoupleSceneFlowTests
{
    [Fact]
    public async Task RemoteRelationshipChangesAreNarrowAndSkipObserversWhoEnteredARoom()
    {
        await using var rig = new Rig();
        var local = Player(11, "local");
        var leaving = Player(22, "leaving");
        await rig.Flow.HandlePlazaEnterAsync(local, new(), default);
        await rig.Flow.HandlePlazaEnterAsync(leaving, new(), default);
        var remote = new PlazaMember(0, 77, "remote", 1, 300001, [300001], 5)
        { Couple = new(88, "partner") };
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77, remote, default)]), default);
        Assert.Equal(remote.Couple, rig.MemberEntered.To(local).Last().Member.Couple);
        await rig.CreateAsync(leaving);
        rig.MemberEntered.Clear();
        var changed = remote with { Couple = null };
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77, changed, default)]), default);

        Assert.Null(Assert.Single(rig.PlazaCouple.To(local)).Member.Couple);
        Assert.Empty(rig.PlazaCouple.To(leaving));
        Assert.Empty(rig.MemberEntered.All());
        var later = Player(33, "later");
        await rig.Flow.HandlePlazaEnterAsync(later, new(), default);
        Assert.Null(rig.PlazaEntered.To(later).Single().Members.Single(member => member.UserNumber == 77).Couple);

        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Left, 77, null, default)]), default);
        rig.PlazaCouple.Clear();
        await rig.Flow.ApplyPlazaFederationAsync(new(false,
            [new(PlazaFederationEventKind.Entered, 77, remote with { EntrySeed = 6 }, default)]), default);
        Assert.Empty(rig.PlazaCouple.All());
        Assert.Equal(remote.Couple, rig.MemberEntered.To(local).Last().Member.Couple);
    }

    [Fact]
    public async Task RemoteSnapshotCannotChangeTheRelationshipOfALocalReplacement()
    {
        await using var rig = new Rig();
        var observer = Player(11, "observer");
        await rig.Flow.HandlePlazaEnterAsync(observer, new(), default);
        var remote = new PlazaMember(0, 77, "remote", 1, 300001, [300001], 5) { Couple = new(88, "remote partner") };
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77, remote, default)]), default);
        var local = Player(77, "local", new(11, "observer"));
        await rig.Flow.HandlePlazaEnterAsync(local, new(), default);
        rig.MemberEntered.Clear();
        await rig.Flow.ApplyPlazaFederationAsync(new(false,
            [new(PlazaFederationEventKind.Entered, 77, remote with { Couple = null }, default)]), default);
        Assert.Empty(rig.PlazaCouple.All());
        Assert.Empty(rig.MemberEntered.All());
        var later = Player(33, "later");
        await rig.Flow.HandlePlazaEnterAsync(later, new(), default);
        Assert.Equal(new CoupleSceneState(11, "observer"),
            rig.PlazaEntered.To(later).Single().Members.Single(member => member.UserNumber == 77).Couple);
    }
}
