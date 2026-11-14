using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class PlazaFlowTests
{
    /// <summary>A skill-only local update refreshes peers and federation without losing entry or movement history.</summary>
    [Fact]
    public async Task LocalAttributeRefreshKeepsEntryAndPositionAndExplicitEmptyClears()
    {
        await using var rig = new Rig(new InGameOptions { GameProtocol = "test/game" });
        var owner = Rig.Player(11, "owner");
        var observer = Rig.Player(22, "observer");
        var link = new Link();
        using var binding = rig.Flow.BindPlazaFederation(link);
        await EnterAsync(rig, owner);
        await EnterAsync(rig, observer);
        var original = rig.PlazaEntered.To(owner).Single().You;
        var view = new LocalEquipmentViewUpdated(11, 300001, [300001, 203100], 0, 0, 0)
        {
            ItemAttributes = [new(203100, 7, [1, 2, 3, 4], 0, "")],
        };
        await rig.Flow.OnEquipmentAttributesPublishedAsync(new("test/game", owner, view, true), default);
        await rig.Flow.HandlePlazaPositionAsync(owner, new(Upload), default);
        var position = rig.Position.To(observer).Last();
        rig.MemberEntered.Sent.Clear();
        rig.Position.Sent.Clear();
        link.Changes.Clear();
        uint[] words = [1, 2, 3, 5];
        var changed = view with { ItemAttributes = [new(203100, 7, words, 0, "")] };
        await rig.Flow.OnEquipmentAttributesPublishedAsync(new("other/game", owner, changed, true), default);
        Assert.Empty(rig.MemberEntered.Sent);
        await rig.Flow.OnEquipmentAttributesPublishedAsync(new("test/game", owner, changed, true), default);
        words[3] = 99;
        var refreshed = Assert.Single(rig.MemberEntered.To(observer)).Member;
        Assert.Equal(original.EntrySeed, refreshed.EntrySeed);
        Assert.Equal(new uint[] { 1, 2, 3, 5 }, Assert.Single(refreshed.ItemAttributes).Values);
        Assert.Equal(position, Assert.Single(rig.Position.To(observer)));
        Assert.Empty(rig.MemberEntered.To(owner));
        var federated = Assert.Single(link.Changes);
        Assert.Equal(PlazaFederationEventKind.Entered, federated.Kind);
        Assert.Equal(new uint[] { 1, 2, 3, 5 }, Assert.Single(federated.Member!.ItemAttributes).Values);

        rig.MemberEntered.Sent.Clear();
        await rig.Flow.OnEquipmentAttributesPublishedAsync(new("test/game", owner, view with { ItemAttributes = [] }, true), default);
        var cleared = Assert.Single(rig.MemberEntered.To(observer)).Member;
        Assert.Empty(cleared.ItemAttributes);
        Assert.Equal(original.EntrySeed, cleared.EntrySeed);
        Assert.Equal(view.EquippedItemIds, cleared.EquippedItemIds);
        Assert.Empty(link.Changes[^1].Member!.ItemAttributes);
    }

    /// <summary>Remote skill changes refresh an existing seat and preserve its recruitment and saved movement.</summary>
    [Fact]
    public async Task RemoteAttributeRefreshKeepsEntryPositionAndRecruitment()
    {
        await using var rig = new Rig();
        var local = Rig.Player(11, "local");
        await EnterAsync(rig, local);
        var remote = new PlazaMember(0, 77, "remote", 9, 300014, [300014, 203100], 55)
        {
            ItemAttributes = [new(203100, 7, [1, 2, 3, 4], 0, "")],
            Recruitment = new(true, true, "Recruiting"),
        };
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77, remote, default)]), default);
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Moved, 77, null, Upload)]), default);
        var position = rig.Position.To(local).Last();
        rig.MemberEntered.Sent.Clear();
        rig.Position.Sent.Clear();
        var changed = remote with { ItemAttributes = [new(203100, 7, [1, 2, 3, 5], 0, "")] };
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77, changed, default)]), default);
        var refreshed = Assert.Single(rig.MemberEntered.To(local)).Member;
        Assert.Equal((55, remote.Recruitment), (refreshed.EntrySeed, refreshed.Recruitment));
        Assert.Equal(new uint[] { 1, 2, 3, 5 }, Assert.Single(refreshed.ItemAttributes).Values);
        Assert.Equal(position, Assert.Single(rig.Position.To(local)));
        rig.MemberEntered.Sent.Clear();
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77,
            remote with { ItemAttributes = [] }, default)]), default);
        Assert.Empty(Assert.Single(rig.MemberEntered.To(local)).Member.ItemAttributes);
        Assert.Empty(rig.MemberLeft.Sent);
    }
}
