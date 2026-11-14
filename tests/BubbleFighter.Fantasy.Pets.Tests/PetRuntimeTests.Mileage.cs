using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Data;
using BubbleFighter.Fantasy.Pets.Mileage;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

public sealed partial class PetRuntimeTests
{
    private const int MileageReward = 100901;

    /// <summary>Commits skill reset, mileage conversion and rewards before publishing inventory, equipment, evolution and success.</summary>
    [Fact]
    public async Task SelectedResetPublishesCommittedMileageAndRewardBeforeItsSuccessDialog()
    {
        var rig = await MileageRig(5);
        var before = await rig.Entitlements.PrepareAsync(1, default);
        var pet = before.CharacterBag.Single(row => row.ItemId == PetTestData.PetId);
        var target = pet.PetIdentity!.ArticleKey.ToString(CultureInfo.InvariantCulture);

        await rig.Runtime.UseAsync(rig.Session, new(PetTestData.SelectFood, 1, target) { SecondaryParameter = "11" }, default);

        Assert.Equal(new[] { "inventory", "equipment", "evolve", "use" }, rig.Order);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(1, (await rig.StateAsync())!.Grade);
        Assert.Equal(2, await rig.QuantityAsync(PetTestData.SelectFood));
        Assert.Equal(7, await rig.QuantityAsync(PetMileagePolicy.BalanceItemId));
        Assert.Equal(2, await rig.QuantityAsync(MileageReward));
        Assert.Equal(new[] { MileageReward }, Assert.Single(rig.Evolutions.Messages).Rewards);
        var publication = Assert.Single(rig.Inventories.Messages);
        Assert.True(publication.IsCompleteSnapshot);
        Assert.Equal(pet.PetIdentity, publication.CharacterBag.Single(row => row.ItemId == PetTestData.PetId).PetIdentity);
        Assert.Equal(7, publication.CharacterBag.Single(row => row.ItemId == PetMileagePolicy.BalanceItemId).Quantity);
        Assert.Equal(2, publication.CharacterBag.Single(row => row.ItemId == MileageReward).Quantity);
    }

