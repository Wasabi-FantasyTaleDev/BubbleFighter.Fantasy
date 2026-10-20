using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Per-entry code-refresh policy and limits for configuration capture, owned resources and teardown observation.</summary>
public sealed record PluginLifecycleOptions
{
    /// <summary>Gets whether explicit executable-code refresh is permitted without restarting the process.</summary>
    [EnumDataType(typeof(PluginCodeRefreshPolicy))]
    public PluginCodeRefreshPolicy CodeRefreshPolicy { get; init; } = PluginCodeRefreshPolicy.Allow;

    /// <summary>Gets the maximum acquiring, live, releasing or failed effects retained by an
    /// activation and all its child resource scopes together. Early release returns a slot only
    /// after cleanup settles; creating a child scope does not grant a separate capacity.</summary>
    [Range(1, 100000)]
    public int EffectCapacity { get; init; } = 4096;

    /// <summary>Gets the additional entry slots this row grants at host startup. The shared
    /// capacity stays fixed when the row is removed, reconfigured or added again.</summary>
    [Range(0, 1000000)]
    public int EntryReserve { get; init; } = 0;

    /// <summary>Gets the additional service metadata slots this row grants at host startup.
    /// Retiring ownership continues to consume the fixed shared capacity until it drains.</summary>
    [Range(0, 10000000)]
    public int ServiceMetadataReserve { get; init; } = 0;

    /// <summary>Gets the additional prepared, published or retiring definition sources allowed by this row's startup grant.</summary>
    [Range(0, 1000000)]
    public int DefinitionSourceReserve { get; init; } = 0;

    /// <summary>Gets the additional prepared, published or retiring plugin definitions allowed by this row's startup grant.</summary>
    [Range(0, 1000000)]
    public int DefinitionReserve { get; init; } = 0;

    /// <summary>Gets the milliseconds to observe teardown before reporting incomplete cleanup.
    /// Expiration never kills a callback or releases dependencies still owned by the activation.</summary>
    [Range(1, 600000)]
    public int TeardownTimeoutMs { get; init; } = 10000;

    /// <summary>Gets the maximum automatic activation attempts for one row before a changing
    /// dependency batch is failed. A new explicit target may retry the failed row.</summary>
    [Range(1, 10000)]
    public int ReconcileAttemptLimit { get; init; } = 32;

    /// <summary>Gets the maximum depth of configuration and lineage values in an admitted
    /// snapshot, counting each root collection as depth one.</summary>
    [Range(1, 256)]
    public int ConfigurationDepthLimit { get; init; } = 32;

    /// <summary>Gets the maximum configuration and lineage values copied into a snapshot,
    /// including collection nodes and each occurrence of a shared value.</summary>
    [Range(1, 1000000)]
    public int ConfigurationNodeLimit { get; init; } = 65536;

    /// <summary>Gets the maximum number of entries in any configuration mapping, sequence,
    /// or lineage collection admitted into a snapshot.</summary>
    [Range(1, 1000000)]
    public int ConfigurationCollectionLimit { get; init; } = 16384;

    /// <summary>Gets the maximum total UTF-16 characters in configuration keys and text
    /// values, lineage entries, and row metadata retained by a snapshot.</summary>
    [Range(1, 67108864)]
    public int ConfigurationTextLimit { get; init; } = 1048576;
}
