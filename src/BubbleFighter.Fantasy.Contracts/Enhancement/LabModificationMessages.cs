using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Enhancement;

/// <summary>Presents a committed modification only after its changed Article content is installed in the client.</summary>
/// <param name="Deleted">Selects the delete completion rather than the apply completion.</param>
/// <param name="Target">Native key of the changed Article.</param>
/// <param name="AttributeId">Installed or removed selector used by the native result and audit path.</param>
/// <param name="Group">Installed or removed modification group, numbered zero through two.</param>
public sealed record LabModificationCompleted(bool Deleted, ulong Target, int AttributeId, int Group) : IMessage;

/// <summary>Supplies one native part category and the seven attribute outcomes displayed by modification preview.</summary>
/// <param name="ItemId">Native count-based part item selected in the laboratory.</param>
/// <param name="NumericTag">First random-table category and content numeric tag.</param>
/// <param name="EffectCategory">Second random-table category.</param>
/// <param name="AttributeIds">Exactly seven native attribute selectors in preview order.</param>
/// <param name="Value0">Positive signed effect units, displayed as Value0 divided by 100 percent.</param>
/// <param name="NegativeValues">Negative effect units keyed by the corresponding attribute selector.</param>
public sealed record ModificationPartConfiguration(int ItemId, int NumericTag, int EffectCategory,
    IReadOnlyList<int> AttributeIds, int Value0, IReadOnlyDictionary<int, int> NegativeValues);

/// <summary>Supplies the validated modification preview and operation configuration shared by client and server.</summary>
/// <param name="Parts">Bounded native part configurations.</param>
/// <param name="TagWeights">First-category weights indexed by existing installed count zero through two.</param>
/// <param name="EffectWeights">Second-category weights indexed by existing installed count zero through two.</param>
public sealed record ModificationConfiguration(IReadOnlyList<ModificationPartConfiguration> Parts,
    IReadOnlyDictionary<int, IReadOnlyList<int>> TagWeights, IReadOnlyDictionary<int, IReadOnlyList<int>> EffectWeights);
