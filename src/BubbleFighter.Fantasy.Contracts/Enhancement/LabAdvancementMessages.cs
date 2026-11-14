using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Enhancement;

/// <summary>
/// A committed laboratory grade-up of one native set (the MakeItemAdvanced tab), sent after the complete inventory that
/// installs it. The native reader resolves the set identity and one member's Article key against that inventory.
/// </summary>
/// <param name="SetId">The native set identity the client requested.</param>
/// <param name="Member">The Article key of one member of the owned set; the client checks membership in that set.</param>
public sealed record LabAdvancementCompleted(int SetId, ulong Member) : IMessage
{
    /// <summary>Gets the ordered references the shared laboratory key-list writer sends.</summary>
    public IReadOnlyList<ulong> Keys => [(ulong)SetId, Member];
}

/// <summary>
/// One grade-up rule as the client's rule engine reads it (kr-1256 sub_114B7F0): a card, the effects that exclude or
/// are required for it, the sets it is limited to, and the tier marker it leads to.
/// </summary>
/// <param name="CardItemId">The upgrade card item the client matches against its selected card.</param>
/// <param name="BlockingEffects">ItemAttr selectors whose presence on the set excludes this rule.</param>
/// <param name="RequiredEffects">ItemAttr selectors that must all be present on the set for this rule.</param>
/// <param name="SetIds">Native sets the rule is limited to; empty admits every set of its group.</param>
/// <param name="TargetMarker">The tier the set reaches, as the client's confirmation text selects it: 4 or 6.</param>
public sealed record ScienceLabAdvancementRule(int CardItemId, IReadOnlyList<int> BlockingEffects,
    IReadOnlyList<int> RequiredEffects, IReadOnlyList<int> SetIds, byte TargetMarker);

/// <summary>The rules the client applies to sets whose maximum member isAdvancedable value equals the key.</summary>
/// <param name="Key">The set class: 1 for L3, 2 for AdvL3, 3 for L4 clothing sets.</param>
/// <param name="Rules">The rules of that class in publication order.</param>
public sealed record ScienceLabAdvancementGroup(int Key, IReadOnlyList<ScienceLabAdvancementRule> Rules);

/// <summary>The grade-up tab's operation card and rule groups.</summary>
/// <param name="TabCardItemId">The card kr-1256 sub_DFA000 installs for tab 6, which the confirmation text also compares.</param>
/// <param name="Groups">Every rule group, with distinct keys.</param>
public sealed record ScienceLabAdvancementConfiguration(int TabCardItemId, IReadOnlyList<ScienceLabAdvancementGroup> Groups);
