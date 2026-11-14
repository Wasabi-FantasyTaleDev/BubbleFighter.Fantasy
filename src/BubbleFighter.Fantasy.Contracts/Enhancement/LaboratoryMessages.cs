using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Contracts.Enhancement;

/// <summary>Identifies the native legacy science-room operation selected by its card.</summary>
public enum LaboratoryOperation
{
    /// <summary>Combines three selections into one selection with a new duration.</summary>
    Synthesis = 1,
    /// <summary>Consumes one selection and creates the configured decomposition reward.</summary>
    Decomposition = 2,
}

/// <summary>Describes the native result explanation independently of its wire subcode.</summary>
public enum LaboratoryOutcome
{
    /// <summary>The ordinary configured recipe completed.</summary>
    Ordinary,
    /// <summary>The combined duration produced permanent ownership.</summary>
    BecamePermanent,
    /// <summary>The combined duration reached its configured finite maximum.</summary>
    DurationCapped,
    /// <summary>The selected additive produced permanent ownership.</summary>
    AdditivePermanent,
    /// <summary>The permanent additive failed and supplied its configured bonus instead.</summary>
    AdditiveBonus,
}

/// <summary>Carries an audited science-room selection after dialect-only fields have been validated.</summary>
/// <param name="CardItemId">The native operation card selected by the UI.</param>
/// <param name="Operation">The operation assigned to that card.</param>
/// <param name="Category">The native material category: accessories, clothing, or weapons.</param>
/// <param name="Grouped">Whether material references contain negative stock-set markers.</param>
/// <param name="Materials">Native material references, including each selected group's ordered members.</param>
/// <param name="Additives">The two native additive slots, with minus one denoting an empty slot.</param>
public sealed record LaboratoryRequested(int CardItemId, LaboratoryOperation Operation, int Category,
    bool Grouped, IReadOnlyList<int> Materials, IReadOnlyList<int> Additives) : IMessage
{
    /// <summary>Gets an immutable copy of the submitted material references.</summary>
    public IReadOnlyList<int> Materials { get; } = Array.AsReadOnly(Materials.ToArray());

    /// <summary>Gets an immutable copy of the submitted additive slots.</summary>
    public IReadOnlyList<int> Additives { get; } = Array.AsReadOnly(Additives.ToArray());
}

/// <summary>Reports a rejected recipe without changing any inventory ownership.</summary>
/// <param name="Operation">The operation whose native failure presentation must be selected.</param>
public sealed record LaboratoryRefused(LaboratoryOperation Operation) : IMessage;

/// <summary>Publishes result references only after their complete inventory has been installed.</summary>
/// <param name="Operation">The completed science-room operation.</param>
/// <param name="PrimaryReference">The synthesis itemId/stockSetId or decomposition Stock.stockId.</param>
/// <param name="Lifetime">The primary result's durable lifetime; synthesis projects it to the connection baseline.</param>
/// <param name="Outcome">The native result explanation selected by the configured recipe.</param>
/// <param name="SecondReference">An optional secondary reward's Stock.stockId, or zero.</param>
/// <param name="ThirdReference">An optional tertiary reward's Stock.stockId, or zero.</param>
public sealed record LaboratoryCompleted(LaboratoryOperation Operation, int PrimaryReference, InventoryLifetime Lifetime,
    LaboratoryOutcome Outcome = LaboratoryOutcome.Ordinary, int SecondReference = 0, int ThirdReference = 0) : IMessage;
