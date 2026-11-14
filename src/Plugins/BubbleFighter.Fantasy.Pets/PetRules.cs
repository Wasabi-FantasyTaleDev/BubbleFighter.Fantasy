using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets;

internal enum PetFailure { None, Unavailable, Full, Cooldown, MaximumGrade, ExperienceNotFull, MaximumExperience, MissingSkill }

internal sealed record PetTransition(PetProgression? State, PetFailure Failure, int? Evolution = null)
{
    internal static PetTransition Refused(PetFailure failure) => new(null, failure);
}

internal sealed class PetRules(PetCatalog catalog, PetsOptions options, Random random)
{
    internal PetProgression Initial(DateTimeOffset now) => new(0, 0,
        Deadline(now, Math.Min(options.InitialFullness, catalog.HungerMaximum)), null, null, null);

    internal int Fullness(PetProgression state, DateTimeOffset now)
    {
        var minutes = (int)Math.Clamp(Math.Truncate((state.FullnessExpiresAt - now).TotalMinutes), int.MinValue, int.MaxValue);
        return Math.Clamp(minutes / catalog.HungerPeriodMinutes + 1, 0, catalog.HungerMaximum);
    }

    internal PetTransition Feed(PetProgression state, PetItem pet, PetItem food, DateTimeOffset now)
    {
        state.Validate();
        if (food.Function is not (19 or 20)) return PetTransition.Refused(PetFailure.Unavailable);
        var cooldown = options.FeedCooldownSeconds ?? (options.ManualLevelUp
            ? throw new InvalidOperationException("Manual-growth pet feeding requires an explicit cooldown policy.") : 0);
        var premium = food.Function == 20;
        var hunger = Fullness(state, now);
        var maxGrade = MaximumGrade(pet);
        if (!premium && options.RefuseNormalFoodWhenFull && hunger == catalog.HungerMaximum)
            return PetTransition.Refused(PetFailure.Full);
        if (!premium && state.LastFedAt is { } fed && now < fed.AddSeconds(cooldown))
            return PetTransition.Refused(PetFailure.Cooldown);
        if (options.ManualLevelUp && state.Grade >= maxGrade)
            return PetTransition.Refused(PetFailure.MaximumGrade);
        var cap = catalog.GradeExperienceMaximum(state.Grade);
        if (options.ManualLevelUp && state.Experience >= cap)
            return PetTransition.Refused(PetFailure.MaximumExperience);
        var experience = options.ExperienceFromFoodParameter ? food.Parameter
            : premium ? options.PremiumFoodExperience : options.NormalFoodExperience;
        if (experience < 0) throw new InvalidDataException("Pet food has negative native experience.");
        var next = state with
        {
            // kr-1256 sub_EDD680 sends only one use for function20; the native catalog promises a level in that use.
            Experience = options.ManualLevelUp && premium ? cap
                : state.Grade >= maxGrade ? state.Experience : checked(state.Experience + experience),
            FullnessExpiresAt = Deadline(now, Math.Min(catalog.HungerMaximum,
                checked(hunger + (premium ? options.PremiumFoodFullness : options.NormalFoodFullness)))),
            LastFedAt = now,
        };
        if ((!options.ManualLevelUp || premium) && state.Grade < maxGrade && next.Experience >= cap)
            return Grow(next, pet);
        return new(next with { Experience = Math.Min(next.Experience, cap) }, PetFailure.None);
    }

