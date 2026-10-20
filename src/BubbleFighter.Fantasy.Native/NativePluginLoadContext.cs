using System.Reflection;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Native;

/// <summary>
/// Isolated load context for one plugin directory. Assemblies shared with the host —
/// the plugin abstractions and domain contracts, logging abstractions, and anything resolvable only outside
/// the plugin directory such as shared-framework assemblies — defer to the default context
/// so service interface types unify across the plugin tree. Everything else resolves from
/// the plugin's own directory through its dependency manifest.
/// </summary>
/// <remarks>
/// Public construction and catalog loading remain noncollectible. A protected constructor
/// permits controlled collectible-context lifetime tests; it does not change deployment
/// policy or establish that module reclamation is complete. Row teardown releases effects,
/// while descriptors and module ownership retain the code lifetime independently.
/// </remarks>
public class NativePluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.Ordinal)
    {
        "BubbleFighter.Fantasy.Abstractions",
        "BubbleFighter.Fantasy.Contracts",
        "Microsoft.Extensions.Logging.Abstractions",
    };

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    /// <summary>Creates a load context for a plugin directory.</summary>
    /// <param name="mainAssemblyPath">The plugin's main assembly path inside its directory.</param>
    /// <param name="name">A display name for the context.</param>
    public NativePluginLoadContext(string mainAssemblyPath, string name)
        : this(Prepare(mainAssemblyPath), name, isCollectible: false)
    {
    }

    /// <summary>Creates an isolated context with explicit collectibility for controlled loader lifetime tests.</summary>
    protected NativePluginLoadContext(string mainAssemblyPath, string name, bool isCollectible)
        : this(Prepare(mainAssemblyPath), name, isCollectible)
    {
    }

    private NativePluginLoadContext(PreparedLoad prepared, string name, bool isCollectible)
        : base(name, isCollectible)
    {
        _resolver = prepared.Resolver;
        _pluginDirectory = prepared.Directory;
    }

    private sealed record PreparedLoad(AssemblyDependencyResolver Resolver, string Directory);

    private static PreparedLoad Prepare(string mainAssemblyPath)
    {
        // A resolver/path failure must happen before the runtime registers an otherwise unreachable context.
        var fullPath = Path.GetFullPath(mainAssemblyPath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullPath)!)
            + Path.DirectorySeparatorChar;
        return new(new AssemblyDependencyResolver(fullPath), directory);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } simple && SharedAssemblies.Contains(simple))
        {
            if (simple is "BubbleFighter.Fantasy.Abstractions" or "BubbleFighter.Fantasy.Contracts")
                return ResolveSharedContract(assemblyName);
            return Default.LoadFromAssemblyName(assemblyName);
        }
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null || !IsInsidePluginDirectory(path))
            return null;
        try
        {
            var assembly = LoadFromAssemblyPath(path);
            ValidateSharedReferences(assembly);
            return assembly;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Plugin context '{Name}' failed to load '{assemblyName.Name}' from '{path}'.",
                ex);
        }
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null && IsInsidePluginDirectory(path)
            ? LoadUnmanagedDllFromPath(path)
            : nint.Zero;
    }

    private bool IsInsidePluginDirectory(string path) => Path.GetFullPath(path).StartsWith(
        _pluginDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Checks host-shared ABI references even when resolution used cached assemblies.</summary>
    public static void ValidateSharedReferences(Assembly assembly)
    {
        // Validate metadata explicitly: a cached assembly can bypass the Load callback.
        foreach (var reference in assembly.GetReferencedAssemblies())
            if (reference.Name is "BubbleFighter.Fantasy.Abstractions" or "BubbleFighter.Fantasy.Contracts")
                ResolveSharedContract(reference);
    }

    /// <summary>Resolves a shared contract only when the requested version is compatible with the host.</summary>
    public static Assembly ResolveSharedContract(AssemblyName reference)
    {
        var shared = Default.LoadFromAssemblyName(new AssemblyName(reference.Name!));
        var available = shared.GetName().Version!;
        var required = reference.Version;
        if (required is null || required.Major != available.Major || required > available)
            throw new ConfigException($"Plugin requires {reference.Name} {required}; host provides {available}. Rebuild the plugin against the deployed contracts.");
        return shared;
    }
}
