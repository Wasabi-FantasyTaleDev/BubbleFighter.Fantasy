using System.Reflection;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Native;

/// <summary>Acquires native plugin activations while preserving the immutable loaded code revision.</summary>
public sealed class NativePluginExecutionFactory : IPluginExecutionBindingFactory, IPluginModuleSource
{
    private readonly string _origin;
    private readonly Func<string, string, PluginDefinition>? _loadDefinition;

    /// <summary>Captures native code and an optional compatibility loader for later refresh operations.</summary>
    public NativePluginExecutionFactory(Type implementationType, string origin, string? mainAssemblyPath,
        Func<string, string, PluginDefinition>? loadDefinition = null)
    {
        ArgumentNullException.ThrowIfNull(implementationType);
        ArgumentNullException.ThrowIfNull(origin);
        ImplementationType = implementationType;
        MainAssemblyPath = mainAssemblyPath;
        _origin = origin;
        _loadDefinition = loadDefinition;
        CoordinationKey = GetCoordinationKey(mainAssemblyPath);
        ModuleCode = ModuleCodeFor(implementationType, mainAssemblyPath);
    }

    /// <summary>Gets the resolved native implementation retained by this immutable factory.</summary>
    public Type ImplementationType { get; }
    /// <summary>Gets the module path used for explicit code refresh, or null for borrowed host code.</summary>
    public string? MainAssemblyPath { get; }
    /// <inheritdoc />
    public string? CoordinationKey { get; }
    /// <inheritdoc />
    public PluginModuleCode ModuleCode { get; }

    /// <summary>Identifies a module operation independently of its currently loaded code revision.</summary>
    public static string? GetCoordinationKey(string? path)
    {
        if (path is null) return null;
        var full = Path.GetFullPath(path);
        return "native:" + (OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full);
    }

    /// <summary>Captures actual assembly identity and its independent code lifetime anchor.</summary>
    public static PluginModuleCode ModuleCodeFor(Type type, string? path) => new(
        GetCoordinationKey(path) ?? "builtin:" + (type.Assembly.FullName
            ?? throw new ConfigException("A native assembly must have a stable source name.")),
        type.Assembly, "native-assembly", AssemblyLoadContext.GetLoadContext(type.Assembly),
        type.Assembly.IsCollectible ? PluginCodeLifetime.Collectible : PluginCodeLifetime.ProcessLifetime);

    /// <summary>Builds a public definition directly from native attributes without a kernel descriptor.</summary>
    public static PluginDefinition CreateDefinition(Type type, string origin, string? mainAssemblyPath) =>
        CreateDefinition(type, origin, mainAssemblyPath, null);

    private static PluginDefinition CreateDefinition(Type type, string origin, string? mainAssemblyPath,
        Func<string, string, PluginDefinition>? loadDefinition)
    {
        var declaration = type.GetCustomAttribute<PluginAttribute>()
            ?? throw new ConfigException($"Plugin type '{type}' has no declaration.");
        var version = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? type.Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var contracts = type.Assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name is "BubbleFighter.Fantasy.Abstractions" or "BubbleFighter.Fantasy.Contracts")
            .ToDictionary(reference => reference.Name!, reference => reference.Version!.ToString(), StringComparer.Ordinal);
        return new(declaration, new NativePluginExecutionFactory(type, origin, mainAssemblyPath, loadDefinition), origin, version, contracts);
    }

    /// <inheritdoc />
    public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var declaration = ImplementationType.GetCustomAttribute<PluginAttribute>()
            ?? throw new ConfigException($"Plugin type '{ImplementationType}' has no declaration.");
        var resolved = reload && MainAssemblyPath is not null
            ? _loadDefinition is { } load
                ? load(MainAssemblyPath, Path.GetDirectoryName(MainAssemblyPath)!)
                : NativePluginModules.LoadDefinition(MainAssemblyPath, Path.GetDirectoryName(MainAssemblyPath)!, declaration.Id)
            : CreateDefinition(ImplementationType, _origin, MainAssemblyPath, _loadDefinition);
        var native = resolved.ExecutionFactory as NativePluginExecutionFactory
            ?? throw new ConfigException("A native refresh must return a native execution definition.");
        // A resolved revision retains the configured compatibility loader for subsequent refreshes.
        var bound = new NativePluginExecutionFactory(native.ImplementationType, _origin, MainAssemblyPath, _loadDefinition);
        return ValueTask.FromResult<IPluginExecution>(new Execution(resolved, bound));
    }

    private sealed class Execution(PluginDefinition definition, NativePluginExecutionFactory factory) : IPluginExecution, IPluginExecutionBinding
    {
        private IPlugin? _instance;
        private int _activated;
        private int _disposed;
        public PluginAttribute Declaration => definition.Declaration;
        public string Version => definition.Version;
        public IReadOnlyDictionary<string, string> ContractVersions => definition.ContractVersions;
        public Type? ImplementationType => factory.ImplementationType;
        public IPluginExecutionFactory BoundFactory => factory;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.Exchange(ref _activated, 1) != 0) throw new InvalidOperationException("An execution can activate only once.");
            _instance = (IPlugin)Activator.CreateInstance(factory.ImplementationType)!;
            return _instance.ActivateAsync(context, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _instance = null;
            // Code is held by definition and execution ownership; activation teardown cannot unload siblings.
            return ValueTask.CompletedTask;
        }
    }
}
