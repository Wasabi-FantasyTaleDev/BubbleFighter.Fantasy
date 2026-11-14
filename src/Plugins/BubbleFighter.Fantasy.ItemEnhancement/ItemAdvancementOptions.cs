using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>
/// Defines the laboratory's clothing grade-up tab (MakeItemAdvanced): which cards upgrade which set tier and what each spends.
/// Nothing is assumed when the tab is enabled; the client names no card, price or success chance.
/// </summary>
public sealed class ItemAdvancementOptions
{
    /// <summary>Gets or sets whether the grade-up tab is shown and its requests admitted.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the card the client lists for the tab. The client also compares it with the selected card to choose the
    /// confirmation text for ordinary HC sets, so it should be a card of the first-tier rule. It must be one of the rules' cards.
    /// </summary>
    public int? TabCardItemId { get; set; }

    /// <summary>Gets or sets the grade-up rules; one per upgrade card.</summary>
    public AdvancementRuleOptions[] Rules { get; set; } = [];

    /// <summary>Requires explicit rules and a tab card whenever the grade-up is enabled.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Enabled) yield break;
        if (Rules is null || Rules.Length == 0 || Rules.Any(rule => rule is null || !rule.IsValid()))
        {
            yield return new("Advancement requires explicit rules, each with a card, a source tier of 3, 4 or 5, a card quantity and distinct positive set identities.");
            yield break;
        }
        if (Rules.Select(rule => rule.CardItemId).Distinct().Count() != Rules.Length)
            yield return new("Advancement rules must use distinct cards.");
        if (TabCardItemId is null or <= 0 || !Rules.Any(rule => rule.CardItemId == TabCardItemId))
            yield return new("Advancement requires a tab_card_item_id that is one of its rules' cards.");
    }
}

/// <summary>Configures one upgrade card: the tier it upgrades, its cost and the sets it is limited to.</summary>
public sealed class AdvancementRuleOptions
{
    /// <summary>Gets or sets the counted upgrade card item the client sends as its argument.</summary>
    public int CardItemId { get; set; }

    /// <summary>
    /// Gets or sets the tier of the set the card upgrades, in the client's numbering (kr-1256 sub_10E7EC0): 3 is HC (L3) leading to
    /// advanced HC, 4 is advanced HC (AdvL3) leading to advanced L4HC, 5 is L4HC leading to advanced L4HC.
    /// </summary>
    public int SourceTier { get; set; }

    /// <summary>Gets or sets the cards one grade-up spends.</summary>
    public int? CardQuantity { get; set; }

    /// <summary>Gets or sets the native sets this card is limited to; empty admits every set of the tier.</summary>
    public int[] SetIds { get; set; } = [];

    internal bool IsValid() => CardItemId > 0 && SourceTier is >= 3 and <= 5 && CardQuantity is > 0
        && SetIds is not null && SetIds.All(id => id > 0) && SetIds.Distinct().Count() == SetIds.Length;
}
