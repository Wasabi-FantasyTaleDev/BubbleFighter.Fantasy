using System.Collections.Immutable;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets.Combat;

internal sealed class PetCombatEffects(string protocol, PetCatalog catalog, bool singlePrecisionInput = true) : IPetCombatEffects
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public bool TryResolve(RoomItemAttribute attribute, out ImmutableArray<PetCombatEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        effects = [];
        if (!catalog.Items.TryGetValue(attribute.ItemId, out var item)
            || item.Function != PetCatalog.PetFunction || item.Category != catalog.PetCategory)
            return false;
        if (attribute.Kind > int.MaxValue || attribute.Values is null || attribute.Values.Count != 4)
            throw new InvalidDataException("A pet combat modifier must contain one native selector and four signed words.");
        // kr-1256 sub_A0DED0 consumes every stored attribute without consulting its acquisition-stage tail.
        if (attribute.Kind == PetCatalog.BaseSelector) return true;
        var skill = new PetSkill((int)attribute.Kind, unchecked((int)attribute.Values[0]),
            unchecked((int)attribute.Values[1]), unchecked((int)attribute.Values[2]), unchecked((int)attribute.Values[3]));
        catalog.ValidateSkill(skill);
        var definition = catalog.Skills[skill.Selector];
        var result = ImmutableArray.CreateBuilder<PetCombatEffect>(4);
        for (var index = 0; index < 4; index++)
        {
            var reference = definition.BuffIndices[index];
            if (reference == 0) continue;
            var buff = catalog.Buffs[reference];
            PetCombatEffectKind? kind = buff.BuffName switch
            {
                "criFactorIncAmount" => PetCombatEffectKind.AssignCriticalChancePercent,
                "criticalCancelRatio" => PetCombatEffectKind.AddCriticalCancellationRatio,
                "damagedDecRatioMin" => PetCombatEffectKind.AddDefenceMinimumRatio,
                "damagedDecRatioMax" => PetCombatEffectKind.AddDefenceMaximumRatio,
                _ => null,
            };
            if (kind is null) continue;
            // kr-1256 sub_A0DED0 converts the integer before multiplying; cn-717 sub_A480E0 stores the wider product as float.
            var value = unchecked((int)attribute.Values[index]);
            var conversion = (float)buff.Conversion;
            var amount = singlePrecisionInput ? (float)value * conversion : (float)((double)value * conversion);
            if (!float.IsFinite(amount)) throw new InvalidDataException("Pet combat conversion exceeds the native float range.");
            result.Add(new(kind.Value, amount));
        }
        effects = result.ToImmutable();
        return true;
    }
}
