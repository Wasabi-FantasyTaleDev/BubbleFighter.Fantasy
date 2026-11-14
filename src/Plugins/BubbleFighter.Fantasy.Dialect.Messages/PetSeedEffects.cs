using System.Collections.Frozen;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Pets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Limits instance-effect application to the deployment's explicitly enabled native modes.</summary>
internal sealed class PetSeedEffects(IPetCombatEffects resolver, IEnumerable<int> modeIds)
{
    private readonly FrozenSet<int> _modes = modeIds.ToFrozenSet();

    internal IEnumerable<PetCombatEffect> ForEquipment(MatchSeedEquipment? equipment)
    {
        if (equipment is null || !_modes.Contains(equipment.ModeId)) yield break;
        foreach (var attribute in equipment.ItemAttributes)
        {
            if (!resolver.TryResolve(attribute, out var effects)) continue;
            foreach (var effect in effects)
            {
                if (!Enum.IsDefined(effect.Kind) || !float.IsFinite(effect.Amount))
                    throw new InvalidOperationException("A pet combat resolver returned an invalid native operation.");
                yield return effect;
            }
        }
    }
}
