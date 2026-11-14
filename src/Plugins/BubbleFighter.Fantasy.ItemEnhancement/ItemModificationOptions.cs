using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Defines explicit modification material, currency and preview policy without inferred economy defaults.</summary>
public sealed class ItemModificationOptions
{
    /// <summary>Gets or sets whether modification apply and delete are admitted by the owning enhancement row.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the quantity debited for each of the three selected parts.</summary>
    public int? PartQuantity { get; set; }

    /// <summary>Gets or sets the explicitly chosen Lucci price of one accepted apply operation, including zero.</summary>
    public int? ApplyLucciCost { get; set; }

    /// <summary>Gets or sets the explicitly chosen Lucci price of deleting one installed group, including zero.</summary>
    public int? DeleteLucciCost { get; set; }

    /// <summary>Gets or sets the complete part matrix used by preview and result selection.</summary>
    public ModificationPartOptions[] Parts { get; set; } = [];

    /// <summary>Gets or sets first-category weights for each installed-count state.</summary>
    public ModificationWeightOptions[] TagWeights { get; set; } = [];

    /// <summary>Gets or sets second-category weights for each installed-count state.</summary>
    public ModificationWeightOptions[] EffectWeights { get; set; } = [];

    /// <summary>Validates every bounded policy value and the complete category cross product before admission.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Enabled) yield break;
        if (PartQuantity is null or < 1 || PartQuantity > int.MaxValue / 3 || ApplyLucciCost is null or < 0 || DeleteLucciCost is null or < 0)
            yield return new("Modification requires explicit part_quantity whose three-part debit fits Int32, and nonnegative apply_lucci_cost and delete_lucci_cost.");
        if (Parts is null || Parts.Length is < 1 or > 24 || Parts.Any(part => part is null || !part.IsValid())
            || Parts.Select(part => part.ItemId).Distinct().Count() != Parts.Length
            || Parts.Select(part => (part.NumericTag, part.EffectCategory)).Distinct().Count() != Parts.Length)
        {
            yield return new("Modification parts must contain distinct items and category pairs with seven signed effect outcomes.");
            yield break;
        }
        // kr-1256 sub_DFA540 resolves installed selectors together with their numeric tag to one part configuration.
        if (Parts.SelectMany(part => part.AttributeIds.Select(id => (part.NumericTag, Id: id))).Distinct().Count() != Parts.Length * 7)
        {
            yield return new("Modification selectors must identify exactly one part configuration within each numeric tag.");
            yield break;
        }
        if (!ValidWeights(TagWeights, 0, 2) || !ValidWeights(EffectWeights, 1, 8))
        {
            yield return new("Modification random tables require distinct keys and three positive bounded weights per category.");
            yield break;
        }
        if (!TagWeights.Select(row => row.Key).Order().SequenceEqual(Parts.Select(part => part.NumericTag).Distinct().Order())
            || !EffectWeights.Select(row => row.Key).Order().SequenceEqual(Parts.Select(part => part.EffectCategory).Distinct().Order())
            || TagWeights.Any(tag => EffectWeights.Any(effect => !Parts.Any(part => part.NumericTag == tag.Key && part.EffectCategory == effect.Key))))
            yield return new("Modification parts must cover every combination of configured random-table categories.");
    }

    internal ModificationConfiguration Configuration() => new(Array.AsReadOnly(Parts.Select(part =>
        new ModificationPartConfiguration(part.ItemId, part.NumericTag, part.EffectCategory,
            Array.AsReadOnly(part.AttributeIds.ToArray()), part.Value0,
            part.NegativeValues.ToFrozenDictionary(value => value.AttributeId, value => value.Value))).ToArray()),
        TagWeights.ToFrozenDictionary(row => row.Key, row => (IReadOnlyList<int>)Array.AsReadOnly(row.Weights.ToArray())),
        EffectWeights.ToFrozenDictionary(row => row.Key, row => (IReadOnlyList<int>)Array.AsReadOnly(row.Weights.ToArray())));

    // kr-1256 sub_DE9260 normalizes three selected parts; repeated parts require positive weights and an Int32 sampling sum.
    private static bool ValidWeights(ModificationWeightOptions[]? rows, int first, int last) => rows is { Length: > 0 }
        && rows.Length <= last - first + 1
        && rows.All(row => row is not null && row.Key >= first && row.Key <= last && row.Weights is { Length: 3 }
            && row.Weights.All(weight => weight > 0 && weight <= int.MaxValue / 3))
        && rows.Select(row => row.Key).Distinct().Count() == rows.Length;
}

/// <summary>Configures one native part's category pair and seven possible installed attributes.</summary>
public sealed class ModificationPartOptions
{
    /// <summary>Gets or sets the count-based part's native item ID.</summary>
    public int ItemId { get; set; }
    /// <summary>Gets or sets its first category, also carried as decimal text on installed attributes.</summary>
    public int NumericTag { get; set; }
    /// <summary>Gets or sets its second category used by modification outcome selection.</summary>
    public int EffectCategory { get; set; }
    /// <summary>Gets or sets exactly seven native ItemAttr selectors.</summary>
    public int[] AttributeIds { get; set; } = [];
    /// <summary>Gets or sets positive effect units; 160 is displayed as 1.60 percent.</summary>
    public int Value0 { get; set; }
    /// <summary>Gets or sets one nonpositive effect value per selector.</summary>
    public ModificationNegativeOptions[] NegativeValues { get; set; } = [];

    // kr-1256 sub_DE9260 seeds category keys 0..2 and 1..8 from constants at 0x1641F90 and 0x1641FB0.
    internal bool IsValid() => ItemId > 0 && ItemId < (long)PetArticleKeys.First
        && NumericTag is >= 0 and <= 2 && EffectCategory is >= 1 and <= 8 && Value0 > 0 && float.IsFinite(Value0 / 100f)
        && AttributeIds is { Length: 7 } && AttributeIds.All(id => id is >= 101 and <= 156) && AttributeIds.Distinct().Count() == 7
        && NegativeValues is { Length: 7 } && NegativeValues.All(value => value is not null && value.Value <= 0 && float.IsFinite(value.Value / 100f))
        && NegativeValues.Select(value => value.AttributeId).Order().SequenceEqual(AttributeIds.Order());
}

/// <summary>Configures the signed negative effect paired with one native modification selector.</summary>
public sealed class ModificationNegativeOptions
{
    /// <summary>Gets or sets the ItemAttr selector receiving this paired value.</summary>
    public int AttributeId { get; set; }
    /// <summary>Gets or sets signed effect units; -40 is displayed as -0.40 percent.</summary>
    public int Value { get; set; }
}

/// <summary>Configures one category's three weights for zero, one and two preexisting modifications.</summary>
public sealed class ModificationWeightOptions
{
    /// <summary>Gets or sets the native category key.</summary>
    public int Key { get; set; }
    /// <summary>Gets or sets the positive bounded weight for each supported installed count.</summary>
    public int[] Weights { get; set; } = [];
}
