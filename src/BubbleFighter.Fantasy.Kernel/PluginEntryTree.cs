using System.Collections.ObjectModel;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Assigns immutable runtime scopes to a fully validated plugin entry hierarchy.</summary>
internal sealed class PluginEntryTree
{
    private readonly ServiceRuntime _runtime;

    private PluginEntryTree(ServiceRuntime runtime, Dictionary<string, PluginRuntimeEntry> entries)
    {
        _runtime = runtime;
        Entries = new ReadOnlyDictionary<string, PluginRuntimeEntry>(entries);
    }

    internal IReadOnlyDictionary<string, PluginRuntimeEntry> Entries { get; }

    internal static PluginEntryTree Build(IReadOnlyList<PluginRow> rows, ServiceRuntime runtime, PluginCatalog catalog, PluginEntryTree? previous = null, bool allowUnavailableIsolation = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        if (previous is not null) runtime.RequireOwner(previous._runtime);
        var known = KnownServices(catalog);
        var nodes = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Id))
                throw new ConfigException("Plugin entry id must not be empty.");
            if (nodes.ContainsKey(row.Id)) throw Failure(row.Id, "id", "entry identity is duplicated");
            if (row.IsolatedServices is null) throw Failure(row.Id, "isolated_services", "must not be null");
            if (row.IsolatedServices.Count > runtime.Capacity)
                throw Failure(row.Id, "isolated_services", "service isolation metadata exceeds runtime capacity");
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            PluginRuntimeEntry? accepted = null;
            previous?.Entries.TryGetValue(row.Id, out accepted);
            for (var index = 0; index < row.IsolatedServices.Count; index++)
            {
                var name = row.IsolatedServices[index];
                var field = $"isolated_services[{index}]";
                if (name is null) throw Failure(row.Id, field, "unknown closed service interface");
                if (known.Ambiguous.Contains(name)) throw Failure(row.Id, field, "service interface name is ambiguous");
                if (!known.Types.ContainsKey(name))
                {
                    var retained = accepted is not null && accepted.IsolatedServices.Contains(name, StringComparer.Ordinal) &&
                        (allowUnavailableIsolation || accepted.UnavailableIsolatedServices.Contains(name, StringComparer.Ordinal));
                    if (!retained) throw Failure(row.Id, field, "unknown closed service interface");
                    missing.Add(name);
                }
                if (!names.Add(name)) throw Failure(row.Id, field, "service interface is duplicated");
            }
            var isolated = names.ToArray();
            var resolved = isolated.Where(known.Types.ContainsKey).Select(name => known.Types[name]).ToArray();
            nodes.Add(row.Id, new(row.Id, row.ParentId, isolated, resolved, missing.ToArray()));
        }

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var ready = new Queue<string>();
        foreach (var node in nodes.Values)
        {
            if (node.ParentId is null) ready.Enqueue(node.Id);
            else
            {
                if (node.ParentId == node.Id) throw Failure(node.Id, "parent_id", "an entry cannot parent itself");
                if (!nodes.ContainsKey(node.ParentId)) throw Failure(node.Id, "parent_id", "parent entry does not exist");
                if (!children.TryGetValue(node.ParentId, out var descendants)) children[node.ParentId] = descendants = [];
                descendants.Add(node.Id);
            }
        }
        var ordered = new List<Candidate>();
        while (ready.TryDequeue(out var id))
        {
            ordered.Add(nodes[id]);
            if (children.TryGetValue(id, out var descendants))
                foreach (var child in descendants) ready.Enqueue(child);
        }
        if (ordered.Count != nodes.Count)
        {
            var reached = ordered.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            var id = nodes.Keys.First(key => !reached.Contains(key));
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            while (ancestors.Add(id)) id = nodes[id].ParentId!;
            throw Failure(id, "parent_id", "entry parents form a cycle");
        }

        // Missing but previously validated names still consume flattened isolation capacity.
        var inherited = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var unavailable = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var rootNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in ordered)
        {
            var names = node.ParentId is null ? rootNames : inherited[node.ParentId];
            if (node.IsolatedServices.Length > 0)
            {
                names = new HashSet<string>(names, StringComparer.Ordinal);
                names.UnionWith(node.IsolatedServices);
            }
            if (names.Count > runtime.Capacity)
                throw Failure(node.Id, "isolated_services", "inherited service isolation metadata exceeds runtime capacity");
            inherited.Add(node.Id, names);
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            if (node.ParentId is not null) missing.UnionWith(unavailable[node.ParentId]);
            missing.UnionWith(node.Unavailable);
            unavailable.Add(node.Id, missing.ToArray());
        }

        var entries = new Dictionary<string, PluginRuntimeEntry>(StringComparer.Ordinal);
        foreach (var node in ordered)
        {
            var parentScope = node.ParentId is null ? runtime.RootScope : entries[node.ParentId].Scope;
            PluginRuntimeEntry? old = null;
            previous?.Entries.TryGetValue(node.Id, out old);
            if (old is not null && old.ParentId == node.ParentId && ReferenceEquals(old.ParentScope, parentScope) &&
                old.IsolatedServices.SequenceEqual(node.IsolatedServices, StringComparer.Ordinal) &&
                old.IsolatedContracts.SequenceEqual(node.Contracts) &&
                old.UnavailableIsolatedServices.SequenceEqual(unavailable[node.Id], StringComparer.Ordinal))
                entries.Add(node.Id, old);
            else
                entries.Add(node.Id, new PluginRuntimeEntry(node.Id, node.ParentId, parentScope.CreateChild(node.Contracts),
                    old?.Identity ?? new object(), node.IsolatedServices, node.Contracts, parentScope, unavailable[node.Id]));
        }
        return new PluginEntryTree(runtime, entries);
    }

    /// <summary>
    /// Returns the first isolated service name of <paramref name="rowId"/> or one of its ancestors that
    /// resolves differently under the two catalogs, or null when its namespace does not depend on the difference.
    /// </summary>
    internal static string? IsolationDiffers(IReadOnlyList<PluginRow> rows, string rowId, PluginCatalog left, PluginCatalog right)
    {
        var byId = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var a = KnownServices(left);
        var b = KnownServices(right);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var id = rowId; id is not null && byId.TryGetValue(id, out var row) && visited.Add(id); id = row.ParentId)
            foreach (var name in row.IsolatedServices)
            {
                if (name is null) continue;
                var inLeft = a.Types.TryGetValue(name, out var leftType) && !a.Ambiguous.Contains(name) ? leftType : null;
                var inRight = b.Types.TryGetValue(name, out var rightType) && !b.Ambiguous.Contains(name) ? rightType : null;
                if (inLeft != inRight || a.Ambiguous.Contains(name) != b.Ambiguous.Contains(name)) return name;
            }
        return null;
    }

    private static (Dictionary<string, Type> Types, HashSet<string> Ambiguous) KnownServices(PluginCatalog catalog)
    {
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        void Add(Type contract)
        {
            if (!contract.IsInterface || contract.ContainsGenericParameters || contract.FullName is not { } name) return;
            if (!types.TryAdd(name, contract) && types[name] != contract) ambiguous.Add(name);
        }
        Add(typeof(IServiceRegistry));
        Add(typeof(IEventBus));
        Add(typeof(IPluginHost));
        var fields = typeof(PluginAttribute).GetProperties().Where(property => property.PropertyType == typeof(Type[])).ToArray();
        foreach (var descriptor in catalog.Plugins)
        {
            var declaration = descriptor.Declaration;
            foreach (var field in fields)
                foreach (var contract in (Type[])field.GetValue(declaration)!) Add(contract);
        }
        return (types, ambiguous);
    }

    private static ConfigException Failure(string id, string field, string reason) => new(id, [$"{field}: {reason}."]);

    private sealed record Candidate(string Id, string? ParentId, string[] IsolatedServices, Type[] Contracts, string[] Unavailable);
}

/// <summary>Retains stable entry identity while recording one immutable parent and service scope selection.</summary>
internal sealed class PluginRuntimeEntry(string id, string? parentId, ServiceScope scope, object identity,
    IReadOnlyList<string> isolatedServices, IReadOnlyList<Type> isolatedContracts, ServiceScope parentScope, IReadOnlyList<string> unavailableIsolatedServices)
{
    internal string Id { get; } = id;
    internal string? ParentId { get; } = parentId;
    // A structurally retained scope is usable by activations only when IsNamespaceReady is true.
    internal ServiceScope Scope { get; } = scope;
    internal object Identity { get; } = identity;
    internal IReadOnlyList<string> IsolatedServices { get; } = Array.AsReadOnly(isolatedServices.ToArray());
    internal IReadOnlyList<Type> IsolatedContracts { get; } = Array.AsReadOnly(isolatedContracts.ToArray());
    internal ServiceScope ParentScope { get; } = parentScope;
    internal IReadOnlyList<string> UnavailableIsolatedServices { get; } = Array.AsReadOnly(unavailableIsolatedServices.ToArray());
    internal bool IsNamespaceReady => UnavailableIsolatedServices.Count == 0;
}
