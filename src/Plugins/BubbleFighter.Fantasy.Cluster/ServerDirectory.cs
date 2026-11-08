using System.Security.Cryptography;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Stores one fenced lease per configured node, using the directory's monotonic clock.</summary>
internal sealed partial class ServerDirectory : IServerDirectory, IServerSelectionDirectory, ILauncherPresenceDirectory
{
    private readonly DirectoryOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Node> _nodes;
    private readonly Dictionary<string, string> _expectedDomains;
    private readonly string _incarnation = Guid.NewGuid().ToString("N");

    internal ServerDirectory(DirectoryOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
        foreach (var group in options.PresenceGroups) _presencePeaks.Add(group.Id, new PresencePeak(group));
        _nodes = options.Nodes.ToDictionary(node => node.Id, node => new Node(node), StringComparer.Ordinal);
        _expectedDomains = options.Selectors.Where(selector => selector.Automatic)
            .SelectMany(selector => selector.Nodes.Select(id => new KeyValuePair<string, string>(_nodes[id].Options.DataScope, selector.PersistentDomainId.ToLowerInvariant())))
            .Distinct().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal LeaseGrant? Register(string nodeId, string instance)
    {
        if (!_nodes.TryGetValue(nodeId, out var node) || !Guid.TryParseExact(instance, "N", out _)) return null;
        lock (node.Gate)
        {
            if (node.Lease is { } current && Fresh(current) && current.Instance != instance) return null;
            if (node.Lease is not { } lease || lease.Instance != instance || !Fresh(lease))
            {
                CapObservationExpiry(node);
                var epoch = checked(++node.LastEpoch);
                lease = new(instance, _incarnation + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), epoch, _time.GetTimestamp());
                node.Lease = lease;
            }
            // Registration retries do not extend the lease without a fresh report.
            return new(lease.Token, lease.Epoch, _options.LeaseSeconds);
        }
    }

    internal bool Observe(string nodeId, NodeHeartbeat report)
    {
        if (!_nodes.TryGetValue(nodeId, out var node) || !ValidReport(node.Options, report)) return false;
        lock (node.Gate)
        {
            if (node.Lease is not { } lease || !Fresh(lease) || lease.Token != report.LeaseToken
                || lease.Epoch != report.Epoch || report.Sequence <= lease.Sequence) return false;
            lease.Sequence = report.Sequence;
            lease.ObservedAt = _time.GetTimestamp();
            lease.Draining = report.Draining;
            lease.PersistentDomainId = report.PersistentDomainId;
            lease.Presence = report.Presence?.Select(value => value with { }).ToArray();
            lease.Builds = report.Builds!.Select(build => build with { }).ToArray();
            lease.Rooms = report.Rooms!.Select(room => RoomListingValidation.Copy(room) with
                { NodeId = nodeId, Epoch = lease.Epoch, JoinAllowed = false }).ToArray();
            var observed = _time.GetUtcNow().ToUniversalTime();
            node.Observation = new(observed, observed.AddSeconds(_options.LeaseSeconds),
                FormattableString.Invariant($"{_incarnation}.{lease.Epoch}.{lease.Sequence}"), lease.ObservedAt);
        }
        ObservePresencePeaks();
        return true;
    }

