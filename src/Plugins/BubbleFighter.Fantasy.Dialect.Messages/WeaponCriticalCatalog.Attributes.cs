using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Pets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

internal sealed partial class WeaponCriticalCatalog
{
    internal enum EffectKind { Ordinary, EquipmentThreshold, Duration, TimedAmount, EquipmentValue }

    internal sealed record CatalogEffect(EffectKind Kind, float Amount);

    internal sealed record EquipmentAttributes(float OrdinaryPercent, int RequiredEquipmentValue,
        int EquipmentValue, int DurationMs, float TimedPercent)
    {
        internal bool TimedEligible => EquipmentValue >= RequiredEquipmentValue && DurationMs > 0;
    }

    private static Dictionary<int, CatalogEffect[]> CriticalEffects(List<Dictionary<int, string>> rows)
    {
        var header = rows.FirstOrDefault(row => row.Values.Contains("shopItemId") && row.Values.Contains("buffName"))
            ?? throw new ConfigException("Weapon item catalog lacks the ShopItemSpec effect columns.");
        int Column(string name)
        {
            var columns = header.Where(pair => pair.Value == name).Select(pair => pair.Key).ToArray();
            return columns.Length == 1 ? columns[0]
                : throw new ConfigException($"ShopItemSpec requires one '{name}' column.");
        }
        var item = Column("shopItemId");
        var name = Column("buffName");
        var amount = Column("value");
        var channel = Column("channel");
        var group = Column("buffGroup");
        var effects = new Dictionary<int, List<CatalogEffect>>();
        foreach (var row in rows)
        {
            EffectKind? kind = Cell(row, name) switch
            {
                "criFactorIncAmount" => EffectKind.Ordinary,
                "AMequipCheckValue" => EffectKind.EquipmentThreshold,
                "AMcriFactorIncDuration" => EffectKind.Duration,
                "AMcriFactorIncAmount" => EffectKind.TimedAmount,
                "AMequipValue" => EffectKind.EquipmentValue,
                _ => null,
            };
            if (kind is null) continue;
            var id = Positive(Cell(row, item), "critical attribute item");
            if (Selector(Cell(row, channel), 0) != 0 || Selector(Cell(row, group), 0) != 0)
                throw new ConfigException($"Item {id} critical attribute requires an audited channel/group selector.");
            if (!effects.TryGetValue(id, out var values)) effects.Add(id, values = []);
            var text = Cell(row, amount);
            if (text.Length == 0) throw new ConfigException($"Item {id} critical attribute has no value.");
            var value = Chance(text, $"item {id} critical attribute");
            if (kind is EffectKind.EquipmentThreshold or EffectKind.EquipmentValue or EffectKind.Duration)
                _ = Integer(value);
            values.Add(new(kind.Value, value));
        }
        return effects.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    internal EquipmentAttributes Attributes(MatchSeedEquipment equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        if (equipment.WornItemIds.Count > maxWornItems)
            throw new InvalidOperationException("Seed equipment exceeds weapon_max_worn_items.");
        var ordinary = 0.0f;
        // cn-324 sub_825BE0 / cn-406 sub_891D40 initialize the threshold to 5 and the other four values to zero.
        var threshold = 5;
        var equipped = 0;
        var duration = 0;
        var timed = 0.0f;
        // id-400 sub_8FC4E0 applies the character first; kr-1256 sub_818AA0 limits that range to 300000..300999.
        foreach (var item in equipment.WornItemIds.OrderBy(id => id is >= 300000 and < 301000 ? 0 : 1).ThenBy(id => id))
        {
            foreach (var effect in catalogEffects.GetValueOrDefault(item) ?? [])
            {
                // cn-324 sub_816450 cases 3/74 assign; cases 76/77/80 accumulate with different native widths.
                switch (effect.Kind)
                {
                    case EffectKind.Ordinary: ordinary = effect.Amount; break;
                    case EffectKind.EquipmentThreshold: threshold = Integer(effect.Amount); break;
                    case EffectKind.Duration: duration = Integer((double)duration + effect.Amount); break;
                    case EffectKind.TimedAmount: timed += effect.Amount; break;
                    case EffectKind.EquipmentValue: equipped = checked(equipped + Integer(effect.Amount)); break;
                    default: throw new InvalidOperationException("Unknown catalog critical effect.");
                }
            }
        }
        // kr-812 sub_9871F0 applies the supplied scene vector through kr-812 sub_99C310 after every static item.
        if (pets is not null)
            foreach (var effect in pets.ForEquipment(equipment))
                if (effect.Kind == PetCombatEffectKind.AssignCriticalChancePercent) ordinary = effect.Amount;
        if (!float.IsFinite(ordinary) || !float.IsFinite(timed) || duration < 0)
            throw new InvalidOperationException("Equipped timed critical attributes have an invalid amount or duration.");
        return new(ordinary, threshold, equipped, duration, timed);
    }

    private static int Integer(double value)
    {
        var integer = Math.Truncate(value);
        if (!double.IsFinite(integer) || integer < int.MinValue || integer > int.MaxValue)
            throw new ConfigException("Catalog critical integer attribute exceeds the native signed 32-bit range.");
        return (int)integer;
    }
}
