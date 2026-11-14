using System.Collections.Immutable;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins immutable seed modifiers and native static-then-instance combat aggregation.</summary>
public sealed class PetCombatSeedTests
{
    /// <summary>Caller mutations cannot alter retained issuance identity, even through nested value vectors.</summary>
    [Fact]
    public void EquipmentOwnsDeepImmutableAttributesAndStructuralEquality()
    {
        int[] ids = [190100, 300001, 190100];
        uint[] words = [1, 2, 3, 4];
        RoomItemAttribute[] attributes = [new(190100, 101, words, 2, "pet")];
        var equipment = new MatchSeedEquipment(1, ids, itemAttributes: attributes);
        var same = new MatchSeedEquipment(1, [300001, 190100], itemAttributes: [new(190100, 101, [1, 2, 3, 4], 2, "pet")]);
        var hash = equipment.GetHashCode();
        ids[0] = 99;
        words[0] = 99;
        attributes[0] = attributes[0] with { Unknown28 = 99 };
        Assert.Equal(same, equipment);
        Assert.Equal(same.GetHashCode(), hash);
        Assert.Equal(hash, equipment.GetHashCode());
        Assert.Equal([190100, 300001], equipment.WornItemIds);
        Assert.Equal(1u, equipment.ItemAttributes[0].Values[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)equipment.ItemAttributes[0].Values)[0] = 99);
        Assert.Throws<NotSupportedException>(() => ((IList<RoomItemAttribute>)equipment.ItemAttributes)[0] = attributes[0]);
    }

    /// <summary>Every retained native field and modifier order participates in seed request equality.</summary>
    [Fact]
    public void NativeModifierChangesAreDistinctSeedInputs()
    {
        var attribute = new RoomItemAttribute(190100, 101, [1, 2, 3, 4], 2, "pet");
        var original = new MatchSeedEquipment(1, [190100], itemAttributes: [attribute]);
        RoomItemAttribute[] changes =
        [
            attribute with { Kind = 102 }, attribute with { Values = [1, 2, 3, 5] },
            attribute with { Unknown28 = 1 }, attribute with { Unknown32 = "Pet" },
        ];
        foreach (var changed in changes)
            Assert.NotEqual(original, new MatchSeedEquipment(1, [190100], itemAttributes: [changed]));
        Assert.NotEqual(new MatchSeedEquipment(1, [190100], itemAttributes: [attribute, changes[0]]),
            new MatchSeedEquipment(1, [190100], itemAttributes: [changes[0], attribute]));
        Assert.Throws<InvalidDataException>(() => new MatchSeedEquipment(1, [1], itemAttributes: [attribute]));
    }

    /// <summary>Supplied pet assignments follow every static item and retain their vector order.</summary>
    [Fact]
    public void OrdinaryCriticalUsesAllStaticRowsThenTheSuppliedVector()
    {
        var catalog = new CombatSeedCatalog(new Dictionary<int, PetCombatEffect[]>
        {
            [300001] = [new(PetCombatEffectKind.AssignCriticalChancePercent, 9)],
            [100001] = [new(PetCombatEffectKind.AssignCriticalChancePercent, 8)],
            [190100] = [new(PetCombatEffectKind.AssignCriticalChancePercent, 4)],
            [301001] = [new(PetCombatEffectKind.AssignCriticalChancePercent, 7)],
            [400001] = [new(PetCombatEffectKind.AssignCriticalChancePercent, 6)],
        }, Pets());
        var attributes = new[]
        {
            Attribute(301001, PetCombatEffectKind.AssignCriticalChancePercent, 1),
            Attribute(301001, PetCombatEffectKind.AssignCriticalChancePercent, 2),
        };
        Assert.Equal(0.02, catalog.Contributions(new(1, [400001, 301001, 300001, 190100, 100001], itemAttributes: attributes)).Critical);
        Assert.Equal(0.01, catalog.Contributions(new(1, [400001, 301001, 300001, 190100, 100001],
            itemAttributes: attributes.Reverse().ToArray())).Critical);
        Assert.Equal(0.02, catalog.Contributions(new(1, [301001, 300001, 190100, 100001], itemAttributes: attributes)).Critical);
        Assert.Equal(0.07, catalog.Contributions(new(1, [301001, 300001, 190100, 100001])).Critical);
    }

    /// <summary>Instance-only items contribute, and cancellation adds ratios without a second percent conversion.</summary>
    [Fact]
    public void InstanceOnlyCriticalAndCancellationKeepDifferentUnits()
    {
        var equipment = Equipment(
            Attribute(190100, PetCombatEffectKind.AssignCriticalChancePercent, 0.5f),
            Attribute(190100, PetCombatEffectKind.AddCriticalCancellationRatio, 0.03f),
            Attribute(190100, PetCombatEffectKind.AddCriticalCancellationRatio, 0.06f));
        var actual = new CombatSeedCatalog(new Dictionary<int, PetCombatEffect[]>(), Pets()).Contributions(equipment);
        Assert.Equal(0.005, actual.Critical);
        Assert.Equal(0.03f + 0.06f, actual.Cancellation);
    }

    /// <summary>Static rows precede the supplied vector, whose additions each retain native float rounding.</summary>
    [Fact]
    public void CancellationAppliesAllStaticRowsBeforeTheSuppliedFloatAdditions()
    {
        var catalog = new CombatSeedCatalog(new Dictionary<int, PetCombatEffect[]>
        {
            [190100] = [new(PetCombatEffectKind.AddCriticalCancellationRatio, 16777216f)],
            [400001] = [new(PetCombatEffectKind.AddCriticalCancellationRatio, -16777216f)],
        }, Pets());
        var equipment = new MatchSeedEquipment(1, [190100, 301001, 400001], itemAttributes:
            [Attribute(301001, PetCombatEffectKind.AddCriticalCancellationRatio, 1)]);
        Assert.Equal(1f, catalog.Contributions(equipment).Cancellation);
        var rounded = new MatchSeedEquipment(1, [190100, 301001, 400001], itemAttributes:
        [
            Attribute(301001, PetCombatEffectKind.AddCriticalCancellationRatio, 16777216f),
            Attribute(301001, PetCombatEffectKind.AddCriticalCancellationRatio, 1),
            Attribute(301001, PetCombatEffectKind.AddCriticalCancellationRatio, -16777216f),
        ]);
        Assert.Equal(0f, catalog.Contributions(rounded).Cancellation);
    }

    /// <summary>Pet defence ratios retain each build's audited endpoint precision.</summary>
    [Theory]
    [InlineData("kr-812", 2)]
    [InlineData("kr-1256", 3)]
    public void PetDefenceUsesTheExistingBuildEndpointPrecision(string build, int low)
    {
        var store = new WeaponCriticalCatalogTests.FixtureStore(new() { ["Config/ShopItem2.xml"] = DefenceSeedTests.ItemWorkbook() });
        var catalog = DefenceSeedCatalog.Load(store, TestBuilds.Packets, build, "Config/ShopItem2.xml", 1 << 20,
            "defence_workbook_max_bytes", Pets());
        var equipment = Equipment(Attribute(190100, PetCombatEffectKind.AddDefenceMinimumRatio, 0.03f),
            Attribute(190100, PetCombatEffectKind.AddDefenceMaximumRatio, 0.07f));
        Assert.Equal(new DefencePercentRange(low, 7), catalog.Range(equipment));
        Assert.Equal(catalog.Table(1, 2, 1_000_000, equipment), catalog.Table(1, 2, 1_000_000, equipment));
    }

    /// <summary>Pet additions follow every static item so native float rounding preserves their final defence ratio.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void PetDefenceAppliesSuppliedAttributesAfterAllStaticEquipment(string build)
    {
        var workbook = DefenceSeedTests.Workbook(
            ("300001", "damagedDecRatioMin", "16777216", "0"),
            ("190100", "damagedDecRatioMin", "-16777216", "0"),
            ("301001", "damagedDecRatioMin", "16777216", "0"),
            ("400001", "damagedDecRatioMin", "-16777216", "0"));
        var store = new WeaponCriticalCatalogTests.FixtureStore(new() { ["Config/ShopItem2.xml"] = workbook });
        var catalog = DefenceSeedCatalog.Load(store, TestBuilds.Packets, build, "Config/ShopItem2.xml", 1 << 20,
            "defence_workbook_max_bytes", Pets());
        var equipment = new MatchSeedEquipment(1, [301001, 400001, 190100, 300001], itemAttributes:
        [
            Attribute(301001, PetCombatEffectKind.AddDefenceMinimumRatio, 0.125f),
            Attribute(301001, PetCombatEffectKind.AddDefenceMinimumRatio, 0.125f),
        ]);
        Assert.Equal(new DefencePercentRange(25, 0), catalog.Range(equipment));
    }

    /// <summary>Excluded modes apply no instance effects, and the allowlist is frozen when the consumer is created.</summary>
    [Fact]
    public void ExplicitModeEligibilityCannotBeMutatedAfterActivation()
    {
        int[] modes = [1];
        var effects = new PetSeedEffects(new FixtureEffects(), modes);
        modes[0] = 2;
        var attribute = Attribute(190100, PetCombatEffectKind.AssignCriticalChancePercent, 2);
        var catalog = new CombatSeedCatalog(new Dictionary<int, PetCombatEffect[]>(), effects);
        Assert.Equal(0.02, catalog.Contributions(new(1, [190100], itemAttributes: [attribute])).Critical);
        Assert.Equal(0, catalog.Contributions(new(2, [190100], itemAttributes: [attribute])).Critical);
        Assert.False(new SceneSeedsOptions().PetCombatEffects);
        Assert.False(new SceneSeedsOptions().CriticalCancellationSeeds);
        Assert.NotEmpty(new SceneSeedsOptions { PetCombatEffects = true }.Validate());
        Assert.NotEmpty(new SceneSeedsOptions { PetCombatModeIds = [1, 1] }.Validate());
        Assert.Empty(new SceneSeedsOptions { PetCombatEffects = true, PetCombatModeIds = [1] }.Validate());
    }

    /// <summary>Cancellation tables use only 1000 readable bits and remain stable for the same actor and round.</summary>
    [Fact]
    public void CancellationTableOwnsActorRoundIdentityAndPreservesPadding()
    {
        var catalog = new CombatSeedCatalog(new Dictionary<int, PetCombatEffect[]>(), Pets());
        var equipment = Equipment(Attribute(190100, PetCombatEffectKind.AddCriticalCancellationRatio, 0.5f));
        var first = catalog.CancellationTable(41, 7, 1_000_000, equipment);
        Assert.Equal(32, first.Length);
        Assert.Equal(0u, first[^1] & 0xFFFFFF00u);
        Assert.Equal(first, catalog.CancellationTable(41, 7, 1_000_000, equipment));
        Assert.NotEqual(first, catalog.CancellationTable(41, 8, 1_000_000, equipment));
        Assert.NotEqual(first, catalog.CancellationTable(41, 7, 1_100_000, equipment));
        var certain = catalog.CancellationTable(41, 7, 1_000_000,
            Equipment(Attribute(190100, PetCombatEffectKind.AddCriticalCancellationRatio, 1)));
        Assert.Equal(1000, certain.Sum(word => System.Numerics.BitOperations.PopCount(word)));
        Assert.All(catalog.CancellationTable(41, 7, 1_000_000, null), word => Assert.Equal(0u, word));
    }

    /// <summary>Static cancellation data is read independently of the weapon resource family.</summary>
    [Fact]
    public void StaticCombatCatalogNeedsNoWeaponFiles()
    {
        var store = new WeaponCriticalCatalogTests.FixtureStore(new() { ["Config/ShopItem2.xml"] = DefenceSeedTests.ItemWorkbook() });
        var catalog = CombatSeedCatalog.Load(store, "Config/ShopItem2.xml", 1 << 20, true, true, Pets());
        Assert.Equal(0.5f, catalog.Contributions(new(1, [100072])).Cancellation);
        Assert.Equal(0, catalog.Contributions(new(1, [100072])).Critical);
        Assert.Throws<ConfigException>(() => CombatSeedCatalog.Load(store, "Config/ShopItem2.xml", 16, true, true, null));
    }

    internal static PetSeedEffects Pets(string protocol = "kr-1256/game") => new(new FixtureEffects(protocol), [1]);

    internal static MatchSeedEquipment Equipment(params RoomItemAttribute[] attributes) =>
        new(1, [190100], itemAttributes: attributes);

    internal static RoomItemAttribute Attribute(int item, PetCombatEffectKind kind, float amount) =>
        new(item, (uint)kind, [BitConverter.SingleToUInt32Bits(amount)], 0, "");

    internal sealed class FixtureEffects(string protocol = "kr-1256/game") : IPetCombatEffects
    {
        /// <inheritdoc />
        public string Protocol => protocol;

        /// <summary>Returns the test's explicitly selected operation and float payload without native catalog conversion.</summary>
        public bool TryResolve(RoomItemAttribute attribute, out ImmutableArray<PetCombatEffect> effects)
        {
            effects = [new((PetCombatEffectKind)attribute.Kind, BitConverter.UInt32BitsToSingle(attribute.Values[0]))];
            return true;
        }
    }
}
