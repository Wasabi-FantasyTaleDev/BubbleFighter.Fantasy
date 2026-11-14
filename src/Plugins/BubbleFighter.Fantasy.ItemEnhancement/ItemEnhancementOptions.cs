using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Defines a catalog-scoped enhancement row and explicit server recipe policy; a configured -1 means that a policy value is absent.</summary>
public sealed class ItemEnhancementOptions : EnhancementArticleOptions
{
    private int? _successBasisPoints;
    private int? _cardQuantity;
    private int? _kitQuantity;
    private int? _additiveQuantity;
    private int? _additiveSuccessBasisPoints;

    /// <summary>Gets or sets the native enhancement card item supplied to the laboratory UI.</summary>
    [Range(0, int.MaxValue)] public int CardItemId { get; set; }

    /// <summary>Gets or sets the four native kit item identities in their UI order.</summary>
    public int[] KitItemIds { get; set; } = [];

    /// <summary>Gets or sets the optional additive's native item identity.</summary>
    [Range(0, int.MaxValue)] public int AdditiveItemId { get; set; }

    /// <summary>Gets or sets the explicitly chosen probability, in ten-thousandths, without an inferred default.</summary>
    [Range(0, 10000)] public int? SuccessBasisPoints
    {
        get => _successBasisPoints;
        set => _successBasisPoints = PolicyValue(value);
    }

    /// <summary>
    /// Gets or sets the success probability, in ten-thousandths, of an attempt that selects the additive. The client only
    /// marks the selection (kr-1256 sub_DD5E70); what the additive does is server policy, so it has no inferred default.
    /// </summary>
    [Range(0, 10000)] public int? AdditiveSuccessBasisPoints
    {
        get => _additiveSuccessBasisPoints;
        set => _additiveSuccessBasisPoints = PolicyValue(value);
    }

    /// <summary>Gets or sets the card debit per accepted attempt; both outcomes use the stated policy.</summary>
    [Range(1, 1000000)] public int? CardQuantity
    {
        get => _cardQuantity;
        set => _cardQuantity = PolicyValue(value);
    }

    /// <summary>Gets or sets the debit of each of the four kits per accepted attempt.</summary>
    [Range(2, 1000000)] public int? KitQuantity
    {
        get => _kitQuantity;
        set => _kitQuantity = PolicyValue(value);
    }

    /// <summary>Gets or sets the additive debit when the request selects the additive.</summary>
    [Range(1, 1000000)] public int? AdditiveQuantity
    {
        get => _additiveQuantity;
        set => _additiveQuantity = PolicyValue(value);
    }

    /// <summary>Gets or sets the maximum accounts whose write and publication can be active concurrently.</summary>
    [Range(1, 65536)] public int MaxInFlightAccounts { get; set; } = 128;

    /// <summary>Gets or sets separately enabled weapon modification policy and native preview content.</summary>
    public ItemModificationOptions Modification { get; set; } = new();

    /// <summary>Gets or sets separately enabled permanent conversion of individual timed items.</summary>
    public ItemConversionOptions Conversion { get; set; } = new();

    /// <summary>Gets or sets separately enabled grade-up of native clothing sets (the MakeItemAdvanced tab).</summary>
    public ItemAdvancementOptions Advancement { get; set; } = new();

    /// <summary>Validates identities and requires explicit economy choices before any row is enabled.</summary>
    public override IEnumerable<ValidationResult> Validate()
    {
        foreach (var result in base.Validate()) yield return result;
        if (Modification is null) { yield return new("modification configuration cannot be null."); yield break; }
        if (Modification.Enabled && !Enabled) yield return new("Modification requires the owning item-enhancement row to be enabled.");
        foreach (var result in Modification.Validate()) yield return result;
        if (Conversion is null) { yield return new("conversion configuration cannot be null."); yield break; }
        if (Conversion.Enabled && !Enabled) yield return new("Conversion requires the owning item-enhancement row to be enabled.");
        foreach (var result in Conversion.Validate()) yield return result;
        if (Advancement is null) { yield return new("advancement configuration cannot be null."); yield break; }
        if (Advancement.Enabled && !Enabled) yield return new("Advancement requires the owning item-enhancement row to be enabled.");
        foreach (var result in Advancement.Validate()) yield return result;
        if (!Enabled) yield break;
        if (CardItemId <= 0 || AdditiveItemId <= 0)
            yield return new("Enabled enhancement requires positive native material identities.");
        if (KitItemIds is null || KitItemIds.Length != 4 || KitItemIds.Any(id => id <= 0)
            || KitItemIds.Distinct().Count() != 4 || KitItemIds.Contains(CardItemId) || KitItemIds.Contains(AdditiveItemId)
            || CardItemId == AdditiveItemId)
            yield return new("Enhancement requires four distinct kits, a separate card and a separate additive.");
        if (SuccessBasisPoints is null || AdditiveSuccessBasisPoints is null || CardQuantity is null || KitQuantity is null || AdditiveQuantity is null)
            yield return new("Enhancement requires explicit success_basis_points, additive_success_basis_points, card_quantity, kit_quantity and additive_quantity.");
    }

    // Environment interpolation cannot carry YAML null; the profile's reserved -1 keeps missing policy distinct from zero success.
    private static int? PolicyValue(int? value) => value == -1 ? null : value;
}
