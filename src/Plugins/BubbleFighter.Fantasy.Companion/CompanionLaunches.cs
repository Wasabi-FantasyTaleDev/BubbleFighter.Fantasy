using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Launcher;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// The bounded launch table. A launch is opened by the launcher API, waits up to <c>pending_seconds</c> for the game
/// login that redeems its game ticket, is then bound to that game session, and ends when the game connection closes.
/// Each launch holds at most one one-time connection ticket and one companion connection; a renewed ticket or a new
/// connection replaces the previous one.
/// </summary>
internal sealed class CompanionLaunches(CompanionOptions options, TimeProvider clock, ILogger logger) : ICompanionTicketIssuer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Launch> _launches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Launch> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<IConnection, Launch> _games = new(ReferenceEqualityComparer.Instance);
    private bool _closed;

    internal int Count { get { lock (_gate) return _launches.Count; } }

    /// <inheritdoc />
    public CompanionGrant Open(string account, string build)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        ArgumentException.ThrowIfNullOrEmpty(build);
        List<CompanionSocket> ended = [];
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                Sweep(ended);
                if (_launches.Count >= options.MaxLaunches)
                    throw new InvalidOperationException($"The companion launch table is at its capacity of {options.MaxLaunches}.");
                var launch = new Launch(CompanionWire.NewId(), account, build, clock.GetUtcNow().AddSeconds(options.PendingSeconds));
                _launches.Add(launch.Id, launch);
                return Grant(launch);
            }
        }
        finally { Close(ended, "companion.launch-expired"); }
    }

    /// <inheritdoc />
    public CompanionGrant? Renew(string account, string launchId)
    {
        List<CompanionSocket> ended = [];
        try
        {
            lock (_gate)
            {
                if (_closed) return null;
                Sweep(ended);
                return _launches.TryGetValue(launchId, out var launch) && launch.Account == account ? Grant(launch) : null;
            }
        }
        finally { Close(ended, "companion.launch-expired"); }
    }

    /// <summary>Consumes a connection ticket; null when it is unknown, used or expired.</summary>
    internal Launch? Redeem(string ticket)
    {
        lock (_gate)
        {
            if (_closed || !_tickets.Remove(Hash(ticket), out var launch)) return null;
            launch.TicketHash = null;
            return launch.TicketExpiresAt > clock.GetUtcNow() && _launches.ContainsKey(launch.Id) ? launch : null;
        }
    }

    /// <summary>
    /// Makes <paramref name="socket"/> the launch's connection and reports whether the launch is already bound to a
    /// game session; null when the launch ended meanwhile.
    /// </summary>
    internal bool? Attach(Launch launch, CompanionSocket socket)
    {
        CompanionSocket? previous;
        bool bound;
        lock (_gate)
        {
            if (_closed || !_launches.TryGetValue(launch.Id, out var live) || !ReferenceEquals(live, launch)) return null;
            previous = launch.Socket;
            launch.Socket = socket;
            bound = launch.Session is not null;
            socket.Initialize(launch, bound);
        }
        previous?.Close("companion.replaced");
        return bound;
    }

    /// <summary>Forgets <paramref name="socket"/> when it is still the launch's connection.</summary>
    internal void Detach(CompanionSocket socket)
    {
        lock (_gate)
            if (socket.Launch is { } launch && ReferenceEquals(launch.Socket, socket)) launch.Socket = null;
    }

    /// <summary>Binds a launch to the game session that redeemed its game ticket.</summary>
    internal void Bind(LauncherLaunchRedeemed redeemed)
    {
        CompanionSocket? socket;
        lock (_gate)
        {
            if (_closed || !_launches.TryGetValue(redeemed.LaunchId, out var launch)) return;
            if (launch.Account != redeemed.Account || launch.Session is not null)
            {
                logger.LogWarning("Companion launch {LaunchId} was redeemed by a session that does not own it; the binding is refused.", redeemed.LaunchId);
                return;
            }
            launch.Session = redeemed.Session;
            _games[redeemed.Session.Connection] = launch;
            socket = launch.Socket;
        }
        socket?.MarkConnected();
    }

    /// <summary>Ends the launch bound to a closed game connection.</summary>
    internal void End(IConnection connection)
    {
        CompanionSocket? socket;
        lock (_gate)
        {
            if (!_games.Remove(connection, out var launch)) return;
            Remove(launch);
            socket = launch.Socket;
        }
        socket?.Close("companion.game-ended");
    }

    /// <summary>The connection of the launch bound to <paramref name="session"/>, if any.</summary>
    internal CompanionSocket? SocketFor(ISession session)
    {
        lock (_gate) return _games.TryGetValue(session.Connection, out var launch) && ReferenceEquals(launch.Session, session) ? launch.Socket : null;
    }

    /// <summary>The game sessions whose launch currently has a connection that has been told it is bound.</summary>
    internal IReadOnlyList<ISession> BoundSessions()
    {
        lock (_gate)
            return _games.Values.Where(l => l.Socket is { Connected: true } && l.Session is not null).Select(l => l.Session!).ToArray();
    }

    /// <summary>Drops launches whose game never logged in and tickets that expired unused.</summary>
    internal void Sweep()
    {
        List<CompanionSocket> ended = [];
        lock (_gate) Sweep(ended);
        Close(ended, "companion.launch-expired");
    }

    /// <summary>Ends every launch; later opens fail and later tickets are refused.</summary>
    internal IReadOnlyList<CompanionSocket> CloseAll()
    {
        lock (_gate)
        {
            _closed = true;
            var sockets = _launches.Values.Select(l => l.Socket).OfType<CompanionSocket>().ToArray();
            _launches.Clear(); _tickets.Clear(); _games.Clear();
            return sockets;
        }
    }

    private void Sweep(List<CompanionSocket> ended)
    {
        var now = clock.GetUtcNow();
        foreach (var launch in _launches.Values.Where(l => l.Session is null && l.PendingUntil <= now).ToArray())
        {
            Remove(launch);
            if (launch.Socket is { } socket) ended.Add(socket);
        }
        foreach (var launch in _launches.Values.Where(l => l.TicketHash is not null && l.TicketExpiresAt <= now))
        {
            _tickets.Remove(launch.TicketHash!);
            launch.TicketHash = null;
        }
    }

    private void Remove(Launch launch)
    {
        _launches.Remove(launch.Id);
        if (launch.TicketHash is not null) _tickets.Remove(launch.TicketHash);
        launch.TicketHash = null;
        if (launch.Session is not null) _games.Remove(launch.Session.Connection);
    }

    private CompanionGrant Grant(Launch launch)
    {
        if (launch.TicketHash is not null) _tickets.Remove(launch.TicketHash);
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        launch.TicketHash = Hash(ticket);
        launch.TicketExpiresAt = clock.GetUtcNow().AddSeconds(options.TicketSeconds);
        _tickets.Add(launch.TicketHash, launch);
        return new(options.EffectivePublicUrl, ticket, launch.Id, launch.TicketExpiresAt);
    }

    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ticket)));

    private static void Close(List<CompanionSocket> sockets, string reason)
    {
        foreach (var socket in sockets) socket.Close(reason);
    }

    /// <summary>One open launch; mutable fields are guarded by the table's lock.</summary>
    internal sealed class Launch(string id, string account, string build, DateTimeOffset pendingUntil)
    {
        internal string Id { get; } = id;
        internal string Account { get; } = account;
        internal string Build { get; } = build;
        internal DateTimeOffset PendingUntil { get; } = pendingUntil;
        internal string? TicketHash { get; set; }
        internal DateTimeOffset TicketExpiresAt { get; set; }
        internal ISession? Session { get; set; }
        internal CompanionSocket? Socket { get; set; }
    }
}