    internal bool Release(string nodeId, string token)
    {
        if (!_nodes.TryGetValue(nodeId, out var node)) return false;
        lock (node.Gate)
        {
            if (node.Lease?.Token != token) return false;
            CapObservationExpiry(node);
            node.Lease = null;
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ServerSelectorEntry> ListServers(string clientBuild)
        => ListServers(clientBuild, configuredMembership: false);

    /// <inheritdoc />
    public IReadOnlyList<string> ClientBuilds => Array.AsReadOnly(_options.Nodes.SelectMany(node => node.Endpoints)
        .Select(endpoint => endpoint.Build).Distinct(StringComparer.Ordinal).ToArray());

    /// <inheritdoc />
    public IReadOnlyList<ServerSelectorEntry> ListSelectionServers(string clientBuild)
        => ListServers(clientBuild, configuredMembership: true);

    private IReadOnlyList<ServerSelectorEntry> ListServers(string clientBuild, bool configuredMembership)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBuild);
        var result = new List<ServerSelectorEntry>(_options.Selectors.Length);
        foreach (var selector in _options.Selectors)
        {
            var targets = selector.Nodes.Select(id => Snapshot(_nodes[id], clientBuild)).OfType<TargetSnapshot>()
                .Where(snapshot => configuredMembership || !selector.Automatic || snapshot.Target.Online && !snapshot.Target.Draining
                    && snapshot.Target.Players < snapshot.Target.Capacity
                    && string.Equals(snapshot.PersistentDomainId, selector.PersistentDomainId, StringComparison.OrdinalIgnoreCase))
                .Select(snapshot => snapshot.Target).ToArray();
            result.Add(new(selector.Id, selector.Name, selector.Automatic, targets) { ShortName = selector.ShortName });
        }
        return result.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<FederatedRoom> ListRooms(string requestingNodeId, string clientBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBuild);
        if (!_nodes.TryGetValue(requestingNodeId, out var requester)) throw new KeyNotFoundException("Unknown requesting node.");
        var groups = requester.Options.RoomDirectories.ToHashSet(StringComparer.Ordinal);
        var rooms = new List<FederatedRoom>();
        foreach (var node in _nodes.Values)
        {
            if (!node.Options.Endpoints.Any(endpoint => endpoint.Build == clientBuild) ||
                node.Options.Id != requestingNodeId && !node.Options.RoomDirectories.Any(groups.Contains)) continue;
            lock (node.Gate)
            {
                if (node.Lease is not { } lease || !Fresh(lease) || !lease.Builds.Any(build => build.Build == clientBuild && build.Ready)) continue;
                rooms.AddRange(lease.Rooms.Where(room => room.ClientBuild == clientBuild).Select(room => RoomListingValidation.Copy(room) with
                {
                    JoinAllowed = node.Options.Id == requestingNodeId
                }));
            }
        }
        // Cross-node authorization takes both node locks in canonical order after snapshots release theirs.
        return rooms.Select(room => room.NodeId == requestingNodeId ? room : room with
            { JoinAllowed = CanMigrateToRoom(requestingNodeId, room) })
            .OrderBy(room => room.NodeId, StringComparer.Ordinal).ThenBy(room => room.RoomId).ToArray();
    }

    private TargetSnapshot? Snapshot(Node node, string build)
    {
        if (node.Options.Endpoints.FirstOrDefault(endpoint => endpoint.Build == build) is not { } endpoint) return null;
        lock (node.Gate)
        {
            var lease = node.Lease is { } current && Fresh(current) ? current : null;
            var state = lease?.Builds.FirstOrDefault(value => value.Build == build);
            var matchesDomain = !_expectedDomains.TryGetValue(node.Options.DataScope, out var expected)
                || string.Equals(lease?.PersistentDomainId, expected, StringComparison.OrdinalIgnoreCase);
            var target = new ServerNodeTarget(node.Options.Id, endpoint.Host, endpoint.Port, node.Options.DataScope, node.Options.JoinScope,
                state?.Ready == true && matchesDomain, lease?.Draining ?? false, state?.Players ?? 0, endpoint.Capacity)
            {
                Authentication = node.Options.Authentication is { } auth ? new(auth.AuthorityId, auth.ApiUrl, auth.Mode, auth.PolicyMode) : null,
                ObservedAtUtc = node.Observation?.ObservedAtUtc,
                ExpiresAtUtc = ObservationExpiry(node.Observation),
                StatusRevision = node.Observation?.Revision,
            };
            return new(target, lease?.PersistentDomainId);
        }
    }

    private bool Fresh(Lease lease) => _time.GetElapsedTime(lease.ObservedAt) < TimeSpan.FromSeconds(_options.LeaseSeconds);

    private DateTimeOffset? ObservationExpiry(Observation? observation)
    {
        if (observation is null) return null;
        var remaining = TimeSpan.FromSeconds(_options.LeaseSeconds) - _time.GetElapsedTime(observation.Timestamp);
        var projected = _time.GetUtcNow().ToUniversalTime().Add(remaining);
        return projected < observation.ExpiresAtUtc ? projected : observation.ExpiresAtUtc;
    }

