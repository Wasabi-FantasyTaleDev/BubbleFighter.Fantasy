using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Cluster;

// The shared plaza: nodes of one verified account domain see each other's plaza members, per client build. Friendly
// servers (another or no data scope, or an unverified database) never share a scope, so their players stay apart.
internal sealed partial class ServerDirectory
{
    private readonly Lock _plazaGate = new();
    private readonly List<PlazaRecord> _plazaLog = [];
    private readonly Dictionary<(string Scope, string Build, int User), PlazaPresent> _plazaMembers = [];
    private TaskCompletionSource _plazaPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _plazaSequence;

    private sealed record PlazaRecord(long Sequence, string Scope, string Node, PlazaWireEvent Change);

    private sealed class PlazaPresent(string node, string token, PlazaWireEvent entered)
    {
        internal string Node { get; } = node;
        internal string Token { get; } = token;
        internal PlazaWireEvent Entered { get; set; } = entered;
        internal PlazaWireEvent? Moved { get; set; }
    }

    // A node shares only while its lease is current and its heartbeat proved the domain its scope declares.
    private string? PlazaScope(string nodeId, string token, long epoch, out HashSet<string> builds)
    {
        builds = [];
        if (!_nodes.TryGetValue(nodeId, out var node)) return null;
        lock (node.Gate)
        {
            if (node.Lease is not { } lease || !Fresh(lease) || lease.Token != token || lease.Epoch != epoch) return null;
            var scope = node.Options.DataScope;
            if (scope.Length == 0 || !Guid.TryParseExact(lease.PersistentDomainId, "N", out var domain) || domain == Guid.Empty
                || _expectedDomains.TryGetValue(scope, out var expected)
                    && !string.Equals(expected, lease.PersistentDomainId, StringComparison.OrdinalIgnoreCase)) return null;
            builds = [.. lease.Builds.Select(build => build.Build)];
            return scope + "/" + domain.ToString("N");
        }
    }

    private bool PlazaOwnerCurrent(PlazaPresent member)
    {
        if (!_nodes.TryGetValue(member.Node, out var node)) return false;
        lock (node.Gate) return node.Lease is { } lease && Fresh(lease) && lease.Token == member.Token;
    }

    internal int PublishPlaza(string nodeId, string token, long epoch, PlazaWirePost post)
    {
        if (!_options.PlazaFederation) return 404;
        if (post.Events is null || post.Events.Length > _options.MaxPlazaEvents || !post.Events.All(PlazaTransport.Valid)) return 400;
        lock (_plazaGate)
        {
            ExpirePlaza();
            if (PlazaScope(nodeId, token, epoch, out var builds) is not { } scope) return 409;
            if (post.Events.Any(change => !builds.Contains(change.Build))) return 400;
            foreach (var change in post.Events)
            {
                var key = (scope, change.Build, change.User);
                var present = _plazaMembers.GetValueOrDefault(key);
                // A player is hosted by one node at a time; another node's report for it waits for its leave.
                if (present is not null && present.Node != nodeId && PlazaOwnerCurrent(present)) continue;
                switch (change.Kind)
                {
                    case PlazaFederationEventKind.Entered:
                        if (present is null || present.Node != nodeId || present.Token != token)
                        {
                            if (_plazaMembers.Count >= _options.MaxPlazaMembers && present is null) continue;
                            _plazaMembers[key] = new(nodeId, token, change);
                        }
                        else present.Entered = change;
                        break;
                    case PlazaFederationEventKind.Moved:
                        if (present is null || present.Node != nodeId) continue;
                        present.Moved = change;
                        break;
                    default:
                        if (present is null || present.Node != nodeId) continue;
                        _plazaMembers.Remove(key);
                        break;
                }
                AppendPlaza(scope, nodeId, change);
            }
            Pulse();
            return 204;
        }
    }

    internal async ValueTask<(int Status, PlazaWireBatch? Batch)> ReadPlazaAsync(string nodeId, string token, long epoch,
        string hub, long after, TimeSpan wait, CancellationToken ct)
    {
        if (!_options.PlazaFederation) return (404, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);
        while (true)
        {
            Task pulse;
            lock (_plazaGate)
            {
                ExpirePlaza();
                if (PlazaScope(nodeId, token, epoch, out _) is not { } scope) return (409, null);
                var oldest = _plazaLog.Count == 0 ? _plazaSequence + 1 : _plazaLog[0].Sequence;
                if (hub != _incarnation || after > _plazaSequence || after < oldest - 1)
                {
                    var members = _plazaMembers.Where(pair => pair.Key.Scope == scope && pair.Value.Node != nodeId)
                        .SelectMany(pair => pair.Value.Moved is { } moved ? new[] { pair.Value.Entered, moved } : [pair.Value.Entered]);
                    return (200, new(_incarnation, _plazaSequence, true, [.. members]));
                }
                var changes = _plazaLog.Where(record => record.Sequence > after && record.Scope == scope && record.Node != nodeId)
                    .Select(record => record.Change).ToArray();
                if (changes.Length > 0 || deadline.IsCancellationRequested)
                    return (200, new(_incarnation, _plazaSequence, false, changes));
                pulse = _plazaPulse.Task;
            }
            try { await pulse.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested) { }
        }
    }

    // Runs under the plaza gate: a node whose lease lapsed or was replaced takes its members with it.
    private void ExpirePlaza()
    {
        var expired = false;
        foreach (var (key, member) in _plazaMembers.ToArray())
        {
            if (PlazaOwnerCurrent(member)) continue;
            _plazaMembers.Remove(key);
            AppendPlaza(key.Scope, member.Node, new(key.Build, PlazaFederationEventKind.Left, key.User, null, null));
            expired = true;
        }
        if (expired) Pulse();
    }

    private void AppendPlaza(string scope, string node, PlazaWireEvent change)
    {
        _plazaLog.Add(new(++_plazaSequence, scope, node, change));
        // A reader that falls behind the retained log receives a snapshot instead.
        if (_plazaLog.Count > _options.MaxPlazaEvents) _plazaLog.RemoveRange(0, _plazaLog.Count - _options.MaxPlazaEvents);
    }

    private void Pulse()
    {
        var pulse = _plazaPulse;
        _plazaPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pulse.TrySetResult();
    }
}
