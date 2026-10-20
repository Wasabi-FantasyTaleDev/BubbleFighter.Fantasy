using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Reserves startup allocation for initial and deferred entries while admitting only the initial configuration.</summary>
/// <remarks>Deferred definitions and isolated contracts need not exist until ordinary configuration admission.
/// All row data and identity-bound grants are captured before the host creates runtime resources.</remarks>
public sealed class PluginStartupPlan
{
    /// <summary>Snapshots both row sets and rejects inconsistent collections or duplicate entry identities.</summary>
    /// <param name="initialRows">Entries whose definitions are available in the seed catalog and may start immediately.</param>
    /// <param name="deferredRows">Entries allocated at startup and admitted later through configuration reconciliation.</param>
    public PluginStartupPlan(IReadOnlyList<PluginRow> initialRows, IReadOnlyList<PluginRow> deferredRows)
    {
        ArgumentNullException.ThrowIfNull(initialRows);
        ArgumentNullException.ThrowIfNull(deferredRows);
        var initialCount = Count(initialRows, "initial");
        var deferredCount = Count(deferredRows, "deferred");
        if ((long)initialCount + deferredCount > int.MaxValue)
            throw new ConfigException("The aggregate startup entry count exceeds the supported range.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        InitialRows = Capture(initialRows, initialCount, "initial", identities);
        DeferredRows = Capture(deferredRows, deferredCount, "deferred", identities);
        AllRows = Array.AsReadOnly(InitialRows.Concat(DeferredRows).ToArray());
    }

    /// <summary>Gets the detached entries admitted when the host is constructed.</summary>
    public IReadOnlyList<PluginRow> InitialRows { get; }

    /// <summary>Gets detached startup allocations whose definitions are resolved only when those entries are admitted.</summary>
    public IReadOnlyList<PluginRow> DeferredRows { get; }

    internal IReadOnlyList<PluginRow> AllRows { get; }

    private static int Count(IReadOnlyList<PluginRow> rows, string kind)
    {
        var count = rows.Count;
        if (count < 0) throw new ConfigException($"The {kind} startup row count must not be negative.");
        return count;
    }

    private static IReadOnlyList<PluginRow> Capture(IReadOnlyList<PluginRow> source, int expected, string kind, HashSet<string> identities)
    {
        var captured = new List<PluginRow>();
        foreach (var row in source)
        {
            if (captured.Count >= expected)
                throw new ConfigException($"The {kind} startup row enumeration exceeds its reported count.");
            if (row is null || string.IsNullOrWhiteSpace(row.Id))
                throw new ConfigException($"The {kind} startup row at index {captured.Count} must have a nonempty entry identity.");
            if (!identities.Add(row.Id)) throw new ConfigException($"Duplicate plugin row id '{row.Id}' across the startup plan.");
            captured.Add(PluginRowSnapshot.Capture(row));
        }
        if (captured.Count != expected || source.Count != expected)
            throw new ConfigException($"The {kind} startup row collection changed or differs from its reported count.");
        return Array.AsReadOnly(captured.ToArray());
    }
}
