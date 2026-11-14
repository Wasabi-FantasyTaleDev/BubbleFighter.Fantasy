using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Pets.Data;
using BubbleFighter.Fantasy.Pets.Mileage;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Exercises pet operations through committed memory inventory and ordered typed publications.</summary>
public sealed partial class PetRuntimeTests
{
    /// <summary>Commits legacy automatic growth and food consumption before inventory, equipment, evolution and use-result publications.</summary>
    [Fact]
    public async Task LegacyAutomaticGrowthPublishesInventoryEquipmentEvolutionThenUse()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5));
        Assert.True(await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default));
        Assert.Equal(new[] { "inventory", "equipment", "evolve", "use" }, rig.Order);
        var publication = Assert.Single(rig.Inventories.Messages);
        foreach (var bag in new[] { publication.CharacterBag, publication.EquippedBag })
        {
            var state = bag.Single(row => row.ItemId == PetTestData.PetId).Pet!.Find(rig.Options.BuildKey)!;
            Assert.Equal(1, state.Grade);
            Assert.Equal(0, state.Experience);
            Assert.Equal(PetTestData.FirstSkill, state.FirstSkill);
            Assert.Equal(PetTestData.Now, state.LastFedAt);
            Assert.Equal(2, bag.Single(row => row.ItemId == PetTestData.NormalFood).Quantity);
        }
        var evolution = Assert.Single(rig.Evolutions.Messages);
        Assert.Equal(1, evolution.Outcome);
        Assert.Equal((ulong)PetTestData.PetId, evolution.Target);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Contains(PetTestData.PetId, Assert.Single(rig.Equipment.Selections));
        Assert.Equal(1, (await rig.StateAsync())!.Grade);
    }

    /// <summary>Lets ordinary modern food fill experience, then grows only on the dedicated request without consuming more food.</summary>
    [Fact]
    public async Task ModernNormalFoodWaitsForTheDedicatedLevelUpRequest()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(0, 5));
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        var fed = (await rig.StateAsync())!;
        Assert.Equal((0, 10), (fed.Grade, fed.Experience));
        Assert.Equal(new[] { "inventory", "equipment", "use" }, rig.Order);
        Assert.Empty(rig.Evolutions.Messages);
        var count = await rig.QuantityAsync(PetTestData.NormalFood);
        rig.ClearMessages();

        await rig.Runtime.LevelUpAsync(rig.Session, new(PetTestData.PetId), rig.Levels, default);
        Assert.Equal(new[] { "inventory", "equipment", "level" }, rig.Order);
        Assert.Equal(new PetLevelUpReplied(PetTestData.PetId, 0), Assert.Single(rig.Levels.Messages));
        Assert.Equal((1, 0), ((await rig.StateAsync())!.Grade, (await rig.StateAsync())!.Experience));
        Assert.Equal(count, await rig.QuantityAsync(PetTestData.NormalFood));
        Assert.Empty(rig.Uses.Messages);
        Assert.Empty(rig.Evolutions.Messages);
    }

    /// <summary>Consumes one premium food to grow exactly one stage despite an active ordinary-food cooldown.</summary>
    [Theory]
    [InlineData(0, 100644)]
    [InlineData(1, 100644)]
    [InlineData(0, 102923)]
    [InlineData(1, 102923)]
    public async Task ModernPremiumFoodGrowsOneStageAndBypassesCooldown(int grade, int food)
    {
        var options = PetTestData.Options(manual: true);
        options.FeedCooldownSeconds = 3600;
        var initial = PetTestData.State(grade, 1) with { LastFedAt = PetTestData.Now };
        var rig = await Rig.CreateAsync(options, initial);
        await rig.Runtime.UseAsync(rig.Session, Use(food), default);
        var grown = (await rig.StateAsync())!;
        Assert.Equal(grade + 1, grown.Grade);
        Assert.Equal(0, grown.Experience);
        Assert.NotNull(grown.FirstSkill);
        if (grade == 1) Assert.NotNull(grown.SecondSkill);
        Assert.Equal(2, await rig.QuantityAsync(food));
        Assert.Equal(new[] { "inventory", "equipment", "evolve", "use" }, rig.Order);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Empty(rig.Levels.Messages);
    }

    /// <summary>Refuses invalid food quantities and malformed or unavailable targets without changing inventory.</summary>
    [Theory]
    [InlineData(0, "301001")]
    [InlineData(-1, "301001")]
    [InlineData(2, "301001")]
    [InlineData(1, "")]
    [InlineData(1, "0")]
    [InlineData(1, "-1")]
    [InlineData(1, " 301001")]
    [InlineData(1, "301001 ")]
    [InlineData(1, "9001")]
    [InlineData(1, "301002")]
    [InlineData(1, "100000000")]
    [InlineData(1, "18446744073709551615")]
    public async Task InvalidQuantityOrTargetNeverConsumesFood(int quantity, string target)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5));
        var before = await rig.SnapshotAsync();
        Assert.True(await rig.Runtime.UseAsync(rig.Session, new(PetTestData.NormalFood, quantity, target), default));
        await AssertRefusedAsync(rig, before);
    }

    /// <summary>Requires the caller to own the target pet and leaves another account's inventory untouched.</summary>
    [Fact]
    public async Task AnotherAccountsPetCannotAuthorizeTheCallersFood()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5));
        var rows = await rig.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        await rig.SeedAsync(2, rows.Select(row => row with { UserNum = 2 }).ToArray());
        await rig.SeedAsync(1, rows.Where(row => row.ItemId != PetTestData.PetId).ToArray());
        var caller = await rig.SnapshotAsync();
        var other = await rig.SnapshotAsync(2);
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        await AssertRefusedAsync(rig, caller);
        Assert.Equal(other, await rig.SnapshotAsync(2));
    }

    /// <summary>Refuses feeding an unequipped legacy pet without consuming food or changing progression.</summary>
    [Fact]
    public async Task UnequippedLegacyPetRejectsFoodWithoutMutation()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5), worn: false);
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        await AssertRefusedAsync(rig, before);
    }

    /// <summary>Allows an unequipped ordinary modern pet to gain experience from one consumed food item.</summary>
    [Fact]
    public async Task ModernOrdinaryPetCanBeFedWithoutBeingEquipped()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(0, 0), worn: false);
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(5, (await rig.StateAsync())!.Experience);
        Assert.Equal(2, await rig.QuantityAsync(PetTestData.NormalFood));
    }

    /// <summary>Preserves inventory when pet ownership or food is empty, expired, ambiguous, missing or unusable.</summary>
    [Theory]
    [InlineData("pet-empty")]
    [InlineData("pet-expired")]
    [InlineData("pet-ambiguous")]
    [InlineData("food-empty")]
    [InlineData("food-expired")]
    [InlineData("food-life")]
    [InlineData("food-missing")]
    public async Task UnavailableOwnershipOrFoodNeverCommitsThePlannedGrowth(string fault)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5));
        var rows = (await rig.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Select(row =>
        {
            if (row.ItemId == PetTestData.PetId)
                return fault switch
                {
                    "pet-empty" => row with { Quantity = 0 },
                    "pet-expired" => row with { Lifetime = InventoryLifetime.Until(PetTestData.Now) },
                    _ => row,
                };
            if (row.ItemId == PetTestData.NormalFood)
                return fault switch
                {
                    "food-empty" => row with { Quantity = 0 },
                    "food-expired" => row with { Lifetime = InventoryLifetime.Until(PetTestData.Now) },
                    "food-life" => row with { Lifetime = InventoryLifetime.Life },
                    _ => row,
                };
            return row;
        }).Where(row => fault != "food-missing" || row.ItemId != PetTestData.NormalFood).ToList();
        if (fault == "pet-ambiguous") rows.Add(rows.Single(row => row.ItemId == PetTestData.PetId) with { SlotKey = 9999 });
        await rig.SeedAsync(1, rows.ToArray());
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        await AssertRefusedAsync(rig, before);
    }

    /// <summary>Publishes the build-specific worn selection while leaving the shared inventory worn flags unchanged.</summary>
    [Fact]
    public async Task BuildSelectedPetUsesProjectedFlagsWithoutChangingSharedFlags()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5), worn: false);
        await rig.Store.UpdateLoadoutSelectionAsync(1, rig.Scope, [new(301000, 301999, PetTestData.PetId)],
            PetTestData.PetId, true, default);
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        var publication = Assert.Single(rig.Inventories.Messages);
        foreach (var bag in new[] { publication.CharacterBag, publication.EquippedBag })
            Assert.Equal(1, bag.Single(row => row.ItemId == PetTestData.PetId).Flags & InventorySlotRecord.WornFlagBit);
        foreach (var row in (await rig.SnapshotAsync()).Where(row => row.ItemId == PetTestData.PetId))
            Assert.Equal(0, row.Flags & InventorySlotRecord.WornFlagBit);
    }

    /// <summary>Refuses feeding outside the supported session phases before consuming food or changing progression.</summary>
    [Theory]
    [InlineData(SessionPhase.Authenticated)]
    [InlineData(SessionPhase.Match)]
    [InlineData(SessionPhase.Migrating)]
    public async Task UnavailableSessionPhaseCannotConsumeFood(SessionPhase phase)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5));
        var session = new TestSession(rig.Options.BuildKey, 1, phase);
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(session, Use(PetTestData.NormalFood), default);
        await AssertRefusedAsync(rig, before);
    }

    /// <summary>Rejects a session without player authentication before inventory mutation or any reply publication.</summary>
    [Fact]
    public async Task UnauthenticatedPetUseFailsBeforeConsumptionOrReply()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5));
        var before = await rig.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Runtime.UseAsync(new TestSession(rig.Options.BuildKey, 0), Use(PetTestData.NormalFood), default));
        Assert.Equal(before, await rig.SnapshotAsync());
        Assert.Empty(rig.Order);
    }

    /// <summary>Refuses ordinary food until the exact cooldown deadline, then consumes it and records the new feeding time.</summary>
    [Fact]
    public async Task NormalFoodCooldownRejectsUntilTheExactDeadline()
    {
        var options = PetTestData.Options(manual: true);
        options.FeedCooldownSeconds = 3600;
        var rig = await Rig.CreateAsync(options, PetTestData.State(0, 0) with { LastFedAt = PetTestData.Now });
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        Assert.Equal(16, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(before, await rig.SnapshotAsync());
        rig.ClearMessages();
        rig.Clock.Now = PetTestData.Now.AddHours(1).AddTicks(-1);
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        Assert.Equal(16, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(before, await rig.SnapshotAsync());
        rig.ClearMessages();
        rig.Clock.Now = PetTestData.Now.AddHours(1);
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.NormalFood), default);
        Assert.Equal(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(2, await rig.QuantityAsync(PetTestData.NormalFood));
        Assert.Equal(rig.Clock.Now, (await rig.StateAsync())!.LastFedAt);
    }

    /// <summary>Consumes one reset per removed growth stage, preserves the remaining skill and refuses resets at the initial stage.</summary>
    [Fact]
    public async Task ResetRemovesOneStagePerUseAndStopsAtTheInitialStage()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(2, 31));
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.ResetFood), default);
        var first = (await rig.StateAsync())!;
        Assert.Equal((1, 0), (first.Grade, first.Experience));
        Assert.Equal(PetTestData.FirstSkill, first.FirstSkill);
        Assert.Null(first.SecondSkill);
        Assert.Equal(0, Assert.Single(rig.Evolutions.Messages).Outcome);
        Assert.Equal(new[] { "inventory", "equipment", "evolve", "use" }, rig.Order);
        rig.ClearMessages();
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.ResetFood), default);
        var second = (await rig.StateAsync())!;
        Assert.Equal((0, 0), (second.Grade, second.Experience));
        Assert.Null(second.FirstSkill);
        Assert.Null(second.SecondSkill);
        Assert.Equal(1, await rig.QuantityAsync(PetTestData.ResetFood));
        rig.ClearMessages();
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.ResetFood), default);
        await AssertRefusedAsync(rig, before);
    }

    /// <summary>Removes the requested earned selector, moves the retained skill to the first stage and consumes one selection item.</summary>
    [Theory]
    [InlineData(11, 21)]
    [InlineData(21, 11)]
    public async Task SelectiveDeletionUsesTheSelectorAndRetainsTheOtherSkill(int removed, int retained)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(2, 31));
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.SelectFood) with
        { SecondaryParameter = removed.ToString(CultureInfo.InvariantCulture) }, default);
        var state = (await rig.StateAsync())!;
        Assert.Equal((1, 0), (state.Grade, state.Experience));
        Assert.Equal(retained, state.FirstSkill!.Selector);
        Assert.Null(state.SecondSkill);
        Assert.Equal(2, await rig.QuantityAsync(PetTestData.SelectFood));
        Assert.Equal(new[] { "inventory", "equipment", "evolve", "use" }, rig.Order);
    }

    /// <summary>Refuses selective deletion without a valid owned selector and preserves all inventory rows.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-11")]
    [InlineData(" 11")]
    public async Task SelectiveDeletionRejectsMissingInvalidOrUnownedSelectors(string? selector)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(2, 31));
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, Use(PetTestData.SelectFood) with { SecondaryParameter = selector }, default);
        await AssertRefusedAsync(rig, before);
    }

    /// <summary>Refuses an unexpected secondary argument on food and reset operations that accept only one target.</summary>
    [Theory]
    [InlineData(100643)]
    [InlineData(100644)]
    [InlineData(100652)]
    public async Task SingleTargetOperationsRejectAnUnexpectedSecondArgument(int item)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(1, 3));
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, Use(item) with { SecondaryParameter = "11" }, default);
        await AssertRefusedAsync(rig, before);
    }

    /// <summary>Updates only the active build's saved progression while both builds consume the same owned food quantity.</summary>
    [Fact]
    public async Task EachBuildMutatesOnlyItsOwnSavedProgression()
    {
        var legacy = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 1));
        var modernState = PetTestData.State(1, 17) with { LastFedAt = PetTestData.Now.AddDays(-2) };
        var rows = await legacy.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        await legacy.SeedAsync(1, rows.Select(row => row.ItemId == PetTestData.PetId
            ? row with { Pet = row.Pet!.With("kr-1256", modernState) } : row).ToArray());
        await legacy.Runtime.UseAsync(legacy.Session, Use(PetTestData.NormalFood), default);
        var afterLegacy = (await legacy.StateAsync())!;
        Assert.Equal(6, afterLegacy.Experience);
        Assert.Equal(modernState, await legacy.StateAsync("kr-1256"));
        var modern = new Rig(PetTestData.Options(manual: true), legacy.Store);
        await modern.Runtime.UseAsync(modern.Session, Use(PetTestData.NormalFood), default);
        Assert.Equal(afterLegacy, await modern.StateAsync("kr-812"));
        Assert.Equal(22, (await modern.StateAsync())!.Experience);
        Assert.Equal(1, await modern.QuantityAsync(PetTestData.NormalFood));
    }

    /// <summary>Refuses replacement targets without supported article resolution and leaves the selection item and pet state unchanged.</summary>
    [Theory]
    [InlineData(301001UL)]
    [InlineData(100000001UL)]
    public async Task UnsupportedAbilityReplacementNeverSucceedsOrConsumesTheSelectionItem(ulong target)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(1, 3));
        var before = await rig.SnapshotAsync();
        await rig.Runtime.ChangeAsync(rig.Session, new(target, 11, 12, PetTestData.ChangeFood), rig.Changes, default);
        Assert.NotEqual(0, Assert.Single(rig.Changes.Messages).Result);
        Assert.Equal(new[] { "change" }, rig.Order);
        Assert.Equal(before, await rig.SnapshotAsync());
    }

    /// <summary>Rejects article-key targets when the runtime exposes only ordinary pet identity, preserving both food and progression.</summary>
    [Fact]
    public async Task UnsupportedArticleTargetCannotFeedOrLevelUpAnOrdinaryPet()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(0, 10));
        var before = await rig.SnapshotAsync();
        await rig.Runtime.UseAsync(rig.Session, new(PetTestData.NormalFood, 1, "100000001"), default);
        await rig.Runtime.LevelUpAsync(rig.Session, new(100000001), rig.Levels, default);
        Assert.NotEqual(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.NotEqual(0, Assert.Single(rig.Levels.Messages).Result);
        Assert.Equal(new[] { "use", "level" }, rig.Order);
        Assert.Equal(before, await rig.SnapshotAsync());
    }

    /// <summary>Refuses dedicated growth below the experience threshold or at maximum grade without changing inventory.</summary>
    [Theory]
    [InlineData(0, 9)]
    [InlineData(2, 0)]
    public async Task DedicatedLevelUpRefusesIncompleteExperienceOrMaximumGrade(int grade, int experience)
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(manual: true), PetTestData.State(grade, experience));
        var before = await rig.SnapshotAsync();
        await rig.Runtime.LevelUpAsync(rig.Session, new(PetTestData.PetId), rig.Levels, default);
        Assert.NotEqual(0, Assert.Single(rig.Levels.Messages).Result);
        Assert.Equal(new[] { "level" }, rig.Order);
        Assert.Equal(before, await rig.SnapshotAsync());
    }

    /// <summary>Declines non-pet consumables without messages or mutation so the next inventory handler can process them.</summary>
    [Fact]
    public async Task NonPetConsumablesContinueToTheNextInventoryHandler()
    {
        var rig = await Rig.CreateAsync(PetTestData.Options(), PetTestData.State(0, 5));
        var before = await rig.SnapshotAsync();
        Assert.False(await rig.Runtime.UseAsync(rig.Session, Use(200001), default));
        Assert.Empty(rig.Order);
        Assert.Equal(before, await rig.SnapshotAsync());
    }

    private static InventoryItemUseRequested Use(int item) => new(item, 1, "301001");

    private static async Task AssertRefusedAsync(Rig rig, InventorySlotRecord[] before)
    {
        Assert.NotEqual(0, Assert.Single(rig.Uses.Messages).Result);
        Assert.Equal(new[] { "use" }, rig.Order);
        Assert.Equal(before, await rig.SnapshotAsync());
    }

    private sealed class Rig
    {
        internal Rig(PetsOptions options, MemoryInventoryRepository? store = null, bool useEntitlements = false,
            PetCatalog? suppliedCatalog = null)
        {
            Options = options;
            Store = store ?? new MemoryInventoryRepository(new MemoryPlayerTable(4));
            var catalog = suppliedCatalog ?? PetTestData.Catalog();
            Scope = new(options.BuildKey, catalog.Items.Keys.Order().ToArray());
            Session = new(options.BuildKey, 1);
            Uses = new(options.Protocol, "use", Order);
            Inventories = new(options.Protocol, "inventory", Order);
            Evolutions = new(options.Protocol, "evolve", Order);
            Levels = new(options.Protocol, "level", Order);
            Changes = new(options.Protocol, "change", Order);
            Equipment = new(Order);
            var rules = new PetRules(catalog, options, new PetTestData.MinimumRandom());
            Entitlements = new(catalog, rules, Store, Store, Scope, Clock);
            Runtime = new(catalog, options, rules,
                Store, Store, Scope, Uses, Inventories, Evolutions, Equipment, Clock, useEntitlements ? Entitlements : null,
                PetMileagePolicy.Create(options, catalog, Scope));
        }

        internal PetsOptions Options { get; }
        internal MemoryInventoryRepository Store { get; }
        internal InventoryLoadoutScope Scope { get; }
        internal TestSession Session { get; }
        internal PetRuntime Runtime { get; }
        internal PetEntitlements Entitlements { get; }
        internal PetTestData.Clock Clock { get; } = new();
        internal List<string> Order { get; } = [];
        internal Sender<InventoryItemUseReplied> Uses { get; }
        internal Sender<InventoryReplaced> Inventories { get; }
        internal Sender<PetEvolved> Evolutions { get; }
        internal Sender<PetLevelUpReplied> Levels { get; }
        internal Sender<PetAbilityChangeReplied> Changes { get; }
        internal EquippedPush Equipment { get; }

        internal static async Task<Rig> CreateAsync(PetsOptions options, PetProgression state, bool worn = true,
            bool useEntitlements = false, PetCatalog? suppliedCatalog = null)
        {
            var rig = new Rig(options, useEntitlements: useEntitlements, suppliedCatalog: suppliedCatalog);
            var rows = new List<InventorySlotRecord>
            {
                PetTestData.Slot(state, options.BuildKey) with { Flags = worn ? (byte)1 : (byte)0 },
                new(1, InventoryBag.CharacterBag, 9002, 300001, 1, 1) { Lifetime = InventoryLifetime.Permanent },
            };
            var itemIds = new[] { PetTestData.NormalFood, PetTestData.PremiumFood, PetTestData.OtherPremiumFood,
                PetTestData.ResetFood, PetTestData.SelectFood, PetTestData.ChangeFood, 200001 };
            rows.AddRange(itemIds.Select((item, index) => new InventorySlotRecord(1, InventoryBag.CharacterBag,
                9010 + index, item, 3, 0) { Lifetime = InventoryLifetime.Permanent }));
            await rig.SeedAsync(1, rows.ToArray());
            return rig;
        }

        internal async Task SeedAsync(long user, InventorySlotRecord[] rows)
        {
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                await Store.ReplaceSlotsAsync(user, bag, rows.Select(row => row with { Bag = bag }).ToArray(), default);
        }

        internal async Task<InventorySlotRecord[]> SnapshotAsync(long user = 1) =>
            [.. await Store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default),
             .. await Store.ListSlotsAsync(user, InventoryBag.EquippedBag, 512, default)];

        internal async Task<PetProgression?> StateAsync(string? build = null) =>
            (await Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default))
                .Single(row => row.ItemId == PetTestData.PetId).Pet?.Find(build ?? Options.BuildKey);

        internal async Task<int> QuantityAsync(int item) =>
            (await Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).SingleOrDefault(row => row.ItemId == item)?.Quantity ?? 0;

        internal void ClearMessages()
        {
            Order.Clear(); Uses.Messages.Clear(); Inventories.Messages.Clear(); Evolutions.Messages.Clear();
            Levels.Messages.Clear(); Changes.Messages.Clear(); Equipment.Selections.Clear();
        }
    }

    private sealed class Sender<T>(string protocol, string label, List<string> order) : IMessageSender<T> where T : IMessage
    {
        /// <summary>Associates the recorded typed reply with the runtime fixture's configured game protocol.</summary>
        public string Protocol => protocol;
        internal List<T> Messages { get; } = [];
        /// <summary>Checks cancellation, retains the typed message and records its position in the publication sequence.</summary>
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Messages.Add(message); order.Add(label); return ValueTask.CompletedTask; }
    }

    private sealed class EquippedPush(List<string> order) : IEquippedViewPush
    {
        internal List<int[]> Selections { get; } = [];
        /// <summary>Checks cancellation, copies the equipment selection and records its place in the publication sequence.</summary>
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Selections.Add(equippedIds.ToArray()); order.Add("equipment"); return ValueTask.CompletedTask; }
    }

    private sealed class TestSession : ISessionWithFeatures
    {
        internal TestSession(string build, int user, SessionPhase phase = SessionPhase.MainMenu)
        {
            Profile = new(build, build[..2], int.Parse(build[3..], CultureInfo.InvariantCulture), 0, 0, build);
            var features = new TestSessionFeatures();
            Features = features;
            if (user == 0) return;
            var player = new PlayerSession(new(user, "player", "player", 1));
            features.Register<IPlayerSession>(player);
            if (phase == SessionPhase.Authenticated) return;
            player.MoveTo(SessionPhase.MainMenu);
            if (phase == SessionPhase.Match) player.MoveTo(SessionPhase.Room);
            player.MoveTo(phase);
        }
        /// <summary>Identifies the runtime fixture's sole session independently of its configured player account.</summary>
        public long Id => 1;
        /// <summary>Supplies the inert connection used by the typed pet runtime fixture.</summary>
        public IConnection Connection { get; } = new Connection();
        /// <summary>Retains the build profile created for the runtime operation under test.</summary>
        public ClientProfile Profile { get; }
        /// <summary>Exposes the configured player identity and phase, or an empty feature set for unauthenticated calls.</summary>
        public ISessionFeatures Features { get; }
        /// <summary>Provides session-local storage without opening a game transport.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Fails raw packet sends so pet operations must publish through the typed fixture senders.</summary>
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            throw new InvalidOperationException("The pet runtime must publish typed messages.");
    }

    private sealed class Connection : IConnection
    {
        /// <summary>Identifies the inert connection attached to the runtime fixture session.</summary>
        public long Id => 1;
        /// <summary>Supplies a loopback endpoint without opening a network socket.</summary>
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects input-pipe access because tests invoke the pet runtime directly.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects output-pipe access because tests record typed runtime publications.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Provides connection-local storage for the runtime fixture.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Keeps the inert connection open for runtime calls.</summary>
        public bool IsClosed => false;
        /// <summary>Accepts close requests without changing the inert connection's always-open state.</summary>
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}