    /// <summary>Refuses conversion above the operation budget without consuming inventory or publishing reward and inventory messages.</summary>
    [Fact]
    public async Task ConversionBudgetRefusalConsumesNothingAndSendsNoRewardOrInventory()
    {
        var options = MileageOptions();
        options.Mileage.Conversion!.MaximumPerOperation = 1;
        var rig = await MileageRig(9, options);
        var complete = await rig.Entitlements.PrepareAsync(1, default);
        var pet = complete.CharacterBag.Single(row => row.ItemId == PetTestData.PetId);
        var before = await rig.SnapshotAsync();

        await rig.Runtime.UseAsync(rig.Session, new(PetTestData.SelectFood, 1,
            pet.PetIdentity!.ArticleKey.ToString(CultureInfo.InvariantCulture)) { SecondaryParameter = "11" }, default);

        Assert.NotEqual(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(new[] { "use" }, rig.Order);
        Assert.Equal(before, await rig.SnapshotAsync());
        Assert.Empty(rig.Evolutions.Messages);
        Assert.Empty(rig.Inventories.Messages);
    }

    /// <summary>Preserves the owned reset item and progression when skill resets are disabled for the build.</summary>
    [Fact]
    public async Task DisabledSkillResetDoesNotConsumeItsOwnedItem()
    {
        var options = PetTestData.Options(manual: true);
        options.SkillResetsEnabled = false;
        var rig = await Rig.CreateAsync(options, PetTestData.State(1, 0), useEntitlements: true);
        await rig.Entitlements.PrepareAsync(1, default);
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, new(PetTestData.ResetFood, 1,
            PetTestData.PetId.ToString(CultureInfo.InvariantCulture)), default);
        Assert.NotEqual(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(before, await rig.SnapshotAsync());
    }

    /// <summary>Rejects mileage activation when gains, limits, reward lifetime or remainder policy are incomplete or contradictory.</summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("missing_gain")]
    [InlineData("missing_maximum")]
    [InlineData("wrong_lifetime")]
    [InlineData("pet_reward")]
    [InlineData("implicit_remainder")]
    [InlineData("display_mismatch")]
    public void MileageActivationRejectsIncompleteOrContradictoryPolicy(string failure)
    {
        var options = MileageOptions();
        switch (failure)
        {
            case "disabled": options.Mileage.Enabled = false; break;
            case "missing_gain": options.Mileage.Grants = [options.Mileage.Grants[0]]; break;
            case "missing_maximum": options.Mileage.DisplayMaximum = null; break;
            case "wrong_lifetime": options.Mileage.Conversion!.Rewards[0].Kind = InventoryGrantKind.Days; break;
            case "pet_reward": options.Mileage.Conversion!.Rewards[0].ItemId = PetTestData.PetId; break;
            case "implicit_remainder": options.Mileage.Conversion!.Remainder = null; break;
            case "display_mismatch":
                options.Mileage.Conversion!.RequireDisplayMaximumMatch = true;
                options.Mileage.DisplayMaximum = 11;
                break;
        }
        var catalog = MileageCatalog();
        Assert.Throws<ConfigException>(() => PetMileagePolicy.Create(options, catalog,
            new(options.BuildKey, catalog.Items.Keys.ToArray())));
    }

    /// <summary>Copies mileage gains and conversion rewards so later changes to configuration cannot change an existing policy.</summary>
    [Fact]
    public void PolicyCopiesConfiguredGainsAndRewardsBeforeTheyCanChange()
    {
        var options = MileageOptions();
        var catalog = MileageCatalog();
        var policy = PetMileagePolicy.Create(options, catalog, new(options.BuildKey, catalog.Items.Keys.ToArray()))!;
        options.Mileage.Grants[1].Quantity = 1;
        options.Mileage.Conversion!.Rewards[0].Amount = 99;
        var plan = policy.Plan(1, PetTestData.SelectFood, [], PetTestData.Now);
        Assert.Equal(12, plan.Gain);
        Assert.Equal(2, Assert.Single(plan.Conversion!.Rewards).Quantity);
        Assert.Equal(2, plan.Conversion.BalanceAfterConversion);
    }

    private static async Task<Rig> MileageRig(int balance, PetsOptions? options = null)
    {
        var rig = await Rig.CreateAsync(options ?? MileageOptions(), PetTestData.State(2, 37),
            useEntitlements: true, suppliedCatalog: MileageCatalog());
        await rig.Store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
            [new(1, InventoryBag.CharacterBag, 9990, PetMileagePolicy.BalanceItemId, balance, 0)
                { Lifetime = InventoryLifetime.Permanent }], default);
        return rig;
    }

    private static PetsOptions MileageOptions()
    {
        var options = PetTestData.Options(manual: true);
        options.Mileage = new()
        {
            Enabled = true, DisplayMaximum = 10,
            Grants = [new() { ConsumableItemId = PetTestData.ResetFood, Quantity = 7 },
                new() { ConsumableItemId = PetTestData.SelectFood, Quantity = 12 }],
            Conversion = new()
            {
                Threshold = 10, MaximumPerOperation = 5, Mode = PetMileageConversionMode.All,
                Remainder = PetMileageRemainder.Preserve,
                Rewards = [new() { ItemId = MileageReward, Kind = InventoryGrantKind.Count, Amount = 2 }],
            },
        };
        return options;
    }

    private static PetCatalog MileageCatalog()
    {
        var basis = PetTestData.Catalog();
        var items = basis.Items.ToDictionary();
        items[PetMileagePolicy.BalanceItemId] = new(PetMileagePolicy.BalanceItemId, 1114, 0, 0)
            { DurationType = "count", MaximumDuration = 1000000000 };
        items[MileageReward] = new(MileageReward, 0, 0, 0) { DurationType = "count", MaximumDuration = 100 };
        return new(items, basis.Skills.ToDictionary(), basis.Buffs.ToDictionary(), basis.PetCategory);
    }
}
