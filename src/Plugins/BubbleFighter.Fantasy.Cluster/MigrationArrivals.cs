using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Signals;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Publishes the destination's bounded table of accepted logged-in migrations.</summary>
[Plugin("cluster.migration-arrivals", Category = "cluster", Names = ["en=Room migration arrivals", "zh-Hans=房间迁移到达", "zh-Hant=房間遷移抵達", "ko=방 이동 도착"], Description = "One-use, address-bound arrivals for native logged-in migration.",
    Provides = [typeof(IMigrationArrivals), typeof(IMigrationArrivalCleanup)])]
public sealed class MigrationArrivalsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MigrationArrivalOptions>();
        var arrivals = new MigrationArrivals(options.MaxArrivals, TimeProvider.System);
        context.Effect(context.Events.On<ConnectionAcceptedEvent>(arrivals.Observe));
        context.Effect(context.Services.Register<IMigrationArrivals>(arrivals));
        context.Effect(context.Services.Register<IMigrationArrivalCleanup>(arrivals));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Bounds the destination's pending migrations.</summary>
public sealed class MigrationArrivalOptions
{
    /// <summary>Gets or sets the combined capacity for pending arrivals and expired records awaiting seat cleanup.</summary>
    [Range(1, 4096)]
    public int MaxArrivals { get; set; } = 64;
}

internal sealed class MigrationArrivals(int capacity, TimeProvider clock) : IMigrationArrivals, IMigrationArrivalCleanup
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _byMigration = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Build, long User), Pending> _byAccount = [];
    // Weak keys retain no connection beyond its transport lifetime; each bounded live connection has one observation.
    private readonly ConditionalWeakTable<IConnection, AcceptedListener> _listeners = new();
    // Present while any arrival, pending or retired, is held; the destination's clean-up sleeps on it.
    private readonly PresenceSignal _tracked = new();

    public Task WhenAnyTracked() => _tracked.WhenPresent;

    internal void Observe(ConnectionAcceptedEvent accepted)
    {
        if (string.IsNullOrWhiteSpace(accepted.ListenerId)) throw new ArgumentException("An accepted connection requires its listener row.");
        lock (_gate)
        {
            if (_listeners.TryGetValue(accepted.Connection, out var known))
            {
                if (known.Id != accepted.ListenerId) throw new InvalidOperationException("A connection cannot change its accepting listener.");
                return;
            }
            _listeners.Add(accepted.Connection, new(accepted.ListenerId));
        }
    }

    public void Expect(MigrationArrival arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var request = arrival.Request;
        if (!AccountHandoff.IsMigrationId(request.MigrationId) || request.UserNum <= 0 || string.IsNullOrEmpty(request.ClientBuild)
            || string.IsNullOrWhiteSpace(arrival.ListenerId) || arrival.ListenerId.Length > 256
            || arrival.Lifetime <= TimeSpan.Zero || arrival.Lifetime > AccountHandoff.MaxLifetime
            || !Enum.IsDefined(request.Kind)
            || request.Guest is not null && request.Kind != MigrationKind.Room
            || (request.Kind == MigrationKind.Room
                ? request.Room is null || arrival.Reservation is null
                : request.Room is not null || arrival.Reservation is not null))
            throw new ArgumentException("The migration arrival is malformed.", nameof(arrival));
        lock (_gate)
        {
            Expire();
            if (_byMigration.ContainsKey(request.MigrationId) || _byAccount.ContainsKey((request.ClientBuild, request.UserNum)))
                throw new InvalidOperationException("The account already has a pending migration to this node.");
            if (_byMigration.Count >= capacity) throw new InvalidOperationException("The configured migration arrival capacity is exhausted.");
            var pending = new Pending(arrival, Normalize(request.ClientAddress), clock.GetTimestamp());
            _byMigration.Add(request.MigrationId, pending);
            _byAccount.Add((request.ClientBuild, request.UserNum), pending);
            _tracked.Refresh(() => _byMigration.Count != 0);
        }
    }

    public MigrationArrival? TryConsume(string clientBuild, long userNum, IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_gate)
        {
            Expire();
            if (connection.IsClosed || connection.RemoteEndPoint is not IPEndPoint remote
                || !_listeners.TryGetValue(connection, out var listener)
                || !_byAccount.TryGetValue((clientBuild, userNum), out var pending) || pending.Retired
                || pending.Arrival.ListenerId != listener.Id || !pending.Address.Equals(Normalize(remote.Address)))
                return null;
            Remove(pending);
            return pending.Arrival;
        }
    }

    public MigrationArrival? Cancel(string migrationId)
    {
        lock (_gate)
        {
            Expire();
            if (!_byMigration.TryGetValue(migrationId, out var pending) || pending.Retired) return null;
            Remove(pending);
            return pending.Arrival;
        }
    }

    internal int Count
    {
        get { lock (_gate) { Expire(); return _byMigration.Values.Count(p => !p.Retired); } }
    }

    public MigrationArrival? Retire(string migrationId)
    {
        lock (_gate)
        {
            if (!_byMigration.TryGetValue(migrationId, out var pending)) return null;
            pending.Retired = true;
            return pending.Arrival;
        }
    }

    public IReadOnlyList<MigrationArrival> Collect(bool drain = false)
    {
        lock (_gate)
        {
            Expire();
            if (drain) foreach (var pending in _byMigration.Values) pending.Retired = true;
            return _byMigration.Values.Where(p => p.Retired).Select(p => p.Arrival).ToArray();
        }
    }

    public void Complete(MigrationArrival arrival)
    {
        lock (_gate)
            if (_byMigration.TryGetValue(arrival.Request.MigrationId, out var pending)
                && pending.Retired && ReferenceEquals(pending.Arrival, arrival)) Remove(pending);
    }

    // A dual-stack listener reports IPv4 peers as mapped IPv6; both nodes must compare the same form.
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private void Expire()
    {
        var now = clock.GetTimestamp();
        foreach (var pending in _byMigration.Values.ToArray())
            if (clock.GetElapsedTime(pending.Created, now) >= pending.Arrival.Lifetime) pending.Retired = true;
    }

    private void Remove(Pending pending)
    {
        _byMigration.Remove(pending.Arrival.Request.MigrationId);
        _byAccount.Remove((pending.Arrival.Request.ClientBuild, pending.Arrival.Request.UserNum));
        _tracked.Refresh(() => _byMigration.Count != 0);
    }

    private sealed record Pending(MigrationArrival Arrival, IPAddress Address, long Created)
    {
        internal bool Retired { get; set; }
    }

    private sealed record AcceptedListener(string Id);
}
