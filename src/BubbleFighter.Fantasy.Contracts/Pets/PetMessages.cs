using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Pets;

/// <summary>Requests the next pet grade after its current experience threshold is reached.</summary>
/// <param name="Target">The ordinary item id or the complete article identity selected by the client.</param>
public sealed record PetLevelUpRequested(ulong Target) : IMessage;

/// <summary>Reports the result of a dedicated pet level-up request after publishing its changed state.</summary>
/// <param name="Target">The requested ordinary item id or article identity.</param>
/// <param name="Result">The native result code; zero is success.</param>
public sealed record PetLevelUpReplied(ulong Target, int Result) : IMessage;

/// <summary>Requests replacement of an acquired pet skill using a skill-selection consumable.</summary>
/// <param name="Target">The article identity of the selected pet.</param>
/// <param name="OldSelector">The acquired ItemAttr definition selected for replacement.</param>
/// <param name="NewSelector">The requested replacement ItemAttr definition.</param>
/// <param name="ConsumableItemId">The inventory item used to perform the replacement.</param>
public sealed record PetAbilityChangeRequested(ulong Target, int OldSelector, int NewSelector, int ConsumableItemId) : IMessage;

/// <summary>Reports a skill replacement after the pet's authoritative attributes have been installed.</summary>
/// <param name="Target">The pet article identity.</param>
/// <param name="Result">The native result code; zero opens the success presentation.</param>
/// <param name="Selector">The replacement ItemAttr definition displayed by the success consumer.</param>
public sealed record PetAbilityChangeReplied(ulong Target, int Result, int Selector) : IMessage
{
    /// <summary>Gets the word at wire offset 12, whose purpose is not established by the native consumer.</summary>
    public int Unknown12 { get; init; }

    /// <summary>Gets the word at wire offset 20, whose purpose is not established by the native consumer.</summary>
    public int Unknown20 { get; init; }
}

/// <summary>Opens the native pet growth or regression presentation using previously installed inventory state.</summary>
/// <param name="Outcome">The recipient's native outcome; one denotes growth and zero denotes regression.</param>
/// <param name="Target">The pet item id or article identity accepted by the recipient's layout.</param>
public sealed record PetEvolved(int Outcome, ulong Target) : IMessage
{
    /// <summary>Gets the optional modern word whose purpose is not established by the native consumer.</summary>
    public int? UnknownOptional { get; init; }

    /// <summary>Gets the reward item ids displayed by the modern regression consumer.</summary>
    public IReadOnlyList<int> Rewards { get; init; } = [];

    /// <summary>Gets the fixed trailing word of the legacy and Taiwan replies, unused by the audited consumers.</summary>
    public int UnknownLast { get; init; }
}
