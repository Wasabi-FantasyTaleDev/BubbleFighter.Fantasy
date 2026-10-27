using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// Server plugins' view of the companion channel. The routes and offers registered here are this server's
/// capabilities: a connection negotiates the ones its hello asks for at the same schema version.
/// </summary>
internal sealed class CompanionHub(CompanionLaunches launches) : ICompanionHub
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Type, int Version), CompanionHandler> _routes = [];
    private readonly Dictionary<(string Type, int Version), int> _offers = [];

    /// <inheritdoc />
    public IDisposable Route(string type, int version, CompanionHandler handler)
    {
        Check(type, version);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            if (!_routes.TryAdd((type, version), handler))
                throw new InvalidOperationException($"Companion message {type} v{version} already has a handler.");
        return new Disposer(() => { lock (_gate) _routes.Remove((type, version)); });
    }

    /// <inheritdoc />
    public IDisposable Offer(string type, int version)
    {
        Check(type, version);
        lock (_gate) _offers[(type, version)] = _offers.GetValueOrDefault((type, version)) + 1;
        return new Disposer(() =>
        {
            lock (_gate)
                if (--_offers[(type, version)] == 0) _offers.Remove((type, version));
        });
    }

    /// <inheritdoc />
    public bool Supports(ISession session, string type, int version) =>
        launches.SocketFor(session) is { } socket && socket.Negotiated(type, version);

    /// <inheritdoc />
    public bool Send(ISession session, string type, int version, object? payload) =>
        launches.SocketFor(session) is { } socket && socket.TrySend(type, version, payload);

    /// <inheritdoc />
    public IReadOnlyList<ISession> BoundSessions() => launches.BoundSessions();

    /// <summary>The requested capabilities this server routes or offers at the same version.</summary>
    internal Dictionary<string, int> Negotiate(IReadOnlyDictionary<string, int> requested)
    {
        lock (_gate)
            return requested.Where(r => _routes.ContainsKey((r.Key, r.Value)) || _offers.ContainsKey((r.Key, r.Value)))
                .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    internal CompanionHandler? Handler(string type, int version)
    {
        lock (_gate) return _routes.GetValueOrDefault((type, version));
    }

    private static void Check(string type, int version)
    {
        // The companion.* types are the channel's own controls.
        if (!CompanionWire.ValidName(type) || type.StartsWith("companion.", StringComparison.Ordinal))
            throw new ArgumentException($"'{type}' is not a valid companion message type.", nameof(type));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, 65535);
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}
