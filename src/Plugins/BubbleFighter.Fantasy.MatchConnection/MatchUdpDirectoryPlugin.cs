using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.MatchConnection;

/// <summary>Publishes a bounded UDP identity directory independently of the match reply adapters.</summary>
[Plugin("match-udp-directory", Description = "Authenticated match UDP endpoint leases.",
    ProvidesMany = [typeof(IMatchUdpRelayDirectory)])]
public sealed class MatchUdpDirectoryPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MatchUdpDirectoryOptions>();
        var directory = new MatchUdpDirectory(options);
        context.Effect(directory);
        context.Effect(context.Services.RegisterMany<IMatchUdpRelayDirectory>(directory));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Configures one protocol's retained initialized UDP relay sessions.</summary>
public sealed class MatchUdpDirectoryOptions
{
    /// <summary>Gets or sets the match relay protocol owning the directory.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of simultaneously retained session leases.</summary>
    [Range(1, 65536)]
    public int MaxSessions { get; set; } = 1024;
}

/// <summary>Binds server-issued identities to one UDP endpoint while the authenticated round lease remains current.</summary>
public sealed class MatchUdpDirectory : IMatchUdpRelayDirectory, IDisposable
{
    private sealed class Entry(ISession session, Func<bool> isCurrent)
    {
        internal ISession Session { get; } = session;
        internal Func<bool> IsCurrent { get; } = isCurrent;
        internal byte[]? Token;
        internal IPEndPoint? Endpoint;
        internal bool Revoked;
        internal bool Disabled;
        internal bool Verified;
        internal MatchUdpRelayState DisabledFrom;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly MatchUdpDirectoryOptions _options;
    private bool _disposed;

    /// <summary>Creates an empty directory using validated protocol and retention bounds.</summary>
    public MatchUdpDirectory(MatchUdpDirectoryOptions options)
    {
        Validator.ValidateObject(options, new ValidationContext(options), true);
        if (options.Protocol.Any(char.IsWhiteSpace))
            throw new ArgumentException("UDP directory protocol cannot contain whitespace.", nameof(options));
        _options = options;
    }

    /// <inheritdoc />
    public string Protocol => _options.Protocol;

    /// <inheritdoc />
    public IDisposable Register(ISession session, Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (session.Connection.IsClosed || !isCurrent())
            throw new InvalidOperationException("UDP registration requires a current initialized match lease.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Count >= _options.MaxSessions)
                throw new InvalidOperationException("UDP session capacity reached.");
            if (_entries.ContainsKey(session.Id))
                throw new InvalidOperationException("Duplicate UDP match registration.");
            var entry = new Entry(session, isCurrent);
            _entries.Add(session.Id, entry);
            return new Registration(this, entry);
        }
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> IssueAsync(ISession session, int identityBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (identityBytes is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(identityBytes), "Native UDP identities contain four or eight bytes.");
        var entry = Find(session);
        if (entry is null || !Current(entry))
            throw new InvalidOperationException("UDP identity requires a current initialized match lease.");
        lock (_gate)
        {
            if (_disposed || entry.Revoked || entry.Disabled)
                throw new InvalidOperationException("UDP match registration was revoked.");
            if (entry.Token is not null)
                throw new InvalidOperationException("UDP match identity was already issued.");
            byte[]? token = null;
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var candidate = RandomNumberGenerator.GetBytes(identityBytes);
                if (candidate.All(value => value == 0) || _entries.Values.Any(other => other.Token is { } issued
                    && CryptographicOperations.FixedTimeEquals(candidate, issued)))
                    continue;
                token = candidate;
                break;
            }
            if (token is null)
                throw new InvalidOperationException("Unable to issue a unique native UDP identity.");
            entry.Token = token;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(token.ToArray());
        }
    }

    /// <inheritdoc />
    public bool TryBind(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint, out ISession? session) =>
        Resolve(token, remoteEndPoint, true, out session);

    /// <inheritdoc />
    public bool TryResolve(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint, out ISession? session) =>
        Resolve(token, remoteEndPoint, false, out session);

