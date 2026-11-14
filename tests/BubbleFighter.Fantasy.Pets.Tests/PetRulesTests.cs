using System.Collections.Immutable;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Checks skill replacement group exclusions and build-configured growth rules against persisted pet state.</summary>
public sealed class PetRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Rejects a replacement that conflicts with a retained skill's random group even when selectors differ.</summary>
    [Fact]
    public void ReplacementRejectsAnotherSkillsGroupEvenWhenItsSelectorDiffers()
    {
        var rules = Rules(allowDuplicates: true);
        var result = rules.Change(State(11, 12), 11, 14);
        Assert.Equal(PetFailure.Unavailable, result.Failure);
        Assert.Null(result.State);
    }

    /// <summary>Exempts groups of the removed skill so a replacement may reuse its shared visual group.</summary>
    [Fact]
    public void ReplacementRemovesOldGroupsBeforeCheckingSharedVisualGroups()
    {
        var rules = Rules();
        var state = State(11, 12);
        var result = rules.Change(state, 11, 13);
        Assert.Equal(PetFailure.None, result.Failure);
        Assert.Equal(13, result.State!.FirstSkill!.Selector);
        Assert.Equal(state.SecondSkill, result.State.SecondSkill);
    }

    /// <summary>Excludes the old skill's groups from the complete conflict set even when another saved skill shares them.</summary>
    [Fact]
    public void OldGroupsAreExcludedGloballyRatherThanOnlyRemovingTheOldSlot()
    {
        var rules = Rules();
        var result = rules.Change(State(11, 17), 11, 13);
        Assert.Equal(PetFailure.None, result.Failure);
        Assert.Equal(13, result.State!.FirstSkill!.Selector);
        Assert.Equal(17, result.State.SecondSkill!.Selector);
    }

    /// <summary>Refuses replacing a skill with itself instead of granting the removed-group exemption.</summary>
    [Fact]
    public void UnchangedReplacementHasNoGroupExemption()
    {
        var rules = Rules(allowDuplicates: true);
        Assert.Equal(PetFailure.Unavailable, rules.Change(State(11), 11, 11).Failure);
    }

    /// <summary>Changes only the second skill while retaining the first skill, grade, experience and feeding timestamp.</summary>
    [Fact]
    public void ReplacementOfSecondSkillPreservesFirstSkillAndGrowthState()
    {
        var rules = Rules();
        var state = State(11, 12) with { Experience = 4, LastFedAt = Now };
        var result = rules.Change(state, 12, 15);
        Assert.Equal(PetFailure.None, result.Failure);
        Assert.Equal(state.FirstSkill, result.State!.FirstSkill);
        Assert.Equal(15, result.State.SecondSkill!.Selector);
        Assert.Equal(state.Grade, result.State.Grade);
        Assert.Equal(state.Experience, result.State.Experience);
        Assert.Equal(state.LastFedAt, result.State.LastFedAt);
    }

    /// <summary>Validates the currently saved selector before replacement can remove evidence of corrupt progression.</summary>
    [Fact]
    public void UnknownCurrentSelectorFailsBeforeReplacementCanDiscardIt()
    {
        var rules = Rules();
        Assert.Throws<InvalidDataException>(() => rules.Change(State(999), 999, 15));
    }

    /// <summary>Uses the legacy grade-specific growth pool without imposing the modern replacement group exclusions.</summary>
    [Fact]
    public void LegacyGrowthDoesNotInheritModernReplacementGroupRestrictions()
    {
        var catalog = Catalog();
        var rules = new PetRules(catalog, new PetsOptions { SharedSkillPool = false, AllowDuplicateSkills = false }, new Random(1));
        var state = State(11) with { Experience = catalog.GradeExperienceMaximum(1) };
        var result = rules.Grow(state, catalog.Items[301234]);
        Assert.Equal(PetFailure.None, result.Failure);
        Assert.Equal(2, result.State!.Grade);
        Assert.Equal(21, result.State.SecondSkill!.Selector);
    }

    /// <summary>Honors the configured duplicate-selector policy when a shared growth pool contains only the existing skill.</summary>
    [Fact]
    public void SharedPoolGrowthKeepsItsConfiguredSelectorDuplicatePolicy()
    {
        var catalog = Catalog(singleGrowthCandidate: true);
        var state = State(11) with { Experience = catalog.GradeExperienceMaximum(1) };
        var rules = new PetRules(catalog, new PetsOptions { SharedSkillPool = true, AllowDuplicateSkills = false }, new Random(1));
        Assert.Throws<InvalidDataException>(() => rules.Grow(state, catalog.Items[301234]));
        rules = new PetRules(catalog, new PetsOptions { SharedSkillPool = true, AllowDuplicateSkills = true }, new Random(1));
        Assert.Equal(11, rules.Grow(state, catalog.Items[301234]).State!.SecondSkill!.Selector);
    }

    private static PetRules Rules(bool allowDuplicates = false) =>
        new(Catalog(), new PetsOptions { AllowDuplicateSkills = allowDuplicates, SharedSkillPool = true }, new Random(1));

    private static PetProgression State(int first, int? second = null) => new(second is null ? 1 : 2, 0, Now, null,
        new PetSkill(first, 1, first == 15 ? 0 : 1, 0, 0), second is { } selector ? new PetSkill(selector, 1, 1, 0, 0) : null);

    private static PetCatalog Catalog(bool singleGrowthCandidate = false)
    {
        var items = new Dictionary<int, PetItem> { [301234] = new(301234, 10001, 2, 777) };
        var skills = new Dictionary<int, PetSkillDefinition>
        {
            [7] = new(7, 777, 0, 100, 0, [0, 18, 16, 0]),
            [11] = Skill(11, 1, 1, 1, 2),
            [12] = Skill(12, 1, 1, 3, 2),
            [13] = Skill(13, 1, 1, 4, 2),
            [14] = Skill(14, 1, 1, 5, 2),
            [15] = Skill(15, 1, 1, 6),
            [17] = Skill(17, 1, 1, 1, 2),
            [21] = Skill(21, 2, 1, 1, 2),
        };
        if (singleGrowthCandidate)
            foreach (var key in skills.Keys.Where(key => key is not (7 or 11)).ToArray())
                skills[key] = skills[key] with { Weight = 0 };
        var buffs = new Dictionary<int, PetBuffDefinition>
        {
            [1] = Buff(1, 100), [2] = Buff(2, 200), [3] = Buff(3, 101),
            [4] = Buff(4, 100), [5] = Buff(5, 101), [6] = Buff(6, 102),
            [16] = new(16, "gradeExp1", 6, 0, 10, 1, 0, 10),
            [17] = new(17, "gradeExp2", 7, 0, 100, 1, 0, 100),
            [18] = new(18, "hunger", 8, 86, 100, 1, 86, 100),
        };
        return new PetCatalog(items, skills, buffs, 777);
    }

    private static PetSkillDefinition Skill(int selector, int grade, int weight, int first, int second = 0) =>
        new(selector, 777, grade, weight, grade, ImmutableArray.Create(first, second, 0, 0));

    private static PetBuffDefinition Buff(int index, int group) => new(index, "testBuff", group, 1, 1, 1, 1, 1);
}
