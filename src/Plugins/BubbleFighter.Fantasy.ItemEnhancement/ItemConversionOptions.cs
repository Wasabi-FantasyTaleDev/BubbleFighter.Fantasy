using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Defines the permanent-conversion card, threshold and optional set cost; nothing is assumed when the tab is enabled.</summary>
public sealed class ItemConversionOptions
{
    /// <summary>Gets or sets whether the laboratory's permanent-conversion tab is shown and its requests admitted.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the counted card item one conversion spends.</summary>
    public int? CardItemId { get; set; }

    /// <summary>Gets or sets the whole days a target must still have; the client checks the same value before sending.</summary>
    public int? MinimumDays { get; set; }

    /// <summary>
    /// Gets or sets how many cards one native set conversion spends. Empty leaves set conversion refused: the client names
    /// one card for the tab but no per-set quantity, so the deployment states it.
    /// </summary>
    public int? SetCardQuantity { get; set; }

    /// <summary>Requires an explicit card and threshold whenever conversion is enabled.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Enabled) yield break;
        if (CardItemId is null or <= 0)
            yield return new("Conversion requires an explicit positive card_item_id.");
        // kr-1256 sub_806A20 treats more than 10000 remaining days as permanent, so no timed item can exceed that.
        if (MinimumDays is null or < 1 or > 10000)
            yield return new("Conversion requires an explicit minimum_days between 1 and 10000.");
        if (SetCardQuantity is < 1)
            yield return new("Conversion set_card_quantity must be positive when set.");
    }
}
