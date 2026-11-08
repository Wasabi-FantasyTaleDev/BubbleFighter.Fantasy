using System.Threading.Channels;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed partial class ServerDirectory
{
    private readonly Lock _migrationGate = new();
    private readonly Dictionary<string, PendingMigration> _migrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Channel<PendingMigration>> _inboxes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _polling = new(StringComparer.Ordinal);

    internal bool HasMigrationLease(string node, string token, long epoch)
    {
        if (!_nodes.TryGetValue(node, out var state)) return false;
        lock (state.Gate)
            return state.Lease is { } lease && Fresh(lease) && lease.Token == token && lease.Epoch == epoch;
    }

    internal (int Status, PendingMigration? Pending) EnqueueMigration(string source, string token, long epoch, MigrationRequest request)
    {
        lock (_migrationGate)
        {
            ExpireMigrations();
            if (!_options.Migrations || source != request.SourceNode || !MigrationTransport.Valid(request)) return (400, null);
            if (!AuthorizeMigration(request, token, epoch)) return (409, null);
            if (_migrations.ContainsKey(request.MigrationId)) return (409, null);
            if (_migrations.Values.Count(p => p.Request.DestinationNode == request.DestinationNode) >= _options.MaxPendingMigrations)
                return (503, null);
            var inbox = Inbox(request.DestinationNode);
            CompactInbox(inbox);
            var pending = new PendingMigration(request, token, epoch, _time.GetTimestamp(),
                TimeSpan.FromMilliseconds(_options.MigrationRequestTimeoutMs) + AccountHandoff.MaxLifetime);
            if (!inbox.Writer.TryWrite(pending)) return (503, null);
            _migrations.Add(request.MigrationId, pending);
            return (200, pending);
        }
    }

    internal async ValueTask<(int Status, MigrationEnvelope? Envelope)> ReadMigrationAsync(string node, string token,
        long epoch, TimeSpan wait, CancellationToken ct)
    {
        Channel<PendingMigration> inbox;
        lock (_migrationGate)
        {
            if (!HasMigrationLease(node, token, epoch)) return (409, null);
            if (!_polling.Add(node)) return (409, null);
            inbox = Inbox(node);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);
        try
        {
            while (await inbox.Reader.WaitToReadAsync(deadline.Token).ConfigureAwait(false))
            {
                lock (_migrationGate)
                {
                    ExpireMigrations();
                    if (!HasMigrationLease(node, token, epoch)) return (409, null);
                    while (inbox.Reader.TryRead(out var pending))
                    {
                        if (!IsCurrent(pending)) continue;
                        pending.Queued = false;
                        if (pending.Request.DestinationEpoch != epoch)
                        {
                            pending.Completion.TrySetException(new MigrationUnavailableException());
                            _migrations.Remove(pending.Request.MigrationId);
                            continue;
                        }
                        if (pending.Cancelled)
                        {
                            _migrations.Remove(pending.Request.MigrationId);
                            return (200, new(pending.Request, true));
                        }
                        if (!AuthorizeMigration(pending.Request, pending.SourceToken, pending.SourceEpoch))
                        {
                            pending.Completion.TrySetException(new MigrationUnavailableException());
                            _migrations.Remove(pending.Request.MigrationId);
                            continue;
                        }
                        pending.Delivered = true;
                        return (200, new(pending.Request, false));
                    }
                }
            }
            return (204, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (204, null); }
        finally { lock (_migrationGate) _polling.Remove(node); }
    }

    internal int OfferMigration(string node, string token, long epoch, string id, MigrationOffer offer)
    {
        lock (_migrationGate)
        {
            ExpireMigrations();
            if (!HasMigrationLease(node, token, epoch)) return 409;
            if (!MigrationTransport.Valid(offer)) return 400;
            if (!_migrations.TryGetValue(id, out var pending) || pending.Request.DestinationNode != node
                || pending.Request.DestinationEpoch != epoch || !pending.Delivered || pending.Cancelled
                || pending.Completion.Task.IsCompleted) return 409;
            if (!AuthorizeMigration(pending.Request, pending.SourceToken, pending.SourceEpoch))
            {
                AbandonMigration(pending);
                return 409;
            }
            pending.Created = _time.GetTimestamp();
            pending.Lifetime = offer.Accepted ? offer.Lifetime : TimeSpan.FromMilliseconds(_options.MigrationRequestTimeoutMs);
            pending.Completion.TrySetResult(offer);
            return 204;
        }
    }

    internal int CancelMigration(string source, string token, long epoch, string id)
    {
        lock (_migrationGate)
        {
            ExpireMigrations();
            if (!HasMigrationLease(source, token, epoch)) return 409;
            if (!_migrations.TryGetValue(id, out var pending)) return 204;
            if (pending.Request.SourceNode != source || pending.SourceToken != token || pending.SourceEpoch != epoch) return 409;
            AbandonMigration(pending);
            return 204;
        }
    }

    internal void AbandonMigration(PendingMigration pending)
    {
        lock (_migrationGate)
        {
            if (!IsCurrent(pending) || pending.Cancelled) return;
            pending.Cancelled = true;
            pending.Completion.TrySetException(new MigrationUnavailableException());
            if (pending.Queued) return;
            var inbox = Inbox(pending.Request.DestinationNode);
            CompactInbox(inbox);
            // Every retained request owns one queue slot, including while its destination callback runs.
            if (!inbox.Writer.TryWrite(pending))
                throw new InvalidOperationException("The migration cancellation lost its reserved inbox capacity.");
            pending.Queued = true;
        }
    }

    private bool CanMigrateToRoom(string source, FederatedRoom room)
    {
        if (!_options.Migrations || room.Listing is null || !_nodes.TryGetValue(source, out var from)) return false;
        string token;
        long epoch;
        lock (from.Gate)
        {
            if (from.Lease is not { } lease) return false;
            token = lease.Token;
            epoch = lease.Epoch;
        }
        return AuthorizeMigrationPath(source, room.NodeId, room.Epoch, room.ClientBuild, token, epoch,
            MigrationKind.Room, room.RoomId, room.InstanceId, null);
    }

    internal MigrationTarget? ResolveMigrationTarget(string source, string token, long epoch, string destination, string build)
    {
        if (!_options.Migrations || source == destination || !RoomDirectoryClientOptions.Identifier(build)
            || !_nodes.TryGetValue(destination, out var target)) return null;
        long destinationEpoch;
        lock (target.Gate)
        {
            if (target.Lease is not { } lease) return null;
            destinationEpoch = lease.Epoch;
        }
        return AuthorizeMigrationPath(source, destination, destinationEpoch, build, token, epoch, MigrationKind.Home, 0, Guid.Empty, null)
            ? new(destination, destinationEpoch) : null;
    }

    private bool AuthorizeMigration(MigrationRequest request, string sourceToken, long sourceEpoch) =>
        AuthorizeMigrationPath(request.SourceNode, request.DestinationNode, request.DestinationEpoch, request.ClientBuild,
            sourceToken, sourceEpoch, request.Kind, request.Room?.RoomId ?? 0, request.Room?.InstanceId ?? Guid.Empty, request.Guest);

    private bool AuthorizeMigrationPath(string sourceNode, string destinationNode, long destinationEpoch, string clientBuild,
        string sourceToken, long sourceEpoch, MigrationKind kind, int roomId, Guid roomInstance,
        MigrationGuest? guest)
    {
        if (!_nodes.TryGetValue(sourceNode, out var from) || !_nodes.TryGetValue(destinationNode, out var to)
            || !from.Options.NativeMigrationBuilds.Contains(clientBuild, StringComparer.Ordinal)
            || !to.Options.NativeMigrationBuilds.Contains(clientBuild, StringComparer.Ordinal)) return false;
        var first = string.CompareOrdinal(sourceNode, destinationNode) < 0 ? from : to;
        var second = ReferenceEquals(first, from) ? to : from;
        lock (first.Gate)
        lock (second.Gate)
        {
            if (from.Lease is not { } source || source.Token != sourceToken || source.Epoch != sourceEpoch || !Fresh(source)
                || to.Lease is not { } target || target.Epoch != destinationEpoch || !Fresh(target) || target.Draining
                || !source.Builds.Any(b => b.Build == clientBuild && b.Ready)
                || target.Builds.FirstOrDefault(b => b.Build == clientBuild && b.Ready) is not { } population
                || to.Options.Endpoints.FirstOrDefault(e => e.Build == clientBuild) is not { } endpoint
                || population.Players >= endpoint.Capacity || from.Options.JoinScope.Length == 0
                || from.Options.JoinScope != to.Options.JoinScope) return false;
            if (kind == MigrationKind.Room && (!from.Options.RoomDirectories.Intersect(to.Options.RoomDirectories, StringComparer.Ordinal).Any()
                || !target.Rooms.Any(r => r.ClientBuild == clientBuild && r.RoomId == roomId
                    && r.InstanceId == roomInstance && r.Listing is not null))) return false;
            if (guest is not null)
                return from.Options.DataScope != to.Options.DataScope && kind == MigrationKind.Room;
            return from.Options.DataScope.Length > 0 && from.Options.DataScope == to.Options.DataScope
                && from.Options.Authentication is { Mode: "master-issued", PolicyMode: "authority" } authority
                && to.Options.Authentication is { Mode: "master-issued", PolicyMode: "authority" } targetAuthority
                && authority.AuthorityId == targetAuthority.AuthorityId && authority.ApiUrl == targetAuthority.ApiUrl
                && Guid.TryParseExact(source.PersistentDomainId, "N", out var sourceDomain) && sourceDomain != Guid.Empty
                && Guid.TryParseExact(target.PersistentDomainId, "N", out var targetDomain) && sourceDomain == targetDomain
                && (!_expectedDomains.TryGetValue(from.Options.DataScope, out var expected)
                    || string.Equals(expected, source.PersistentDomainId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private Channel<PendingMigration> Inbox(string node)
    {
        if (!_inboxes.TryGetValue(node, out var inbox))
        {
            inbox = Channel.CreateBounded<PendingMigration>(new BoundedChannelOptions(_options.MaxPendingMigrations)
            { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            _inboxes.Add(node, inbox);
        }
        return inbox;
    }

    private void ExpireMigrations()
    {
        foreach (var pending in _migrations.Values.ToArray())
            if (_time.GetElapsedTime(pending.Created) >= pending.Lifetime)
            {
                _migrations.Remove(pending.Request.MigrationId);
                pending.Completion.TrySetException(new MigrationUnavailableException());
            }
    }

    private bool IsCurrent(PendingMigration pending) => ReferenceEquals(_migrations.GetValueOrDefault(pending.Request.MigrationId), pending);
    private void CompactInbox(Channel<PendingMigration> inbox)
    {
        var current = new List<PendingMigration>();
        while (inbox.Reader.TryRead(out var pending))
            if (IsCurrent(pending)) current.Add(pending);
        foreach (var pending in current)
            if (!inbox.Writer.TryWrite(pending)) throw new InvalidOperationException("Migration inbox compaction exceeded its capacity.");
    }

    internal sealed class PendingMigration(MigrationRequest request, string sourceToken, long sourceEpoch, long created, TimeSpan lifetime)
    {
        internal MigrationRequest Request { get; } = request;
        internal string SourceToken { get; } = sourceToken;
        internal long SourceEpoch { get; } = sourceEpoch;
        internal TaskCompletionSource<MigrationOffer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long Created { get; set; } = created;
        internal TimeSpan Lifetime { get; set; } = lifetime;
        internal bool Delivered { get; set; }
        internal bool Queued { get; set; } = true;
        internal bool Cancelled { get; set; }
    }
}

internal sealed class MigrationUnavailableException : Exception;
