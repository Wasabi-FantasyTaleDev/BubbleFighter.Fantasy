using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Pets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class WeaponCriticalCatalogTests
{
    /// <summary>Supplied pet assignments follow lower and higher static item IDs in their original vector order.</summary>
    [Fact]
    public void PetCriticalAssignmentsFollowAllStaticRowsInSuppliedOrder()
    {
        var store = Fixture();
        AddEffect(store, 300001, "9");
        AddEffect(store, 190100, "6");
        AddEffect(store, 301001, "8");
        AddEffect(store, 400001, "4");
        var catalog = WeaponCriticalCatalog.Load(store, TestBuilds.Packets, "cn-324", new(), PetCombatSeedTests.Pets());
        var first = PetCombatSeedTests.Attribute(301001, PetCombatEffectKind.AssignCriticalChancePercent, 0.5f);
        var second = PetCombatSeedTests.Attribute(301001, PetCombatEffectKind.AssignCriticalChancePercent, 2);
        var later = new MatchSeedEquipment(1, [203103, 301001, 201001, 300001, 190100, 400001], itemAttributes: [first, second]);
        Assert.Equal(2, catalog.Attributes(later).OrdinaryPercent);
        Assert.Equal((0.04, 0.04), catalog.Contributions(later));
        var reversed = new MatchSeedEquipment(1, later.WornItemIds, itemAttributes: [second, first]);
        Assert.Equal(0.5f, catalog.Attributes(reversed).OrdinaryPercent);
        Assert.Equal((0.025, 0.025), catalog.Contributions(reversed));
        Assert.Equal(8, catalog.Attributes(new(1, [203103, 301001, 201001, 300001, 190100])).OrdinaryPercent);
    }

    /// <summary>A pet need not have a static critical row for its instance critical skill to be applied.</summary>
    [Fact]
    public void WeaponCatalogDoesNotSkipInstanceOnlyPets()
    {
        var catalog = WeaponCriticalCatalog.Load(Fixture(), TestBuilds.Packets, "cn-324", new(), PetCombatSeedTests.Pets());
        var equipment = new MatchSeedEquipment(1, [203103, 201001, 301001], itemAttributes:
            [PetCombatSeedTests.Attribute(301001, PetCombatEffectKind.AssignCriticalChancePercent, 2)]);
        Assert.Equal((0.04, 0.04), catalog.Contributions(equipment));
    }
}
