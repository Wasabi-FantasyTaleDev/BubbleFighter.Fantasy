using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

public sealed partial class PetRuntimeTests
{
    /// <summary>Keeps one durable article identity through feeding, growth, replacement and selective deletion with committed publications.</summary>
    [Fact]
    public async Task ArticleFeedingLevelUpReplacementAndDeletionShareOneDurableIdentity()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(0, 5), useEntitlements: true);
        var initialized = await rig.Entitlements.PrepareAsync(1, default);
        var identity = initialized.CharacterBag.Single(row => row.ItemId == PetTestData.PetId).PetIdentity!;
        var target = identity.ArticleKey.ToString(CultureInfo.InvariantCulture);
        await rig.Runtime.UseAsync(rig.Session, new(PetTestData.NormalFood, 1, target), default);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(10, (await rig.StateAsync())!.Experience);
        Assert.Equal(0, (await rig.StateAsync())!.Grade);

        rig.ClearMessages();
        await rig.Runtime.LevelUpAsync(rig.Session, new(identity.ArticleKey), rig.Levels, default);
        Assert.Equal(0, Assert.Single(rig.Levels.Messages).Result);
        Assert.Equal(1, (await rig.StateAsync())!.Grade);
        Assert.Equal(11, (await rig.StateAsync())!.FirstSkill!.Selector);

        rig.ClearMessages();
        await rig.Runtime.ChangeAsync(rig.Session, new(identity.ArticleKey, 11, 12, PetTestData.ChangeFood), rig.Changes, default);
        Assert.Equal(0, Assert.Single(rig.Changes.Messages).Result);
        Assert.Equal(12, (await rig.StateAsync())!.FirstSkill!.Selector);
        Assert.Equal(2, await rig.QuantityAsync(PetTestData.ChangeFood));
        Assert.Equal(new[] { "inventory", "equipment", "change" }, rig.Order);

        rig.ClearMessages();
        await rig.Runtime.UseAsync(rig.Session, new(PetTestData.SelectFood, 1, target) { SecondaryParameter = "12" }, default);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(0, (await rig.StateAsync())!.Grade);
        Assert.Null((await rig.StateAsync())!.FirstSkill);
        Assert.Equal(0, Assert.Single(rig.Evolutions.Messages).Outcome);
        var publication = Assert.Single(rig.Inventories.Messages);
        Assert.True(publication.IsCompleteSnapshot);
        Assert.All(publication.CharacterBag.Concat(publication.EquippedBag).Where(row => row.ItemId == PetTestData.PetId),
            row => Assert.Equal(identity, row.PetIdentity));
        Assert.All(rig.Equipment.Selections, ids => Assert.Contains(PetTestData.PetId, ids));
    }

    /// <summary>Initializes a build's fullness once while preserving another build's progression and the shared article identity.</summary>
    [Fact]
    public async Task InitialFullnessIsSavedOnceAndAnotherBuildGetsItsOwnState()
    {
        var rig = new Rig(PetTestData.Options(manual: true), useEntitlements: true);
        await rig.SeedAsync(1, [PetTestData.Slot(PetTestData.State(2, 37), "kr-812")]);
        var first = await rig.Entitlements.PrepareAsync(1, default);
        var row = Assert.Single(first.CharacterBag);
        var modern = row.Pet!.Find("kr-1256")!;
        Assert.Equal(0, modern.Grade);
        Assert.Equal(PetTestData.Now, modern.FullnessExpiresAt);
        Assert.Equal(37, row.Pet.Find("kr-812")!.Experience);
        rig.Clock.Now = rig.Clock.Now.AddHours(2);
        var next = Assert.Single((await rig.Entitlements.PrepareAsync(1, default)).CharacterBag);
        Assert.Equal(row.PetIdentity, next.PetIdentity);
        Assert.Equal(modern, next.Pet!.Find("kr-1256"));
        Assert.Equal(row.Pet.Find("kr-812"), next.Pet.Find("kr-812"));
    }

    /// <summary>Rejects unaddressable article keys during resolution and replacement without changing owned inventory.</summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(99999999UL)]
    [InlineData(9223372036854775808UL)]
    [InlineData(18446744073709551615UL)]
    public async Task UnaddressableArticleReferencesCannotAliasAnOwnedPet(ulong key)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(1, 5), useEntitlements: true);
        await rig.Entitlements.PrepareAsync(1, default);
        var before = await rig.SnapshotAsync();
        Assert.Null(await rig.Entitlements.ResolveAsync(1, key, default));
        await rig.Runtime.ChangeAsync(rig.Session, new(key, 11, 12, PetTestData.ChangeFood), rig.Changes, default);
        Assert.NotEqual(0, Assert.Single(rig.Changes.Messages).Result);
        Assert.Equal(before, await rig.SnapshotAsync());
    }
}
