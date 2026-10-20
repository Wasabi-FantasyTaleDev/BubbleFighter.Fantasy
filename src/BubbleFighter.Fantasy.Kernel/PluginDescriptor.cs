using System.Collections.ObjectModel;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Describes a plugin's static declaration and activation factory independently of its backend.</summary>
/// <param name="Id">The stable configured plugin identity.</param>
/// <param name="Description">The optional diagnostic description.</param>
/// <param name="Type">The optional native implementing type; backend definitions need not have one.</param>
/// <param name="Origin">The diagnostic definition source.</param>
/// <param name="MainAssemblyPath">The native assembly path when one exists.</param>
public sealed record PluginDescriptor(string Id, string? Description, Type? Type, string Origin, string? MainAssemblyPath)
{
    private readonly PluginAttribute? _declaration = Type is null ? null : Snapshot(Type.GetCustomAttribute<PluginAttribute>()
        ?? throw new ConfigException($"Plugin type '{Type}' has no declaration."));
    private readonly string? _version = Type?.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Type?.Assembly.GetName().Version?.ToString(3);
    private readonly IReadOnlyDictionary<string, string> _contractVersions = new ReadOnlyDictionary<string, string>(Type?.Assembly.GetReferencedAssemblies()
        .Where(reference => reference.Name is "BubbleFighter.Fantasy.Abstractions" or "BubbleFighter.Fantasy.Contracts")
        .ToDictionary(reference => reference.Name!, reference => reference.Version!.ToString(), StringComparer.Ordinal) ?? new(StringComparer.Ordinal));
    private readonly IPluginExecutionFactory? _factory = Type is null ? null : new NativePluginExecutionFactory(Type, Origin, MainAssemblyPath);
    private readonly PluginModuleCode? _moduleCode = Type is null ? null : NativePluginExecutionFactory.ModuleCodeFor(Type, MainAssemblyPath);
    internal PluginModuleCode? ModuleCode => _moduleCode;

    /// <summary>Adapts a backend-neutral definition through the same declaration and factory snapshot path as native metadata.</summary>
    /// <param name="definition">The immutable public definition to capture before host admission.</param>
    public PluginDescriptor(PluginDefinition definition)
        : this((definition ?? throw new ArgumentNullException(nameof(definition))).Declaration, definition.ExecutionFactory,
            definition.Origin, definition.Version, definition.ContractVersions)
    {
    }