    private void CapObservationExpiry(Node node)
    {
        if (node.Observation is { } observation)
        {
            var now = _time.GetUtcNow().ToUniversalTime();
            if (now < observation.ExpiresAtUtc) node.Observation = observation with { ExpiresAtUtc = now };
        }
    }

    private bool ValidReport(DirectoryNodeOptions node, NodeHeartbeat report)
    {
        if (report.Presence is { } presence && (presence.Length > _options.MaxPresencePlayersPerNode
            || presence.Any(member => member is null || member.UserNum <= 0
                || !node.Endpoints.Any(endpoint => endpoint.Build == member.BuildId)
                || report.Builds is null || !report.Builds.Any(build => build is not null && build.Build == member.BuildId)
                || (member.Name is null) != (member.Level is null) || member.Level < 0
                || member.Name is not null && (member.Name.Length is < 1 or > 128 || member.Name.Any(char.IsControl)))
            || presence.Select(member => member.UserNum).Distinct().Count() != presence.Length)) return false;
        if (report.LeaseToken is not { Length: > 0 and <= 128 } || report.Epoch <= 0 || report.Sequence <= 0
            || (report.PersistentDomainId is not null || node.DataScope.Length > 0)
                && (!Guid.TryParseExact(report.PersistentDomainId, "N", out var domainId) || domainId == Guid.Empty)
            || report.Builds is null || report.Builds.Length > node.Endpoints.Length || report.Builds.Any(build => build is null
                || build.Players < 0 || !node.Endpoints.Any(endpoint => endpoint.Build == build.Build))
            || report.Builds.Select(build => build.Build).Distinct(StringComparer.Ordinal).Count() != report.Builds.Length
            || report.Rooms is null || report.Rooms.Length > _options.MaxRoomsPerNode || report.Rooms.Any(room => room is null
                || !node.Endpoints.Any(endpoint => endpoint.Build == room.ClientBuild) || room.RoomId < 0
                || room.Title is null || room.Title.Length > 256 || room.Title.Any(char.IsControl)
                || room.Mode is null || room.Mode.Length > 64 || room.Mode.Any(char.IsControl)
                || room.Phase is null || room.Phase.Length > 32 || room.Phase.Any(char.IsControl)
                || room.Map is { Length: > 256 } || room.Map?.Any(char.IsControl) == true
                || room.Players < 0 || room.Capacity is < 1 or > 256 || room.Players > room.Capacity
                || !RoomListingValidation.IsValid(room, _options.MaxRoomSettingsBytes))
            || report.Rooms.Select(room => (room.ClientBuild, room.RoomId)).Distinct().Count() != report.Rooms.Length) return false;
        return true;
    }

    private sealed class Node(DirectoryNodeOptions options)
    {
        internal Lock Gate { get; } = new();
        internal DirectoryNodeOptions Options { get; } = options;
        internal long LastEpoch { get; set; }
        internal Lease? Lease { get; set; }
        internal Observation? Observation { get; set; }
    }

    private sealed record Observation(DateTimeOffset ObservedAtUtc, DateTimeOffset ExpiresAtUtc, string Revision, long Timestamp);

    private sealed record TargetSnapshot(ServerNodeTarget Target, string? PersistentDomainId);

    private sealed class Lease(string instance, string token, long epoch, long observedAt)
    {
        internal string Instance { get; } = instance;
        internal string Token { get; } = token;
        internal long Epoch { get; } = epoch;
        internal long ObservedAt { get; set; } = observedAt;
        internal long Sequence { get; set; }
        internal bool Draining { get; set; }
        internal string? PersistentDomainId { get; set; }
        internal LauncherPresenceMember[]? Presence { get; set; }
        internal NodeBuildState[] Builds { get; set; } = [];
        internal FederatedRoom[] Rooms { get; set; } = [];
    }
}

internal sealed record LeaseGrant(string Token, long Epoch, int LeaseSeconds);
internal sealed record NodeBuildState(string Build, bool Ready, int Players);
internal sealed record NodeHeartbeat(string LeaseToken, long Epoch, long Sequence, bool Draining, string? PersistentDomainId,
    NodeBuildState[]? Builds, FederatedRoom[]? Rooms)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LauncherPresenceMember[]? Presence { get; init; }
}
