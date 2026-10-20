namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Describes one backend code revision and the references required to keep it available while the runtime owns it.</summary>
/// <param name="ModuleId">The nonempty diagnostic module name, independent of lifecycle coordination keys.</param>
/// <param name="Identity">A stable opaque reference identifying this actual revision; host comparison uses reference identity.</param>
/// <param name="Attribution">The nonempty backend-defined description of how this revision is attributed.</param>
/// <param name="LifetimeAnchor">An optional additional reference that must stay alive while code holders remain.</param>
/// <param name="Lifetime">The backend's declared lifetime capability, not evidence that reclamation has occurred.</param>
/// <remarks>This is an in-process backend contract, not a wire descriptor or a reclamation command.</remarks>
public sealed record PluginModuleCode(string ModuleId, object Identity, string Attribution, object? LifetimeAnchor = null,
    PluginCodeLifetime Lifetime = PluginCodeLifetime.Unknown)
{
    /// <summary>Gets the stable module name used together with the opaque revision reference.</summary>
    public string ModuleId { get; init; } = ModuleId;

    /// <summary>Gets the revision reference; its user-defined equality and hash-code methods are not a host identity contract.</summary>
    public object Identity { get; init; } = Identity;

    /// <summary>Gets the backend's provenance label without requiring the host to recognize a language or runtime.</summary>
    public string Attribution { get; init; } = Attribution;

    /// <summary>Gets an optional extra code root retained alongside this revision until its runtime holders drain.</summary>
    public object? LifetimeAnchor { get; init; } = LifetimeAnchor;

    /// <summary>Gets the declared code lifetime capability; collectible capability never proves completed collection.</summary>
    public PluginCodeLifetime Lifetime { get; init; } = Lifetime;
}

/// <summary>Optionally supplies backend-neutral code provenance from an execution factory.</summary>
/// <remarks>Provenance must remain stable for a factory instance. A different revision requires a newly bound factory definition.
/// Factories without this interface remain explicitly unattributed; provenance does not transfer resource cleanup ownership.</remarks>
public interface IPluginModuleSource
{
    /// <summary>Gets the factory's stable provenance snapshot, captured when constructing a definition and checked again after acquisition.</summary>
    PluginModuleCode ModuleCode { get; }
}