internal static class PetTestData
{
    internal const int PetId = 301001;
    internal const int NormalFood = 100643, PremiumFood = 100644, OtherPremiumFood = 102923;
    internal const int ResetFood = 100652, SelectFood = 103384, ChangeFood = 103383;
    internal static readonly DateTimeOffset Now = new(2026, 11, 9, 23, 30, 0, TimeSpan.Zero);
    internal static readonly PetSkill FirstSkill = new(11, 4, 0, -2, 0);
    internal static readonly PetSkill SecondSkill = new(21, 8, 0, 0, 0);

    internal static PetsOptions Options(bool manual = false, string? build = null) => new()
    {
        BuildKey = build ?? (manual ? "kr-1256" : "kr-812"),
        Protocol = (build ?? (manual ? "kr-1256" : "kr-812")) + "/game",
        ManualLevelUp = manual, GradeCapFromPetParameter = manual, SharedSkillPool = manual,
        ClientConfigurationSource = manual ? PetClientConfigurationSource.DynamicConfig : PetClientConfigurationSource.None,
        SkillResetsEnabled = true,
        FeedCooldownSeconds = manual ? 0 : null,
        ClientUtcOffsetMinutes = 0,
        ExperienceFromFoodParameter = manual, InitialFullness = 1,
        NormalFoodFullness = 5, PremiumFoodFullness = 10,
    };

