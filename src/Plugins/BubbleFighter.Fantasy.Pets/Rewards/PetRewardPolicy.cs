using System.Collections.Frozen;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets.Rewards;

internal sealed class PetRewardPolicy
{
    private readonly PetCatalog _catalog;
    private readonly FrozenSet<int> _modeIds;
    private readonly PetRewardStacking _stacking;
    private readonly PetRewardRounding _rounding;
    private readonly bool _includeCollectedLucci;
    private readonly FrozenDictionary<string, Effect> _effects;

    internal PetRewardPolicy(PetCatalog catalog, PetRewardOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        var failures = options.Validate().Select(result => result.ErrorMessage ?? "Invalid pet reward policy.").ToArray();
        if (failures.Length != 0) throw new ConfigException(string.Join(" ", failures));
        _catalog = catalog;
        Enabled = options.Enabled;
        _modeIds = options.ModeIds.ToFrozenSet();
        _stacking = options.Stacking;
        _rounding = options.Rounding;
        _includeCollectedLucci = options.IncludeCollectedLucci;
        _effects = new Dictionary<string, Effect>(StringComparer.Ordinal)
        {
            ["lucciBonus1"] = new(false, options.LucciBonus1Multiplier),
            ["lucciBonus2"] = new(false, options.LucciBonus2Multiplier),
            ["lucciBonus3"] = new(false, options.LucciBonus3Multiplier),
            ["expBonus1"] = new(true, options.ExpBonus1Multiplier),
            ["expBonus2"] = new(true, options.ExpBonus2Multiplier),
            ["expBonus3"] = new(true, options.ExpBonus3Multiplier),
        }.ToFrozenDictionary(StringComparer.Ordinal);
        foreach (var buff in catalog.Buffs.Values.Where(buff => _effects.ContainsKey(buff.BuffName)))
            if (buff.Minimum != 0 || buff.Maximum != 0 || buff.Conversion != 0
                || buff.RawMinimum != 0 || buff.RawMaximum != 0)
                throw new InvalidDataException($"Native reward buff {buff.BuffName} must retain its zero-valued payload.");
    }

    internal bool Enabled { get; }

    internal RoundRewardAmounts Apply(int modeId, RoundEquipmentSnapshot equipment, RoundRewardAmounts reward)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(reward);
        reward.Validate();
        if (!Enabled || !_modeIds.Contains(modeId)) return reward;

        var pets = equipment.WornItemIds.Where(id => _catalog.Items.TryGetValue(id, out var item)
            && item.Function == PetCatalog.PetFunction && item.Category == _catalog.PetCategory).ToHashSet();
        var stages = new HashSet<(int ItemId, uint Stage)>();
        decimal experienceFactor = 1m, lucciFactor = 1m;
        foreach (var attribute in equipment.ItemAttributes.Where(attribute => pets.Contains(attribute.ItemId)))
        {
            if (attribute.Values.Count != 4 || attribute.Kind > int.MaxValue
                || !stages.Add((attribute.ItemId, attribute.Unknown28)))
                throw new InvalidDataException("A worn pet has a malformed or repeated native attribute stage.");
            if (attribute.Kind == PetCatalog.BaseSelector)
            {
                if (attribute.Unknown28 != 0)
                    throw new InvalidDataException("A worn pet's base attribute must use acquisition stage zero.");
                continue;
            }
            if (attribute.Unknown28 is not (1 or 2))
                throw new InvalidDataException("An earned pet attribute must identify acquisition stage one or two.");
            var skill = new PetSkill((int)attribute.Kind, unchecked((int)attribute.Values[0]),
                unchecked((int)attribute.Values[1]), unchecked((int)attribute.Values[2]), unchecked((int)attribute.Values[3]));
            _catalog.ValidateSkill(skill);
            foreach (var index in _catalog.Skills[skill.Selector].BuffIndices)
            {
                if (index == 0 || !_effects.TryGetValue(_catalog.Buffs[index].BuffName, out var effect)) continue;
                if (effect.Experience) experienceFactor = Combine(experienceFactor, effect.Multiplier);
                else lucciFactor = Combine(lucciFactor, effect.Multiplier);
            }
        }
        var adjusted = new RoundRewardAmounts(Scale(reward.Experience, experienceFactor),
            Scale(reward.CompletionLucci, lucciFactor),
            _includeCollectedLucci ? Scale(reward.CollectedLucci, lucciFactor) : reward.CollectedLucci);
        adjusted.Validate();
        return adjusted;
    }

    private decimal Combine(decimal current, decimal next) => _stacking switch
    {
        PetRewardStacking.Highest => Math.Max(current, next),
        PetRewardStacking.AdditiveBonus => checked(current + next - 1m),
        PetRewardStacking.Multiplicative => checked(current * next),
        _ => throw new InvalidOperationException("The pet reward stacking policy is invalid."),
    };

    private int Scale(int value, decimal factor)
    {
        var scaled = checked(value * factor);
        var rounded = _rounding switch
        {
            PetRewardRounding.Floor => decimal.Floor(scaled),
            PetRewardRounding.Nearest => decimal.Round(scaled, 0, MidpointRounding.AwayFromZero),
            PetRewardRounding.Ceiling => decimal.Ceiling(scaled),
            _ => throw new InvalidOperationException("The pet reward rounding policy is invalid."),
        };
        return checked((int)rounded);
    }

    private sealed record Effect(bool Experience, decimal Multiplier);
}
