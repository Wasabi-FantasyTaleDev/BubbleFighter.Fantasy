using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed partial class ServerDirectory
{
    private readonly Lock _presenceGate = new();
    private readonly Dictionary<string, PresencePeak> _presencePeaks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<string> Groups => Array.AsReadOnly(_options.PresenceGroups.Select(group => group.Id).ToArray());

    internal bool CanReadPresence(string node, string group) => _options.PresenceGroups
        .Any(value => value.Id == group && value.Nodes.Contains(node, StringComparer.Ordinal));

    /// <inheritdoc />
    public ValueTask<LauncherPresenceSnapshot> ReadAsync(string group, bool includeRoster, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_presenceGate)
        {
            var configured = _options.PresenceGroups.SingleOrDefault(value => value.Id == group)
                ?? throw new KeyNotFoundException("Unknown presence group.");
            return ValueTask.FromResult(PresenceSnapshot(configured, includeRoster));
        }
    }

    private LauncherPresenceSnapshot PresenceSnapshot(PresenceGroupOptions group, bool includeRoster)
    {
        var complete = true;
        var members = new Dictionary<int, LauncherPresenceMember>();
        foreach (var id in group.Nodes.Order(StringComparer.Ordinal))
        {
            var node = _nodes[id];
            lock (node.Gate)
            {
                if (node.Lease is not { } lease || !Fresh(lease) || lease.Presence is null
                    || !string.Equals(lease.PersistentDomainId, group.PersistentDomainId, StringComparison.OrdinalIgnoreCase))
                { complete = false; continue; }
                foreach (var member in lease.Presence)
                    if (!members.TryGetValue(member.UserNum, out var previous) || previous.Migrating && !member.Migrating)
                        members[member.UserNum] = member;
            }
        }
        var peak = _presencePeaks[group.Id].Observe(members.Count);
        var players = includeRoster ? members.Values.Where(value => value.Name is not null)
            .Select(value => new LauncherRosterPlayer(value.Name!, value.Level!.Value, value.BuildId))
            .OrderBy(value => value.Name, StringComparer.Ordinal).ThenBy(value => value.BuildId, StringComparer.Ordinal).ToArray() : [];
        return new(members.Count, peak, complete, includeRoster && complete && players.Length == members.Count,
            Array.AsReadOnly(players));
    }

    private void ObservePresencePeaks()
    {
        lock (_presenceGate)
            foreach (var group in _options.PresenceGroups) _ = PresenceSnapshot(group, includeRoster: false);
    }
}
