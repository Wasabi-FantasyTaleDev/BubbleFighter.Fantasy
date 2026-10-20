using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// Indexes static plugin definitions and discovers native classes marked with
/// <see cref="PluginAttribute"/> that implement <see cref="IPlugin"/>. Native sources are assemblies already loaded in the host plus one
/// subdirectory per native module under each configured plugin root.
/// </summary>
public sealed class PluginCatalog
{
    private readonly IReadOnlyList<PluginDescriptor> _plugins;

    private PluginCatalog(List<PluginDescriptor> plugins) => _plugins = plugins.AsReadOnly();

    /// <summary>Gets every discovered plugin.</summary>
    public IReadOnlyList<PluginDescriptor> Plugins => _plugins;

    /// <summary>Finds a plugin by id.</summary>
    /// <param name="id">The plugin id.</param>
    /// <returns>The descriptor, or <see langword="null"/>.</returns>
    public PluginDescriptor? Find(string id) => _plugins.FirstOrDefault(p => p.Id == id);

    /// <summary>Builds a catalog from already-resolved static definitions, including non-native execution backends.</summary>
    /// <param name="definitions">The definitions to copy and validate before host composition.</param>
    /// <returns>A catalog with unique stable plugin identities.</returns>
    public static PluginCatalog FromDefinitions(IEnumerable<PluginDescriptor> definitions)
    {
        var plugins = definitions.ToList();
        foreach (var plugin in plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Id) || plugin.Id != plugin.Declaration.Id)
                throw new ConfigException("A plugin definition must match its nonempty declaration identity.");
            _ = plugin.ExecutionFactory;
            plugin.ValidateContractVersions();
        }
        var duplicate = plugins.GroupBy(plugin => plugin.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ConfigException($"Duplicate plugin id '{duplicate.Key}'.");
        return new PluginCatalog(plugins);
    }

    /// <summary>
    /// Scans already-loaded assemblies plus each plugin root directory. Every subdirectory
    /// of a root is one module; its main assembly is <c>&lt;dir&gt;/&lt;dirname&gt;.dll</c>
    /// loaded in an isolated <see cref="PluginLoadContext"/>. All definitions in that module
    /// share the same loaded assembly while retaining distinct plugin identities.
    /// </summary>
    /// <param name="pluginRoots">Absolute plugin root directories.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ConfigException">Thrown on duplicate plugin ids or malformed plugin directories.</exception>
    public static PluginCatalog Discover(IEnumerable<string> pluginRoots) =>
        Discover(pluginRoots, static (path, name) => new PluginLoadContext(path, name));

    internal static PluginCatalog Discover(IEnumerable<string> pluginRoots,
        Func<string, string, PluginLoadContext> createContext, IEnumerable<Assembly>? borrowedAssemblies = null) =>
        NativeModuleLoadBatch.Execute("discover", createContext, batch =>
    {
        var plugins = new List<PluginDescriptor>();
        batch.FailurePhase = "borrowed-assemblies";
        foreach (var assembly in borrowedAssemblies ?? System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies)
            ScanAssembly(assembly, origin: assembly.GetName().Name ?? "host", mainAssemblyPath: null, plugins);

        batch.FailurePhase = "module-discovery";
        foreach (var root in pluginRoots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var main = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
                if (!File.Exists(main))
                    continue;
                plugins.AddRange(batch.Load(main, dir));
                batch.FailurePhase = "module-discovery";
            }
        }

        batch.FailurePhase = "catalog-validation";
        var duplicates = plugins.GroupBy(p => p.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ConfigException($"Duplicate plugin id(s): {string.Join(", ", duplicates)}.");
        return FromDefinitions(plugins);
    });

    /// <summary>Reads immutable load-failure facts from this exception or its inner cause without retaining module objects.</summary>
    /// <param name="exception">The original load failure or a lifecycle wrapper around it.</param>
    /// <returns>A JSON snapshot of loading stages and cleanup outcomes, or null when no loading attempt supplied one.</returns>
    public static string? GetLoadFailureDiagnostics(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (NativeModuleLoadBatch.GetFailureDiagnostics(current) is { } diagnostics) return diagnostics;
        return null;
    }

    /// <summary>
    /// Scans specific assemblies only; used by tests to build a catalog without touching
    /// the file system.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ConfigException">Thrown on duplicate plugin ids.</exception>
    public static PluginCatalog ScanAssemblies(IEnumerable<Assembly> assemblies)
    {
        var plugins = new List<PluginDescriptor>();
        foreach (var assembly in assemblies)
            ScanAssembly(assembly, origin: assembly.GetName().Name ?? "unknown", mainAssemblyPath: null, plugins);
        var duplicates = plugins.GroupBy(p => p.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ConfigException($"Duplicate plugin id(s): {string.Join(", ", duplicates)}.");
        return new PluginCatalog(plugins);
    }

    /// <summary>
    /// Loads one module into a fresh isolated context and returns all of its validated definitions.
    /// </summary>
    /// <param name="mainAssemblyPath">The plugin's main assembly path.</param>
    /// <param name="pluginDirectory">The plugin directory.</param>
    /// <returns>A read-only list of uniquely identified definitions sharing one loaded assembly.</returns>
    /// <exception cref="ConfigException">The module has no definitions, duplicate or empty identities, or incompatible shared contracts.</exception>
    /// <remarks>Production loading remains process-lifetime. Rejected discovery does not establish reclamation of a partially loaded context.</remarks>
    public static IReadOnlyList<PluginDescriptor> LoadModule(string mainAssemblyPath, string pluginDirectory) =>
        LoadModule(mainAssemblyPath, pluginDirectory, static (path, name) => new PluginLoadContext(path, name));

    internal static IReadOnlyList<PluginDescriptor> LoadModule(string mainAssemblyPath, string pluginDirectory,
        Func<string, string, PluginLoadContext> createContext) =>
        NativeModuleLoadBatch.Execute("load-module", createContext, batch => batch.Load(mainAssemblyPath, pluginDirectory));

    /// <summary>Loads a module and selects exactly the requested definition without relying on type order.</summary>
    /// <param name="mainAssemblyPath">The module's main assembly path.</param>
    /// <param name="pluginDirectory">The module directory.</param>
    /// <param name="pluginId">The nonempty, ordinal plugin identity to select.</param>
    /// <returns>The requested definition from the newly loaded module revision.</returns>
    /// <exception cref="ConfigException">The selector is empty, the module is invalid, or the requested identity does not exist.</exception>
    public static PluginDescriptor LoadDefinition(string mainAssemblyPath, string pluginDirectory, string pluginId) =>
        LoadDefinition(mainAssemblyPath, pluginDirectory, pluginId, static (path, name) => new PluginLoadContext(path, name));

    internal static PluginDescriptor LoadDefinition(string mainAssemblyPath, string pluginDirectory, string pluginId,
        Func<string, string, PluginLoadContext> createContext)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            throw new ConfigException("A module definition selector requires a nonempty plugin id.");
        return NativeModuleLoadBatch.Execute("load-definition", createContext, batch =>
        {
            var definitions = batch.Load(mainAssemblyPath, pluginDirectory);
            batch.FailurePhase = "definition-selection";
            return definitions.SingleOrDefault(plugin => StringComparer.Ordinal.Equals(plugin.Id, pluginId))
                ?? throw new ConfigException($"Plugin assembly '{mainAssemblyPath}' does not define plugin '{pluginId}'.");
        });
    }

    /// <summary>Loads a module through the compatibility API that requires exactly one definition.</summary>
    /// <param name="mainAssemblyPath">The module's main assembly path.</param>
    /// <param name="pluginDirectory">The module directory.</param>
    /// <returns>The descriptor of the module's single definition.</returns>
    /// <exception cref="ConfigException">The module is invalid or does not contain exactly one definition.</exception>
    public static PluginDescriptor LoadDescriptor(string mainAssemblyPath, string pluginDirectory) =>
        LoadDescriptor(mainAssemblyPath, pluginDirectory, static (path, name) => new PluginLoadContext(path, name));

    internal static PluginDescriptor LoadDescriptor(string mainAssemblyPath, string pluginDirectory,
        Func<string, string, PluginLoadContext> createContext) =>
        NativeModuleLoadBatch.Execute("load-descriptor", createContext, batch =>
    {
        var found = batch.Load(mainAssemblyPath, pluginDirectory);
        batch.FailurePhase = "single-definition-selection";
        return found.Count switch
        {
            1 => found[0],
            _ => throw new ConfigException($"Plugin assembly '{mainAssemblyPath}' defines {found.Count} [Plugin] types; LoadDescriptor requires exactly one definition."),
        };
    });

    internal static PluginDescriptor AdaptDefinition(PluginDefinition definition)
    {
        var native = definition.ExecutionFactory as BubbleFighter.Fantasy.Native.NativePluginExecutionFactory;
        return new PluginDescriptor(definition) { Type = native?.ImplementationType, MainAssemblyPath = native?.MainAssemblyPath };
    }

    internal static void ScanAssembly(Assembly assembly, string origin, string? mainAssemblyPath, List<PluginDescriptor> sink) =>
        sink.AddRange(BubbleFighter.Fantasy.Native.NativePluginModules.ScanAssembly(assembly, origin, mainAssemblyPath).Select(AdaptDefinition));
}
