using BubbleFighter.Fantasy.Contracts.Enhancement;

namespace BubbleFighter.Fantasy.Dialect.Messages;

internal static class ModificationConfigurationWire
{
    internal static void Apply(Dictionary<string, object?> values, ModificationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration.Parts);
        ArgumentNullException.ThrowIfNull(configuration.TagWeights);
        ArgumentNullException.ThrowIfNull(configuration.EffectWeights);
        if (configuration.Parts.Count is < 1 or > 24 || configuration.Parts.Any(part => part is null)
            || configuration.Parts.Select(part => part.ItemId).Distinct().Count() != configuration.Parts.Count
            || configuration.Parts.Select(part => (part.NumericTag, part.EffectCategory)).Distinct().Count() != configuration.Parts.Count)
            throw new ArgumentException("Modification configuration requires distinct native part categories.", nameof(configuration));
        foreach (var part in configuration.Parts)
        {
            if (part.ItemId <= 0 || part.NumericTag is < 0 or > 2 || part.EffectCategory is < 1 or > 8
                || part.Value0 <= 0 || part.AttributeIds is null || part.AttributeIds.Count != 7
                || part.AttributeIds.Distinct().Count() != 7 || part.AttributeIds.Any(id => id is < 101 or > 156)
                || part.NegativeValues is null || part.NegativeValues.Count != 7
                || part.AttributeIds.Any(id => !part.NegativeValues.TryGetValue(id, out var amount) || amount > 0)
                || !configuration.TagWeights.ContainsKey(part.NumericTag)
                || !configuration.EffectWeights.ContainsKey(part.EffectCategory))
                throw new ArgumentException("Modification preview contains an invalid part, selector or effect magnitude.", nameof(configuration));
        }
        ValidateWeights(configuration.TagWeights, 0, 2);
        ValidateWeights(configuration.EffectWeights, 1, 8);
        if (!configuration.TagWeights.Keys.Order().SequenceEqual(configuration.Parts.Select(part => part.NumericTag).Distinct().Order())
            || !configuration.EffectWeights.Keys.Order().SequenceEqual(configuration.Parts.Select(part => part.EffectCategory).Distinct().Order())
            || configuration.TagWeights.Keys.Any(tag => configuration.EffectWeights.Keys.Any(effect =>
                !configuration.Parts.Any(part => part.NumericTag == tag && part.EffectCategory == effect))))
            throw new ArgumentException("Modification previews must cover every enabled category pair.", nameof(configuration));
        values["unknown_8"] = true;
        // kr-1256 sub_DE9AE0 reads configuration magnitudes as float percentages, unlike signed Article effect words.
        values["modification_configurations"] = configuration.Parts.OrderBy(part => part.ItemId).Select(part =>
            (object?)new Dictionary<string, object?>
            {
                ["unknown_4"] = part.ItemId,
                ["unknown_8"] = part.NumericTag,
                ["unknown_12"] = part.EffectCategory,
                ["unknown_16"] = part.AttributeIds.Cast<object?>().ToArray(),
                ["unknown_28"] = FloatPercentage(part.Value0),
                ["unknown_32"] = part.NegativeValues.OrderBy(pair => pair.Key).Select(pair =>
                    (object?)new Dictionary<string, object?> { ["key"] = pair.Key, ["value"] = FloatPercentage(pair.Value) }).ToArray(),
            }).ToArray();
        values["modification_random_table"] = new Dictionary<string, object?>
        {
            ["unknown_4"] = WeightRows(configuration.TagWeights),
            ["unknown_12"] = WeightRows(configuration.EffectWeights),
        };
    }

    private static uint FloatPercentage(int hundredths) => unchecked((uint)BitConverter.SingleToInt32Bits(hundredths / 100f));

    private static void ValidateWeights(IReadOnlyDictionary<int, IReadOnlyList<int>> weights, int first, int last)
    {
        if (weights.Count == 0 || weights.Any(pair => pair.Key < first || pair.Key > last || pair.Value is null
            || pair.Value.Count != 3 || pair.Value.Any(weight => weight <= 0 || weight > int.MaxValue / 3)))
            // kr-1256 sub_DE9260 normalizes three selected parts, including repeated categories.
            throw new ArgumentException("Modification weights require native categories and three positive entries whose repeated-part sum fits Int32.", nameof(weights));
    }

    private static object?[] WeightRows(IReadOnlyDictionary<int, IReadOnlyList<int>> weights) => weights.OrderBy(pair => pair.Key)
        .Select(pair => (object?)new Dictionary<string, object?>
        {
            ["key"] = pair.Key,
            ["values"] = pair.Value.Cast<object?>().ToArray(),
        }).ToArray();
}
