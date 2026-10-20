using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Native;

/// <summary>Prepares bounded native module sources and constructs language-neutral definitions.</summary>
public static class NativePluginModules
{
    /// <summary>Snapshots module paths and their complete operation keys before the host reserves acquisition.</summary>
    public static NativePluginModulePlan Prepare(IReadOnlyList<string> roots, int maximumDefinitions, IReadOnlyList<string>? excludedAssemblyNames = null,
        int maximumScannedDirectories = 4096, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumScannedDirectories);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDefinitions);
        if (roots.Count > maximumDefinitions) throw new ConfigException("Native source root count exceeds definition capacity.");
        var excluded = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (excludedAssemblyNames is not null)
        {
            if (excludedAssemblyNames.Count > maximumDefinitions) throw new ConfigException("Native source exclusions exceed definition capacity.");
            foreach (var name in excludedAssemblyNames)
            {
                if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
                    throw new ConfigException("Native source exclusions must be simple assembly names.");
                if (!excluded.Add(name)) throw new ConfigException($"Native source repeats excluded assembly '{name}'.");
            }
        }
        var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var scannedDirectories = 0;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root)) throw new ConfigException("Native source roots must be nonempty paths.");
            var fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot)) throw new ConfigException($"Native source root '{fullRoot}' does not exist.");
            foreach (var directory in Directory.EnumerateDirectories(fullRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scannedDirectories >= maximumScannedDirectories)
                    throw new ConfigException($"Native source exceeds its {maximumScannedDirectories} scanned directory capacity.");
                scannedDirectories++;
                var name = Path.GetFileName(directory);
                if (excluded.Contains(name)) continue;
                var path = Path.Combine(directory, name + ".dll");
                if (!File.Exists(path)) continue;
                var key = NativePluginExecutionFactory.GetCoordinationKey(path)!;
                if (!paths.TryAdd(key, path)) throw new ConfigException($"Native source lists module '{path}' more than once.");
                if (paths.Count > maximumDefinitions) throw new ConfigException("Native source module count exceeds definition capacity.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new NativePluginModulePlan(paths.Values.ToArray(), paths.Keys.ToArray(), maximumDefinitions);
    }

    /// <summary>Reads definitions from an existing assembly without acquiring ownership of borrowed code.</summary>
    public static IReadOnlyList<PluginDefinition> ScanAssembly(Assembly assembly, string origin, string? mainAssemblyPath, int maximumDefinitions = int.MaxValue)
    {
        NativePluginLoadContext.ValidateSharedReferences(assembly);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error)
        {
            if (mainAssemblyPath is not null)
                throw new ConfigException($"Plugin assembly '{mainAssemblyPath}' could not load every type: {string.Join("; ", error.LoaderExceptions.Where(item => item is not null).Select(item => item!.Message))}");
            types = error.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        var found = new List<PluginDefinition>();
        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(IPlugin).IsAssignableFrom(type) || type.GetCustomAttribute<PluginAttribute>() is null) continue;
            if (found.Count >= maximumDefinitions) throw new ConfigException($"Native source exceeds its {maximumDefinitions} remaining definition capacity.");
            found.Add(NativePluginExecutionFactory.CreateDefinition(type, origin, mainAssemblyPath));
        }
        return ValidateDefinitions(found);
    }

    /// <summary>Loads and selects one exact definition while preserving cleanup of a rejected module.</summary>
    public static PluginDefinition LoadDefinition(string mainAssemblyPath, string pluginDirectory, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ConfigException("A module definition selector requires a nonempty plugin id.");
        return NativeModuleLoadBatch.Execute("load-definition", static (path, name) => new NativePluginLoadContext(path, name), batch =>
        {
            var definitions = batch.Load(mainAssemblyPath, pluginDirectory);
            batch.FailurePhase = "definition-selection";
            return definitions.SingleOrDefault(definition => StringComparer.Ordinal.Equals(definition.Id, pluginId))
                ?? throw new ConfigException($"Plugin assembly '{mainAssemblyPath}' does not define plugin '{pluginId}'.");
        });
    }

    internal static IReadOnlyList<PluginDefinition> ValidateDefinitions(IEnumerable<PluginDefinition> definitions)
    {
        var found = definitions.ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in found)
        {
            if (string.IsNullOrWhiteSpace(definition.Id)) throw new ConfigException("A plugin definition must match its nonempty declaration identity.");
            if (!identities.Add(definition.Id)) throw new ConfigException($"Duplicate plugin id '{definition.Id}'.");
            var declaration = definition.Declaration;
            foreach (var field in typeof(PluginAttribute).GetProperties().Where(property => property.PropertyType == typeof(Type[])))
            {
                var values = (Type[])field.GetValue(declaration)!;
                var seen = new HashSet<Type>();
                for (var index = 0; index < values.Length; index++)
                {
                    var contract = values[index];
                    if (contract is null || !contract.IsInterface || contract.ContainsGenericParameters)
                        throw new ConfigException($"Plugin '{definition.Id}' declaration field '{field.Name}[{index}]' must be a closed interface type.");
                    if (!seen.Add(contract))
                        throw new ConfigException($"Plugin '{definition.Id}' declaration field '{field.Name}[{index}]' duplicates '{contract.FullName}' in the same field.");
                }
            }
            foreach (var reference in definition.ContractVersions)
                if (reference.Key is "BubbleFighter.Fantasy.Abstractions" or "BubbleFighter.Fantasy.Contracts")
                {
                    if (!Version.TryParse(reference.Value, out var version))
                        throw new ConfigException($"Plugin '{definition.Id}' declares an invalid shared contract version '{reference.Value}'.");
                    NativePluginLoadContext.ResolveSharedContract(new AssemblyName(reference.Key) { Version = version });
                }
        }
        return Array.AsReadOnly(found);
    }
}

/// <summary>Holds one immutable native source path selection and its pre-acquisition coordination keys.</summary>
public sealed class NativePluginModulePlan
{
    private readonly IReadOnlyList<string> _modulePaths;
    private readonly int _maximumDefinitions;

    internal NativePluginModulePlan(string[] modulePaths, string[] coordinationKeys, int maximumDefinitions)
    {
        _modulePaths = Array.AsReadOnly(modulePaths);
        CoordinationKeys = Array.AsReadOnly(coordinationKeys);
        _maximumDefinitions = maximumDefinitions;
    }

    /// <summary>Gets all module operation keys that the host must reserve before invoking the producer.</summary>
    public IReadOnlyList<string> CoordinationKeys { get; }

    /// <summary>Loads the prepared files and returns a batch whose lifetime is owned by source registration.</summary>
    public ValueTask<IPluginDefinitionBatch> LoadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IPluginDefinitionBatch>(NativeModuleLoadBatch.Acquire(_modulePaths, _maximumDefinitions, cancellationToken));
}