    /// <summary>Creates a definition whose execution is supplied by a backend without requiring a CLR plugin class.</summary>
    /// <param name="declaration">The service and dependency declaration, copied on construction.</param>
    /// <param name="factory">The factory that acquires an owned execution.</param>
    /// <param name="origin">The diagnostic definition source.</param>
    /// <param name="version">The backend's declared release version.</param>
    /// <param name="contractVersions">The referenced shared contract versions, copied on construction.</param>
    public PluginDescriptor(PluginAttribute declaration, IPluginExecutionFactory factory, string origin, string version,
        IReadOnlyDictionary<string, string> contractVersions) : this(declaration.Id, declaration.Description, null, origin, null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _declaration = Snapshot(declaration); _factory = factory; _version = version;
        _moduleCode = factory is IPluginModuleSource source
            ? source.ModuleCode ?? throw new ConfigException($"Plugin '{Id}' declared a module source but returned no provenance.")
            : null;
        _contractVersions = new ReadOnlyDictionary<string, string>(contractVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        CoordinationKey = factory.CoordinationKey;
        if (CoordinationKey is not null && string.IsNullOrWhiteSpace(CoordinationKey))
            throw new ConfigException("An execution coordination key must be nonempty or omitted.");
    }

    /// <summary>Gets a defensive copy of the definition's service and dependency declaration.</summary>
    public PluginAttribute Declaration => Snapshot(_declaration ?? throw new ConfigException($"Plugin '{Id}' has no declaration."));
    /// <summary>Gets the factory responsible for creating and refreshing backend executions.</summary>
    public IPluginExecutionFactory ExecutionFactory => _factory ?? throw new ConfigException($"Plugin '{Id}' has no execution factory.");
    /// <summary>Gets the captured backend key used to serialize overlapping module operations.</summary>
    public string? CoordinationKey { get; } = NativePluginExecutionFactory.GetCoordinationKey(MainAssemblyPath);
    /// <summary>Gets the resolved release version without requiring native assembly reflection.</summary>
    public string Version => _version ?? throw new ConfigException($"Plugin '{Id}' has no release version.");
    /// <summary>Gets the immutable shared contract version references.</summary>
    public IReadOnlyDictionary<string, string> ContractVersions => _contractVersions;
    /// <summary>Gets the declared executable build intervals.</summary>
    public IReadOnlyList<string> SupportedBuilds => Array.AsReadOnly(Declaration.SupportedBuilds);

    internal PluginDescriptor ResolveExecution(IPluginExecution execution)
    {
        var resolved = Snapshot(execution.Declaration);
        var original = Declaration;
        if (resolved.Id != Id || !original.Overrides.ToHashSet(StringComparer.Ordinal).SetEquals(resolved.Overrides) ||
            typeof(PluginAttribute).GetProperties().Where(property => property.PropertyType == typeof(Type[]))
                .Any(property => !((Type[])property.GetValue(original)!).ToHashSet().SetEquals((Type[])property.GetValue(resolved)!)))
            throw new ConfigException($"Plugin '{Id}' changed its declared contracts; definition reconciliation is required.");
        var implementationType = execution.ImplementationType;
        if (!StringComparer.Ordinal.Equals(CoordinationKey, ExecutionFactory.CoordinationKey))
            throw new ConfigException($"Plugin '{Id}' changed its execution coordination key.");
        var factory = ExecutionFactory;
        if (factory is IPluginExecutionBindingFactory)
        {
            if (execution is not IPluginExecutionBinding binding)
                throw new ConfigException($"Plugin '{Id}' opted into execution binding but returned no binding.");
            factory = binding.BoundFactory ?? throw new ConfigException($"Plugin '{Id}' returned no bound execution factory.");
        }
        var descriptor = new PluginDescriptor(resolved, factory, Origin, execution.Version, execution.ContractVersions)
        { Type = implementationType, MainAssemblyPath = MainAssemblyPath };
        if (ReferenceEquals(factory, ExecutionFactory) && !SameFactoryCode(ModuleCode, descriptor.ModuleCode))
            throw new ConfigException($"Plugin '{Id}' changed its factory code provenance; publish a new definition with an immutable factory.");
        if (!StringComparer.Ordinal.Equals(CoordinationKey, descriptor.CoordinationKey))
            throw new ConfigException($"Plugin '{Id}' changed its execution coordination key.");
        descriptor.ValidateContractVersions();
        return descriptor;
    }

    private static bool SameFactoryCode(PluginModuleCode? accepted, PluginModuleCode? acquired) =>
        ReferenceEquals(accepted, acquired) || accepted is not null && acquired is not null &&
        StringComparer.Ordinal.Equals(accepted.ModuleId, acquired.ModuleId) &&
        ReferenceEquals(accepted.Identity, acquired.Identity) &&
        StringComparer.Ordinal.Equals(accepted.Attribution, acquired.Attribution) &&
        ReferenceEquals(accepted.LifetimeAnchor, acquired.LifetimeAnchor) && accepted.Lifetime == acquired.Lifetime;

    internal void ValidateContractVersions()
    {
        foreach (var reference in ContractVersions)
            if (reference.Key is "BubbleFighter.Fantasy.Abstractions" or "BubbleFighter.Fantasy.Contracts")
            {
                if (!System.Version.TryParse(reference.Value, out var version))
                    throw new ConfigException($"Plugin '{Id}' declares an invalid shared contract version '{reference.Value}'.");
                PluginLoadContext.ResolveSharedContract(new AssemblyName(reference.Key) { Version = version });
            }
    }

    private static PluginAttribute Snapshot(PluginAttribute value) => new(value.Id)
    {
        Description = value.Description, SupportedBuilds = value.SupportedBuilds.ToArray(),
        SupportedFamilies = value.SupportedFamilies.ToArray(),
        Requires = CopyContracts(value.Id, nameof(PluginAttribute.Requires), value.Requires),
        RequiresMany = CopyContracts(value.Id, nameof(PluginAttribute.RequiresMany), value.RequiresMany),
        Optional = CopyContracts(value.Id, nameof(PluginAttribute.Optional), value.Optional),
        OptionalMany = CopyContracts(value.Id, nameof(PluginAttribute.OptionalMany), value.OptionalMany),
        Provides = CopyContracts(value.Id, nameof(PluginAttribute.Provides), value.Provides),
        ProvidesMany = CopyContracts(value.Id, nameof(PluginAttribute.ProvidesMany), value.ProvidesMany),
        ConditionalProvides = CopyContracts(value.Id, nameof(PluginAttribute.ConditionalProvides), value.ConditionalProvides),
        ConditionalProvidesMany = CopyContracts(value.Id, nameof(PluginAttribute.ConditionalProvidesMany), value.ConditionalProvidesMany),
        Observes = CopyContracts(value.Id, nameof(PluginAttribute.Observes), value.Observes),
        Decorates = CopyContracts(value.Id, nameof(PluginAttribute.Decorates), value.Decorates), Overrides = value.Overrides.ToArray(),
    };

    private static Type[] CopyContracts(string pluginId, string field, Type[]? values)
    {
        if (values is null) throw new ConfigException($"Plugin '{pluginId}' declaration field '{field}' must not be null.");
        var copy = values.ToArray();
        var seen = new HashSet<Type>();
        for (var index = 0; index < copy.Length; index++)
        {
            try { ServiceRuntime.RequireContract(copy[index]); }
            catch (ArgumentException error)
            { throw new ConfigException($"Plugin '{pluginId}' declaration field '{field}[{index}]': {error.Message}"); }
            if (!seen.Add(copy[index]))
                throw new ConfigException($"Plugin '{pluginId}' declaration field '{field}[{index}]' duplicates '{copy[index].FullName}' in the same field.");
        }
        return copy;
    }
}
