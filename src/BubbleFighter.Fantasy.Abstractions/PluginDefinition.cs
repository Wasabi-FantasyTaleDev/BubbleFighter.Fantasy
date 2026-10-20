using System.Collections.ObjectModel;

namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Captures a backend's declaration and execution factory without requiring a native plugin class or kernel reference.</summary>
/// <remarks>The factory must keep its coordination key and optional module provenance stable for this definition's lifetime.
/// Publishing a new code revision requires a new factory. Host admission validates declaration and contract compatibility.</remarks>
public sealed class PluginDefinition
{
    private readonly PluginAttribute _declaration;

    /// <summary>Copies declaration metadata and contract versions while retaining the backend's stable execution factory.</summary>
    /// <param name="declaration">The declaration whose arrays are copied before the definition is exposed.</param>
    /// <param name="factory">The factory that acquires an owned execution for each activation.</param>
    /// <param name="origin">The backend's diagnostic description of the definition source.</param>
    /// <param name="version">The declared release version; compatibility is checked during admission.</param>
    /// <param name="contractVersions">The referenced shared contract versions, copied with ordinal key comparison.</param>
    public PluginDefinition(PluginAttribute declaration, IPluginExecutionFactory factory, string origin, string version,
        IReadOnlyDictionary<string, string> contractVersions)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(contractVersions);
        _declaration = Snapshot(declaration);
        ExecutionFactory = factory;
        Origin = origin;
        Version = version;
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in contractVersions)
        {
            if (reference.Key is null || reference.Value is null)
                throw new ArgumentException("Contract version names and values must not be null.", nameof(contractVersions));
            versions.Add(reference.Key, reference.Value);
        }
        ContractVersions = new ReadOnlyDictionary<string, string>(versions);
    }

    /// <summary>Gets the declared plugin identity used by profile rows.</summary>
    public string Id => _declaration.Id;

    /// <summary>Gets an independent copy whose arrays cannot mutate the accepted definition.</summary>
    public PluginAttribute Declaration => Snapshot(_declaration);

    /// <summary>Gets the stable backend factory retained by this definition.</summary>
    public IPluginExecutionFactory ExecutionFactory { get; }

    /// <summary>Gets the diagnostic definition origin captured at construction.</summary>
    public string Origin { get; }

    /// <summary>Gets the declared backend release version captured at construction.</summary>
    public string Version { get; }

    /// <summary>Gets the copied, read-only shared contract version mapping.</summary>
    public IReadOnlyDictionary<string, string> ContractVersions { get; }

    private static PluginAttribute Snapshot(PluginAttribute declaration) => new(declaration.Id)
    {
        Description = declaration.Description,
        SupportedBuilds = Copy(declaration, nameof(PluginAttribute.SupportedBuilds), declaration.SupportedBuilds),
        SupportedFamilies = Copy(declaration, nameof(PluginAttribute.SupportedFamilies), declaration.SupportedFamilies),
        Requires = Copy(declaration, nameof(PluginAttribute.Requires), declaration.Requires),
        RequiresMany = Copy(declaration, nameof(PluginAttribute.RequiresMany), declaration.RequiresMany),
        Optional = Copy(declaration, nameof(PluginAttribute.Optional), declaration.Optional),
        OptionalMany = Copy(declaration, nameof(PluginAttribute.OptionalMany), declaration.OptionalMany),
        Provides = Copy(declaration, nameof(PluginAttribute.Provides), declaration.Provides),
        ProvidesMany = Copy(declaration, nameof(PluginAttribute.ProvidesMany), declaration.ProvidesMany),
        ConditionalProvides = Copy(declaration, nameof(PluginAttribute.ConditionalProvides), declaration.ConditionalProvides),
        ConditionalProvidesMany = Copy(declaration, nameof(PluginAttribute.ConditionalProvidesMany), declaration.ConditionalProvidesMany),
        Observes = Copy(declaration, nameof(PluginAttribute.Observes), declaration.Observes),
        Decorates = Copy(declaration, nameof(PluginAttribute.Decorates), declaration.Decorates),
        Overrides = Copy(declaration, nameof(PluginAttribute.Overrides), declaration.Overrides),
        Middleware = Copy(declaration, nameof(PluginAttribute.Middleware), declaration.Middleware),
    };

    private static T[] Copy<T>(PluginAttribute declaration, string field, T[]? values) =>
        values?.ToArray() ?? throw new ConfigException($"Plugin '{declaration.Id}' declaration field '{field}' must not be null.");
}