    /// <inheritdoc />
    public void Disable(ISession session)
    {
        var entry = Find(session);
        if (entry is null || !Current(entry)) return;
        lock (_gate)
        {
            if (_disposed || entry.Revoked) return;
            if (!entry.Disabled)
                entry.DisabledFrom = entry.Verified ? MatchUdpRelayState.Verified
                    : entry.Endpoint is not null ? MatchUdpRelayState.Bound
                    : entry.Token is not null ? MatchUdpRelayState.Offered : MatchUdpRelayState.Registered;
            entry.Disabled = true;
            entry.Endpoint = null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ISession> BoundSessions()
    {
        lock (_gate)
            return _entries.Values.Where(entry => !entry.Revoked && !entry.Disabled && entry.Endpoint is not null)
                .Select(entry => entry.Session).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ISession> RegisteredSessions()
    {
        lock (_gate) return _entries.Values.Where(entry => !entry.Revoked).Select(entry => entry.Session).ToArray();
    }

    /// <inheritdoc />
    public MatchUdpRelayState StateOf(ISession session)
    {
        var entry = Find(session);
        if (entry is null || !Current(entry)) return MatchUdpRelayState.Absent;
        lock (_gate)
            return entry.Revoked || _disposed ? MatchUdpRelayState.Absent
                : entry.Disabled ? MatchUdpRelayState.Disabled
                : entry.Verified ? MatchUdpRelayState.Verified
                : entry.Endpoint is not null ? MatchUdpRelayState.Bound
                : entry.Token is not null ? MatchUdpRelayState.Offered : MatchUdpRelayState.Registered;
    }

    /// <inheritdoc />
    public MatchUdpRelayState DisabledFrom(ISession session)
    {
        var entry = Find(session);
        if (entry is null) return MatchUdpRelayState.Absent;
        lock (_gate) return entry.Disabled && !entry.Revoked ? entry.DisabledFrom : MatchUdpRelayState.Absent;
    }

    /// <inheritdoc />
    public bool Verify(ISession session)
    {
        var entry = Find(session);
        if (entry is null || !Current(entry)) return false;
        lock (_gate)
        {
            if (_disposed || entry.Revoked || entry.Disabled || entry.Endpoint is null || entry.Verified) return false;
            entry.Verified = true;
            return true;
        }
    }

    /// <inheritdoc />
    public void Reset(ISession session)
    {
        var entry = Find(session);
        if (entry is null) return;
        lock (_gate)
        {
            entry.Token = null;
            entry.Endpoint = null;
            entry.Verified = false;
            entry.Disabled = false;
        }
    }

    /// <inheritdoc />
    public bool TryGetEndpoint(ISession session, out IPEndPoint? remoteEndPoint)
    {
        remoteEndPoint = null;
        var entry = Find(session);
        if (entry is null || !Current(entry)) return false;
        lock (_gate)
        {
            if (_disposed || entry.Revoked || entry.Disabled || !entry.Verified || entry.Endpoint is not { } endpoint)
                return false;
            remoteEndPoint = new IPEndPoint(endpoint.Address, endpoint.Port);
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetTarget(ISession session, out IPEndPoint? remoteEndPoint, out ReadOnlyMemory<byte> identity)
    {
        remoteEndPoint = null;
        identity = ReadOnlyMemory<byte>.Empty;
        var entry = Find(session);
        if (entry is null || !Current(entry)) return false;
        lock (_gate)
        {
            if (_disposed || entry.Revoked || entry.Disabled || entry.Endpoint is not { } endpoint || entry.Token is not { } token)
                return false;
            remoteEndPoint = new IPEndPoint(endpoint.Address, endpoint.Port);
            identity = token.ToArray();
            return true;
        }
    }

    private bool Resolve(ReadOnlySpan<byte> token, IPEndPoint remote, bool bind, out ISession? session)
    {
        ArgumentNullException.ThrowIfNull(remote);
        session = null;
        if (token.Length is not (4 or 8) || remote.Port == 0) return false;
        Entry? found = null;
        lock (_gate)
        {
            if (_disposed) return false;
            foreach (var entry in _entries.Values)
                if (!entry.Revoked && !entry.Disabled && entry.Token is { } issued
                    && CryptographicOperations.FixedTimeEquals(token, issued))
                {
                    found = entry;
                    break;
                }
        }
        // The room/lifecycle predicate may acquire its own locks; never invoke it under the directory lock.
        if (found is null || !Current(found)) return false;
        lock (_gate)
        {
            if (_disposed || found.Revoked || found.Disabled || found.Token is not { } currentToken
                || !CryptographicOperations.FixedTimeEquals(token, currentToken)) return false;
            if (found.Endpoint is null && bind)
                found.Endpoint = new IPEndPoint(remote.Address, remote.Port);
            if (found.Endpoint is null || !found.Endpoint.Equals(remote)) return false;
            session = found.Session;
            return true;
        }
    }

    private Entry? Find(ISession session)
    {
        lock (_gate)
            return !_disposed && _entries.TryGetValue(session.Id, out var entry)
                && ReferenceEquals(entry.Session, session) && !entry.Revoked ? entry : null;
    }

    private static bool Current(Entry entry) => !entry.Session.Connection.IsClosed && entry.IsCurrent();

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            entry.Revoked = true;
            entry.Token = null;
            entry.Endpoint = null;
            if (_entries.TryGetValue(entry.Session.Id, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(entry.Session.Id);
        }
    }

    /// <summary>Invalidates every retained identity and endpoint when the directory row unloads.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Revoked = true;
                entry.Token = null;
                entry.Endpoint = null;
            }
            _entries.Clear();
        }
    }

    private sealed class Registration(MatchUdpDirectory owner, Entry entry) : IDisposable
    {
        private MatchUdpDirectory? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(entry);
    }
}