    internal PetTransition Grow(PetProgression state, PetItem pet)
    {
        state.Validate();
        if (state.Grade >= MaximumGrade(pet)) return PetTransition.Refused(PetFailure.MaximumGrade);
        var cap = catalog.GradeExperienceMaximum(state.Grade);
        if (state.Experience < cap) return PetTransition.Refused(PetFailure.ExperienceNotFull);
        var grade = state.Grade + 1;
        var excluded = options.AllowDuplicateSkills ? Array.Empty<int>()
            : new[] { state.FirstSkill?.Selector ?? 0, state.SecondSkill?.Selector ?? 0 };
        var candidates = catalog.Skills.Values.Where(skill => skill.Selector != 7
            && skill.Category == catalog.PetCategory && skill.Grade == (options.SharedSkillPool ? 1 : grade)
            && skill.Weight > 0 && !excluded.Contains(skill.Selector)).OrderBy(skill => skill.Selector).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("The native pet catalog has no eligible earned-ability pool.");
        var total = candidates.Sum(skill => (decimal)skill.Weight);
        var draw = (decimal)random.NextDouble() * total;
        var selected = candidates[^1];
        foreach (var candidate in candidates)
        {
            draw -= candidate.Weight;
            if (draw < 0) { selected = candidate; break; }
        }
        var earned = catalog.RollSkill(selected.Selector, random);
        return new(state with
        {
            Grade = grade,
            Experience = options.CarryGrowthExperience && grade < MaximumGrade(pet)
                ? Math.Min(state.Experience - cap, catalog.GradeExperienceMaximum(grade)) : 0,
            FirstSkill = grade == 1 ? earned : state.FirstSkill,
            SecondSkill = grade == 2 ? earned : null,
        }, PetFailure.None, 1);
    }

    internal PetTransition Reset(PetProgression state, int? selector)
    {
        state.Validate();
        if (state.Grade == 0) return PetTransition.Refused(PetFailure.MaximumGrade);
        if (selector is { } id && state.FirstSkill?.Selector != id && state.SecondSkill?.Selector != id)
            return PetTransition.Refused(PetFailure.MissingSkill);
        var retained = selector is { } chosen && state.FirstSkill?.Selector == chosen
            ? state.SecondSkill : state.FirstSkill;
        return new(state with { Grade = state.Grade - 1, Experience = 0,
            FirstSkill = state.Grade == 2 ? retained : null, SecondSkill = null }, PetFailure.None, 0);
    }

    internal PetTransition Change(PetProgression state, int previous, int replacement)
    {
        state.Validate();
        if (state.FirstSkill?.Selector != previous && state.SecondSkill?.Selector != previous)
            return PetTransition.Refused(PetFailure.MissingSkill);
        if (!catalog.Skills.TryGetValue(replacement, out var definition) || replacement == 7
            || definition.Category != catalog.PetCategory || definition.Grade != 1 || definition.Weight <= 0
            || HasReplacementConflict(state, previous, replacement))
            return PetTransition.Refused(PetFailure.Unavailable);
        var skill = catalog.RollSkill(replacement, random);
        return new(state with { FirstSkill = state.FirstSkill?.Selector == previous ? skill : state.FirstSkill,
            SecondSkill = state.FirstSkill?.Selector != previous && state.SecondSkill?.Selector == previous ? skill : state.SecondSkill }, PetFailure.None);
    }

    private bool HasReplacementConflict(PetProgression state, int previous, int replacement)
    {
        // kr-1256 sub_A0E990 excludes the replaced definition's groups globally, then checks the candidate's groups.
        HashSet<int> excluded = previous == replacement ? [] : Groups(previous);
        var occupied = Groups(PetCatalog.BaseSelector);
        if (state.FirstSkill is { } first) occupied.UnionWith(Groups(first.Selector));
        if (state.SecondSkill is { } second) occupied.UnionWith(Groups(second.Selector));
        occupied.ExceptWith(excluded);
        return Groups(replacement).Overlaps(occupied);
    }

    private HashSet<int> Groups(int selector)
    {
        if (!catalog.Skills.TryGetValue(selector, out var definition))
            throw new InvalidDataException($"Pet ItemAttr {selector} is absent from the current catalog.");
        return definition.BuffIndices.Where(reference => reference != 0)
            .Select(reference => catalog.Buffs[reference].RandomGroup).ToHashSet();
    }

    internal int MaximumGrade(PetItem pet)
    {
        var cap = options.GradeCapFromPetParameter ? pet.Parameter : options.MaximumGrade;
        return cap is >= 1 and <= 2 ? cap : throw new InvalidDataException("The native pet growth cap is outside supported stages.");
    }

    private DateTimeOffset Deadline(DateTimeOffset now, int fullness) =>
        now.AddMinutes(checked((fullness - 1) * catalog.HungerPeriodMinutes));
}
