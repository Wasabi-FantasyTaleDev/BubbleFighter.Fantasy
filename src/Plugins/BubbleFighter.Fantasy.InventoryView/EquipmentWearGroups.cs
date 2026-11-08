using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Caches one bounded snapshot of immutable protocol-owned positions before the inventory transaction.</summary>
internal sealed class EquipmentWearGroups(IEventBus events, string protocol)
{
    private sealed record Snapshot(IReadOnlyList<InventoryWearGroup> Baseline,
        InventoryWearGroup[] Sources, IReadOnlyList<InventoryWearGroup> Groups);

    private Snapshot? _snapshot;

    internal async ValueTask<IReadOnlyList<InventoryWearGroup>> ResolveAsync(
        IReadOnlyList<InventoryWearGroup> baseline, CancellationToken ct)
    {
        var additions = await events.WaterfallAsync<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>(
            new(protocol), _ => ValueTask.FromResult<IReadOnlyList<InventoryWearGroup>>([]), ct).ConfigureAwait(false);
        if (additions is null || additions.Count > InventoryLimits.MaxWearGroups - baseline.Count)
            throw new ConfigException($"Additional equipment positions exceed the combined limit of {InventoryLimits.MaxWearGroups}.");
        ct.ThrowIfCancellationRequested();
        if (additions.Count == 0)
        {
            Volatile.Write(ref _snapshot, null);
            return baseline;
        }
        var cached = Volatile.Read(ref _snapshot);
        if (cached is not null && ReferenceEquals(cached.Baseline, baseline)
            && cached.Sources.Length == additions.Count
            && !cached.Sources.Where((group, index) => !ReferenceEquals(group, additions[index])).Any())
            return cached.Groups;

        var groups = new List<InventoryWearGroup>(baseline.Count + additions.Count);
        groups.AddRange(baseline);
        var existingItems = new HashSet<int>(baseline.Where(group => group.ItemIds is not null).SelectMany(group => group.ItemIds!));
        var ranges = baseline.Where(group => group.ItemIds is null).ToArray();
        var sources = new InventoryWearGroup[additions.Count];
        var itemCount = 0;
        for (var index = 0; index < additions.Count; index++)
        {
            var group = additions[index];
            if (group?.ItemIds is not { } ids || ids.Count == 0
                || ids.Count > InventoryLimits.MaxItemsPerWearGroup - itemCount || group.RequireEquipped)
                throw new ConfigException("Additional equipment positions require bounded explicit item identities and no mandatory occupant.");
            var members = ids.ToArray();
            itemCount += members.Length;
            var identities = new HashSet<int>(members);
            if (members.Any(id => id <= 0) || identities.Count != members.Length
                || !identities.Contains(group.LegacyDefaultItemId) || group.FirstItemId != members.Min() || group.LastItemId != members.Max())
                throw new ConfigException("An additional equipment position has invalid or repeated item identities.");
            if (members.Any(id => !existingItems.Add(id) || ranges.Any(range => range.Contains(id))))
                throw new ConfigException("Additional equipment positions overlap an existing position.");
            groups.Add(group with { ItemIds = Array.AsReadOnly(members) });
            sources[index] = group;
        }
        ct.ThrowIfCancellationRequested();
        var resolved = groups.AsReadOnly();
        Volatile.Write(ref _snapshot, new Snapshot(baseline, sources, resolved));
        return resolved;
    }
}
