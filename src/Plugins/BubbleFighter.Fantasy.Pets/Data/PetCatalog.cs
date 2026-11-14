using System.Collections.Frozen;
using System.Collections.Immutable;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Pets.Data;

/// <summary>Native item function, parameter and resolved subcategory for one inventory identity.</summary>
internal sealed record PetItem(int ItemId, int Function, int Parameter, int Category)
{
    internal string? DurationType { get; init; }
    internal int? MaximumDuration { get; init; }
}

/// <summary>One ItemAttr definition, with all four positional references preserved.</summary>
internal sealed record PetSkillDefinition(int Selector, int Category, int Grade, int Weight,
    int Value, ImmutableArray<int> BuffIndices);

/// <summary>Native integer bounds and decimal source values for one RandomTable entry.</summary>
internal sealed record PetBuffDefinition(int BuffIndex, string BuffName, int RandomGroup,
    int Minimum, int Maximum, decimal Conversion, decimal RawMinimum, decimal RawMaximum);

/// <summary>Validated native pet data and value checks shared by the pet operation handlers.</summary>
internal sealed class PetCatalog
{
    // kr-812 sub_9782C0 and kr-1256 sub_806530 identify followers by this native function.
    internal const int PetFunction = 10001;

    // kr-1256 sub_EE2A40 reads progression through this ItemAttr selector.
    internal const int BaseSelector = 7;

    internal PetCatalog(Dictionary<int, PetItem> items, Dictionary<int, PetSkillDefinition> skills,
        Dictionary<int, PetBuffDefinition> buffs, int petCategory)
    {
        Items = items.ToFrozenDictionary();
        Skills = skills.ToFrozenDictionary();
        Buffs = buffs.ToFrozenDictionary();
        PetCategory = petCategory;
        if (!Skills.TryGetValue(BaseSelector, out var state) || state.Category != PetCategory || state.Grade != 0
            || !state.BuffIndices.SequenceEqual(new[] { 0, 18, 16, 0 }))
            throw new InvalidDataException("Pet catalog has no matching base ItemAttr definition.");
        if (!Skills.Values.Any(skill => skill.Category == PetCategory && skill.Grade == 1 && skill.Weight > 0))
            throw new InvalidDataException("Pet catalog has no selectable first-grade skill definitions.");
        RequireMetadata(16, "gradeExp1");
        RequireMetadata(17, "gradeExp2");
        RequireMetadata(18, "hunger");
        if (HungerPeriodMinutes <= 0 || HungerMaximum <= 0
            || GradeExperienceMaximum(0) <= 0 || GradeExperienceMaximum(1) <= 0)
            throw new InvalidDataException("Pet catalog has nonpositive hunger or experience limits.");
    }

    internal IReadOnlyDictionary<int, PetItem> Items { get; }
    internal IReadOnlyDictionary<int, PetSkillDefinition> Skills { get; }
    internal IReadOnlyDictionary<int, PetBuffDefinition> Buffs { get; }
    internal int PetCategory { get; }

    // cn-717 sub_A48470 and kr-1256 sub_A0E390 obtain the hunger period and cap from buff18.
    internal int HungerPeriodMinutes => Buffs[18].Minimum;
    internal int HungerMaximum => Buffs[18].Maximum;

    /// <summary>Returns the native EXP cap for a nonnegative internal growth stage.</summary>
    internal int GradeExperienceMaximum(int grade)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(grade);
        // kr-1256 sub_EE2C70 selects buff16 at grade0 and buff17 at later stages.
        return Buffs[grade == 0 ? 16 : 17].Maximum;
    }

    /// <summary>Rolls four unscaled integers after the caller selects an eligible skill definition.</summary>
    internal PetSkill RollSkill(int selector, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var definition = RequireSkill(selector);
        if (definition.Weight <= 0)
            throw new InvalidDataException($"Pet skill {selector} is not in the native weighted pool.");
        int Roll(int index)
        {
            var reference = definition.BuffIndices[index];
            if (reference == 0) return 0;
            var buff = Buffs[reference];
            return checked((int)random.NextInt64(buff.Minimum, (long)buff.Maximum + 1));
        }
        return new PetSkill(selector, Roll(0), Roll(1), Roll(2), Roll(3));
    }

    /// <summary>Rejects unavailable selectors or persisted values outside this client's native bounds.</summary>
    internal void ValidateSkill(PetSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var definition = RequireSkill(skill.Selector);
        ReadOnlySpan<int> values = [skill.Value0, skill.Value1, skill.Value2, skill.Value3];
        for (var index = 0; index < values.Length; index++)
        {
            var reference = definition.BuffIndices[index];
            var valid = reference == 0 ? values[index] == 0
                : values[index] >= Buffs[reference].Minimum && values[index] <= Buffs[reference].Maximum;
            if (!valid)
                throw new InvalidDataException($"Pet skill {skill.Selector} value {index} is outside its native range.");
        }
    }

    private PetSkillDefinition RequireSkill(int selector)
    {
        if (selector == BaseSelector || !Skills.TryGetValue(selector, out var definition)
            || definition.Category != PetCategory || definition.Grade <= 0)
            throw new InvalidDataException($"ItemAttr {selector} is not an earned pet skill in this catalog.");
        return definition;
    }

    private void RequireMetadata(int index, string name)
    {
        if (!Buffs.TryGetValue(index, out var buff) || buff.BuffName != name)
            throw new InvalidDataException($"Pet catalog requires RandomTable {index} named {name}.");
    }
}
