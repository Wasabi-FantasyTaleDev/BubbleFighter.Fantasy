using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>
/// The grade-up rules the server publishes in S2C 162 and applies to requests with the client's own rule engine
/// (kr-1256 sub_114B7F0), so a request is admitted exactly when the client would have offered its card.
/// </summary>
internal sealed class AdvancementPolicy
{
    // kr-1256 sub_10E7EC0 derives tier 4 from effect 100, and tier 6 from 157 (HC or AdvHC) or 158 (L4HC); the sheet names them
    // "normal HC to advanced HC", "advanced HC to advanced L4HC" and "normal L4HC to advanced L4HC".
    private const int ToAdvancedHc = 100;
    private const int ToAdvancedL4FromAdvancedHc = 157;
    private const int ToAdvancedL4FromL4 = 158;

    private readonly Dictionary<int, List<Entry>> _groups = [];

    internal AdvancementPolicy(ItemAdvancementOptions options, EnhancementCatalog catalog, InventoryLoadoutScope scope)
    {
        var errors = options.Validate().ToArray();
        if (!options.Enabled || errors.Length != 0) throw new ConfigException("Advancement policy is disabled or invalid.");
        foreach (var effect in new[] { ToAdvancedHc, ToAdvancedL4FromAdvancedHc, ToAdvancedL4FromL4 })
            try { _ = catalog.AdvancementValues(effect); }
            catch (InvalidDataException exception) { throw new ConfigException(exception.Message); }
        foreach (var rule in options.Rules)
        {
            if (!scope.Supports(rule.CardItemId) || !catalog.Items.TryGetValue(rule.CardItemId, out var card) || card.DurationType != "count"
                || card.IsUnlimited || card.Kind != ItemEnhancementKind.None)
                throw new ConfigException($"Advancement card {rule.CardItemId} must be a counted catalog material of this build.");
            foreach (var setId in rule.SetIds)
                if (!catalog.Sets.TryGetValue(setId, out var set) || set.Members.Any(member => !catalog.Items.TryGetValue(member, out var item)
                        || item.Kind != ItemEnhancementKind.Clothing || item.AdvancementClass == 0))
                    throw new ConfigException($"Advancement set {setId} must be a native set of grade-up clothing.");
                else if (!ClassesOf(rule.SourceTier).Contains(ClassOf(catalog, set)))
                    throw new ConfigException($"Advancement set {setId} has class {ClassOf(catalog, set)}, which a tier {rule.SourceTier} rule never matches.");
            var quantity = rule.CardQuantity!.Value;
            // Rules are keyed by the set class (the largest member isAdvancedable: 1 is L3, 2 is AdvL3, 3 is L4). An HC set that already
            // reached advanced HC keeps class 1, so its next rule lists effect 100 as required.
            switch (rule.SourceTier)
            {
                case 3:
                    Add(1, rule, quantity, 4, [ToAdvancedHc, ToAdvancedL4FromAdvancedHc], [], ToAdvancedHc);
                    break;
                case 4:
                    Add(2, rule, quantity, 6, [ToAdvancedL4FromAdvancedHc], [], ToAdvancedL4FromAdvancedHc);
                    Add(1, rule, quantity, 6, [ToAdvancedL4FromAdvancedHc], [ToAdvancedHc], ToAdvancedL4FromAdvancedHc);
                    break;
                default:
                    Add(3, rule, quantity, 6, [ToAdvancedL4FromL4], [], ToAdvancedL4FromL4);
                    break;
            }
        }
        Configuration = new(options.TabCardItemId!.Value, _groups.OrderBy(group => group.Key).Select(group =>
            new ScienceLabAdvancementGroup(group.Key, group.Value.Select(entry => entry.Published).ToArray())).ToArray());
    }

    /// <summary>Gets the rule groups published to the client.</summary>
    internal ScienceLabAdvancementConfiguration Configuration { get; }

    /// <summary>Gets the set class the client keys its rules by: the largest member isAdvancedable, or zero for none.</summary>
    internal static int ClassOf(EnhancementCatalog catalog, EnhancementSet set) =>
        set.Members.Max(member => catalog.Items.TryGetValue(member, out var item) ? item.AdvancementClass : 0);

    /// <summary>
    /// Applies kr-1256 sub_114B7F0 and sub_D3BB40: the rules whose effects allow the set must agree on one marker, and the
    /// selected card must be one of them. Returns its cost and the effect it installs, or null when the client would refuse.
    /// </summary>
    internal (int Quantity, int Effect)? Match(int setId, int setClass, IReadOnlySet<int> effects, int cardId)
    {
        if (!_groups.TryGetValue(setClass, out var entries)) return null;
        var accepted = entries.Where(entry => (entry.Published.SetIds.Count == 0 || entry.Published.SetIds.Contains(setId))
            && !entry.Published.BlockingEffects.Any(effects.Contains) && entry.Published.RequiredEffects.All(effects.Contains)).ToArray();
        if (accepted.Length == 0 || accepted.Select(entry => entry.Published.TargetMarker).Distinct().Count() != 1) return null;
        var chosen = accepted.SingleOrDefault(entry => entry.Published.CardItemId == cardId);
        return chosen is null ? null : (chosen.Quantity, chosen.Effect);
    }

    // The set classes each source tier's rules are published under, as the switch in the constructor adds them.
    private static int[] ClassesOf(int sourceTier) => sourceTier switch { 3 => [1], 4 => [1, 2], _ => [3] };

    private void Add(int group, AdvancementRuleOptions rule, int quantity, byte marker, int[] blocking, int[] required, int effect)
    {
        if (!_groups.TryGetValue(group, out var entries)) _groups[group] = entries = [];
        entries.Add(new(new ScienceLabAdvancementRule(rule.CardItemId, blocking, required, rule.SetIds, marker), quantity, effect));
    }

    private sealed record Entry(ScienceLabAdvancementRule Published, int Quantity, int Effect);
}
