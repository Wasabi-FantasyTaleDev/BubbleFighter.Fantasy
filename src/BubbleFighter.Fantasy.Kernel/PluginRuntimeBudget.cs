using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Preserves startup grants while charging each admitted identity for its largest declared or owned capacity.</summary>
internal sealed class PluginRuntimeBudget
{
    private readonly Dictionary<string, StartupGrant> _initialGrants;

    private PluginRuntimeBudget(int entryCapacity, int serviceMetadataCapacity, int definitionSourceCapacity,
        int definitionCapacity, Dictionary<string, StartupGrant> initialGrants)
    {
        EntryCapacity = entryCapacity;
        ServiceMetadataCapacity = serviceMetadataCapacity;
        DefinitionSourceCapacity = definitionSourceCapacity;
        DefinitionCapacity = definitionCapacity;
        _initialGrants = initialGrants;
    }

    internal int EntryCapacity { get; }
    internal int ServiceMetadataCapacity { get; }

    // The host registers IServiceRegistry, IEventBus, IPluginHost and IHostReadiness before any row activates.
    internal const int HostServiceCount = 4;
    internal int DefinitionSourceCapacity { get; }
    internal int DefinitionCapacity { get; }

    internal static PluginRuntimeBudget Capture(IReadOnlyList<PluginRow> initial, int initialDefinitionCount = 0)
    {
        if (initial is null) throw new ConfigException("Initial runtime rows must not be null.");
        var expected = CollectionCount(initial.Count, "initial");
        long entries = expected;
        long metadata = HostServiceCount;
        long sources = 0;
        long definitions = CollectionCount(initialDefinitionCount, "initial definition");
        var grants = new Dictionary<string, StartupGrant>(StringComparer.Ordinal);
        var count = 0;
        foreach (var row in initial)
        {
            if (count >= expected) throw new ConfigException("Initial row enumeration exceeds its reported count.");
            ValidateRow(row, "initial", count++);
            if (!grants.TryAdd(row.Id, StartupGrant.From(row.Lifecycle)))
                throw new ConfigException(row.Id, ["Initial entry identity is duplicated."]);
            entries = checked(entries + row.Lifecycle.EntryReserve);
            metadata = checked(metadata + 1L + row.Lifecycle.EffectCapacity + row.Lifecycle.ServiceMetadataReserve);
            sources = checked(sources + row.Lifecycle.DefinitionSourceReserve);
            definitions = checked(definitions + row.Lifecycle.DefinitionReserve);
            _ = Capacity(entries, "entry");
            _ = Capacity(metadata, "service metadata");
            _ = Capacity(sources, "definition source");
            _ = Capacity(definitions, "definition");
        }
        if (count != expected) throw new ConfigException("Initial row enumeration differs from its reported count.");
        return new PluginRuntimeBudget(Capacity(entries, "entry"), Capacity(metadata, "service metadata"),
            Capacity(sources, "definition source"), Capacity(definitions, "definition"), grants);
    }

    internal void Validate(IReadOnlyList<PluginRow> candidate, IReadOnlyCollection<PluginRow> retiring, IReadOnlyCollection<PluginRow> owned)
    {
        if (candidate is null || retiring is null)
            throw new ConfigException("Candidate and retiring row collections must not be null.");
        var candidateCount = CollectionCount(candidate.Count, "candidate");
        var retiringCount = CollectionCount(retiring.Count, "retiring");
        if ((long)candidateCount + retiringCount > EntryCapacity)
            throw new ConfigException($"Runtime entry capacity of {EntryCapacity} is exceeded by candidate and retiring rows.");
        var capacities = new Dictionary<string, int>(StringComparer.Ordinal);
        long metadata = HostServiceCount;
        Include(candidate, candidateCount, isCandidate: true);
        Include(retiring, retiringCount, isCandidate: false);
        var expected = CollectionCount(owned.Count, "owned");
        var count = 0;
        foreach (var row in owned)
        {
            if (count >= expected) throw new ConfigException("The owned row enumeration exceeds its reported count.");
            ValidateRow(row, "owned", count++);
            // Candidate and retiring rows seed the peak; owned snapshots may only raise it, so a repeated
            // snapshot for one Ordinal id is idempotent. This relies on LiveRows enforcing one activation per
            // identity (PluginHost.Scheduling.cs), which keeps duplicate owned EffectCapacity values comparable.
            if (!capacities.TryGetValue(row.Id, out var peak))
                throw new ConfigException(row.Id, ["Owned entry must appear in candidate or retiring rows."]);
            if (row.Lifecycle.EffectCapacity <= peak) continue;
            metadata = checked(metadata + row.Lifecycle.EffectCapacity - peak);
            capacities[row.Id] = row.Lifecycle.EffectCapacity;
            if (metadata > ServiceMetadataCapacity)
                throw new ConfigException(row.Id, [$"Runtime service metadata capacity of {ServiceMetadataCapacity} is exceeded by declared and owned entry capacities."]);
        }
        if (count != expected) throw new ConfigException("The owned row enumeration differs from its reported count.");

        void Include(IEnumerable<PluginRow> rows, int expected, bool isCandidate)
        {
            var label = isCandidate ? "candidate" : "retiring";
            var count = 0;
            foreach (var row in rows)
            {
                if (count >= expected) throw new ConfigException($"The {label} row enumeration exceeds its reported count.");
                ValidateRow(row, label, count++);
                if (!capacities.TryAdd(row.Id, row.Lifecycle.EffectCapacity))
                    throw new ConfigException(row.Id, ["Entry identity is duplicated or still retiring and cannot be reused."]);
                if (isCandidate)
                {
                    var grant = _initialGrants.GetValueOrDefault(row.Id);
                    if (StartupGrant.From(row.Lifecycle) != grant)
                        throw new ConfigException(row.Id, ["lifecycle.entry_reserve, lifecycle.service_metadata_reserve, lifecycle.definition_source_reserve and lifecycle.definition_reserve must match this entry's startup grants; new entries must use zero."]);
                }
                metadata = checked(metadata + 1L + row.Lifecycle.EffectCapacity);
                if (metadata > ServiceMetadataCapacity)
                    throw new ConfigException(row.Id, [$"Runtime service metadata capacity of {ServiceMetadataCapacity} is exceeded by candidate and retiring rows."]);
            }
            if (count != expected) throw new ConfigException($"The {label} row enumeration differs from its reported count.");
        }
    }

    private static void ValidateRow(PluginRow row, string collection, int index)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Id))
            throw new ConfigException($"The {collection} row at index {index} must have a nonempty entry identity.");
        PluginContext.ValidateLifecycle(row);
    }

    private static int CollectionCount(int count, string collection)
    {
        if (count < 0) throw new ConfigException($"The {collection} row count must not be negative.");
        return count;
    }

    private static int Capacity(long value, string kind)
    {
        if (value > int.MaxValue) throw new ConfigException($"The aggregate runtime {kind} capacity exceeds the supported range.");
        return checked((int)value);
    }

    private readonly record struct StartupGrant(int EntryReserve, int ServiceMetadataReserve,
        int DefinitionSourceReserve, int DefinitionReserve)
    {
        internal static StartupGrant From(PluginLifecycleOptions options) => new(options.EntryReserve,
            options.ServiceMetadataReserve, options.DefinitionSourceReserve, options.DefinitionReserve);
    }
}