    internal static PetProgression State(int grade, int experience) => new(grade, experience, Now.AddMinutes(-86), null,
        grade >= 1 ? FirstSkill : null, grade >= 2 ? SecondSkill : null);

    internal static InventorySlotRecord Slot(PetProgression state, string build = "kr-1256") =>
        new(1, InventoryBag.CharacterBag, 9001, PetId, 1, 1)
        { Lifetime = InventoryLifetime.Permanent, Pet = PetProgressions.Empty.With(build, state) };

    internal static PetCatalog Catalog() => new(new()
    {
        [PetId] = new(PetId, PetCatalog.PetFunction, 2, 504),
        [300001] = new(300001, 0, 0, 0), [200001] = new(200001, 0, 0, 0),
        [NormalFood] = new(NormalFood, 19, 5, 504), [PremiumFood] = new(PremiumFood, 20, 10, 504),
        [OtherPremiumFood] = new(OtherPremiumFood, 20, 10, 504),
        [ResetFood] = new(ResetFood, 21, 0, 504), [SelectFood] = new(SelectFood, 39, 0, 504),
        [ChangeFood] = new(ChangeFood, 38, 10, 504),
    }, new()
    {
        [7] = new(7, 504, 0, 100, 0, [0, 18, 16, 0]),
        [11] = new(11, 504, 1, 1, 0, [1, 0, 2, 0]),
        [12] = new(12, 504, 1, 1, 0, [3, 0, 0, 0]),
        [21] = new(21, 504, 2, 1, 0, [3, 0, 0, 0]),
    }, new()
    {
        [1] = new(1, "first", 1, 4, 4, 1, 4, 4),
        [2] = new(2, "second", 2, -2, -2, 1, -2, -2),
        [3] = new(3, "third", 3, 8, 8, 1, 8, 8),
        [16] = new(16, "gradeExp1", 6, 0, 10, 1, 0, 10),
        [17] = new(17, "gradeExp2", 7, 0, 100, 1, 0, 100),
        [18] = new(18, "hunger", 8, 86, 100, 1, 86, 100),
    }, 504);

    internal sealed class MinimumRandom : Random
    {
        /// <summary>Returns zero so weighted skill selection consistently uses the start of its probability interval.</summary>
        public override double NextDouble() => 0;
        /// <summary>Returns the inclusive lower bound so generated skill values are deterministic.</summary>
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }

    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = PetTestData.Now;
        /// <summary>Returns the clock value controlled by feeding and fullness boundary tests.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
