using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Enhancement;

/// <summary>Requests the configuration needed to open the client's science laboratory.</summary>
public sealed record ScienceLabConfigurationRequested : IMessage;

/// <summary>Publishes the active enchantment materials for the client's science laboratory.</summary>
/// <param name="EnchantMaterials">The operation-card item ID followed by the four kit item IDs in native display order.</param>
/// <param name="AdditiveItemId">The optional reagent's item ID, used by the client to identify its additive selection.</param>
public sealed record ScienceLabConfigurationPublished(IReadOnlyList<int> EnchantMaterials, int AdditiveItemId) : IMessage
{
    /// <summary>Gets the enabled modification preview policy, or null while modification remains closed.</summary>
    public ModificationConfiguration? Modification { get; init; }

    /// <summary>Gets the enabled permanent-conversion policy, or null while its tab stays hidden.</summary>
    public ScienceLabConversionConfiguration? Conversion { get; init; }

    /// <summary>Gets the enabled grade-up (MakeItemAdvanced) rules, or null while that tab stays hidden.</summary>
    public ScienceLabAdvancementConfiguration? Advancement { get; init; }
}

/// <summary>The permanent-conversion tab's card and minimum remaining time, as the client checks them before sending.</summary>
/// <param name="CardItemId">The counted card item spent by one conversion.</param>
/// <param name="MinimumDays">The whole days a target must still have, compared by the client and again by the server.</param>
public sealed record ScienceLabConversionConfiguration(int CardItemId, int MinimumDays);
