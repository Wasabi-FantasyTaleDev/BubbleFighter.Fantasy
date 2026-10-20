using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.MatchConnection;

/// <summary>Owns authenticated match connections and their bounded handshake lifetime.</summary>
[Plugin("match-connection", Description = "Authenticated match transport lifecycle.",
    Requires = [typeof(IMessageRouter)], Optional = [typeof(IMatchRoomDirectory)],
    OptionalMany = [typeof(IMatchCapabilityDirectory), typeof(IInboundMessageBinding<MatchAuthenticate>),
        typeof(IInboundMessageBinding<MatchSceneRegistered>), typeof(IInboundMessageBinding<MatchCapabilityAuthenticate>),
        typeof(IInboundMessageBinding<MatchSceneIdentityRegistered>), typeof(IInboundMessageBinding<MatchMapNamed>),
        typeof(IMessageSender<MatchAuthenticationAccepted>), typeof(IMessageSender<MatchSceneAccepted>)])]
public sealed class MatchConnectionPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MatchConnectionOptions>();
        IMatchRoomDirectory directory;
        bool mapReport;
        var sceneReport = !options.CapabilityAuthentication || options.RequireCapabilitySceneReport;
        if (options.CapabilityAuthentication)
        {
            RequireBinding<MatchCapabilityAuthenticate>();
            if (sceneReport) RequireBinding<MatchSceneIdentityRegistered>();
            var directories = context.Services.GetAll<IMatchCapabilityDirectory>()
                .Where(item => item.Protocol == options.Protocol).ToArray();
            if (directories.Length != 1)
                throw new InvalidOperationException($"Match capability directory for protocol '{options.Protocol}' requires exactly one provider; found {directories.Length}.");
            directory = directories[0];
            mapReport = BindingCount<MatchMapNamed>() switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidOperationException($"Ambiguous match map binding for protocol '{options.Protocol}'."),
            };
        }
        else
        {
            RequireBinding<MatchAuthenticate>();
            RequireBinding<MatchSceneRegistered>();
            RequireBinding<MatchMapNamed>();
            directory = context.Services.Get<IMatchRoomDirectory>();
            mapReport = true;
        }
        var accepted = RequireSender<MatchAuthenticationAccepted>();
        var initialized = sceneReport ? RequireSender<MatchSceneAccepted>() : null;
        var lifecycle = new MatchConnectionLifecycle(directory, accepted, initialized, options, context.Logger);
        context.Effect(lifecycle);
        var routes = context.Services.Get<IMessageRouter>().For(context);
        if (options.CapabilityAuthentication)
        {
            context.Effect(routes.On<MatchCapabilityAuthenticate>(options.Protocol, lifecycle.AuthenticateCapabilityAsync));
            if (sceneReport)
                context.Effect(routes.On<MatchSceneIdentityRegistered>(options.Protocol, lifecycle.RegisterSceneIdentityAsync));
        }
        else
        {
            context.Effect(routes.On<MatchAuthenticate>(options.Protocol, lifecycle.AuthenticateAsync));
            context.Effect(routes.On<MatchSceneRegistered>(options.Protocol, lifecycle.RegisterSceneAsync));
        }
        if (mapReport) context.Effect(routes.On<MatchMapNamed>(options.Protocol, lifecycle.NameMapAsync));
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => lifecycle.Disconnect(e.Connection.Id)));
        return ValueTask.CompletedTask;

        int BindingCount<T>() where T : IMessage => context.Services.GetAll<IInboundMessageBinding<T>>()
            .Count(binding => binding.Protocol == options.Protocol);
        void RequireBinding<T>() where T : IMessage
        {
            var count = BindingCount<T>();
            if (count != 1)
                throw new InvalidOperationException($"Match inbound bindings missing or ambiguous for protocol '{options.Protocol}': IInboundMessageBinding<{typeof(T).Name}> requires exactly one provider; found {count}.");
        }
        IMessageSender<T> RequireSender<T>() where T : IMessage
        {
            var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == options.Protocol).ToArray();
            return senders.Length == 1 ? senders[0]
                : throw new InvalidOperationException($"Match reply sender IMessageSender<{typeof(T).Name}> for protocol '{options.Protocol}' requires exactly one provider; found {senders.Length}.");
        }
    }
}

/// <summary>Bounds match transport count and incomplete-handshake retention.</summary>
public sealed class MatchConnectionOptions
{
    /// <summary>Gets or sets the protocol scope served by the installed match dialect.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-1256/relay";
    /// <summary>Gets or sets the maximum number of authenticated or pending match connections.</summary>
    [Range(1, 65536)]
    public int MaxConnections { get; set; } = 1024;
    /// <summary>Gets or sets how long authentication may wait for scene registration.</summary>
    [Range(1000, 120000)]
    public int HandshakeTimeoutMs { get; set; } = 15000;

    /// <summary>Gets or sets whether this row mounts opaque native capability messages and selects a protocol-scoped room directory instead of account-bearing authentication.</summary>
    public bool CapabilityAuthentication { get; set; }

    /// <summary>Gets or sets whether native capability joins wait for an actor report; direct-success dialects initialize before the join reply.</summary>
    public bool RequireCapabilitySceneReport { get; set; } = true;
}

/// <summary>Serializes each transport's handshake and revokes leases on failure, timeout, or unload.</summary>
public sealed class MatchConnectionLifecycle : IDisposable
{
    private sealed class Entry(ISession session, bool capabilityOnly)
    {
        public ISession Session { get; } = session;
        public bool CapabilityOnly { get; } = capabilityOnly;
        public object Sync { get; } = new();
        public CancellationTokenSource Stop { get; } = new();
        public bool Revoked { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public MatchSeatBinding? Binding { get; set; }
        public bool Ready { get; set; }
        public long Started { get; } = Environment.TickCount64;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly IMatchRoomDirectory _directory;
    private readonly IMessageSender<MatchAuthenticationAccepted> _accepted;
    private readonly IMessageSender<MatchSceneAccepted>? _initialized;
    private readonly MatchConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>Creates a lifecycle whose timer and retained connections end on disposal.</summary>
    public MatchConnectionLifecycle(IMatchRoomDirectory directory,
        IMessageSender<MatchAuthenticationAccepted> accepted, IMessageSender<MatchSceneAccepted>? initialized,
        MatchConnectionOptions options, ILogger logger)
    {
        Validator.ValidateObject(options, new ValidationContext(options), true);
        if (initialized is null && (!options.CapabilityAuthentication || options.RequireCapabilitySceneReport))
            throw new ArgumentException("Scene-report authentication requires a scene acknowledgement sender.", nameof(initialized));
        _directory = directory;
        _accepted = accepted;
        _initialized = initialized;
        _options = options;
        _logger = logger;
        _timer = new Timer(_ => Sweep(), null, options.HandshakeTimeoutMs, options.HandshakeTimeoutMs);
    }

    /// <summary>Checks the capability before sending any authentication reply; duplicates are rejected.</summary>
    public ValueTask AuthenticateAsync(ISession session, MatchAuthenticate message, CancellationToken ct) =>
        AuthenticateCoreAsync(session, false, token => _directory.BindAsync(session, message, token), ct);

    /// <summary>Authenticates an exact native key through the protocol-scoped capability directory, without guessing account fields.</summary>
    public ValueTask AuthenticateCapabilityAsync(ISession session, MatchCapabilityAuthenticate message, CancellationToken ct)
    {
        if (_directory is not IMatchCapabilityDirectory directory)
            throw new InvalidOperationException("The room directory does not support native match capabilities.");
        if (directory.Protocol != _options.Protocol)
            throw new InvalidOperationException("Native match directory protocol does not match this lifecycle.");
        return AuthenticateCoreAsync(session, true, token => directory.BindCapabilityAsync(session, message, token), ct);
    }

    private async ValueTask AuthenticateCoreAsync(ISession session, bool capabilityOnly,
        Func<CancellationToken, ValueTask<MatchSeatBinding>> bind, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.ContainsKey(session.Id)) throw new InvalidOperationException("Duplicate match authentication.");
            if (_entries.Count >= _options.MaxConnections) throw new InvalidOperationException("Match connection capacity reached.");
            entry = new Entry(session, capabilityOnly);
            _entries.Add(session.Id, entry);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stop.Token);
        var entered = false;
        try
        {
            await entry.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            MatchSeatBinding binding;
            try { binding = await bind(linked.Token).ConfigureAwait(false); }
            catch (Exception ex) when (linked.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException("Match authentication was revoked while binding.", ex, linked.Token);
            }
            lock (entry.Sync)
            {
                if (entry.Revoked || linked.IsCancellationRequested)
                {
                    _directory.Release(session);
                    throw new OperationCanceledException("Match authentication was revoked.", linked.Token);
                }
                entry.Binding = binding;
                if (capabilityOnly && !_options.RequireCapabilitySceneReport)
                {
                    if (!_directory.IsCurrent(session, binding))
                        throw new InvalidOperationException("Match capability was revoked before its direct acknowledgement.");
                    // cn-279 sub_701270 reports game-side success immediately on relay1, without a later validation report.
                    _directory.MarkInitialized(session, binding);
                    entry.Ready = true;
                }
            }
            await _accepted.SendAsync(session, new(), linked.Token).ConfigureAwait(false);
        }
        catch
        {
            Remove(session.Id, entry);
            await CloseObservedAsync(session, "match authentication failed").ConfigureAwait(false);
            throw;
        }
        finally { if (entered) entry.Gate.Release(); }
    }

    /// <summary>Accepts scene registration only for the current authenticated account and entity.</summary>
    public ValueTask RegisterSceneAsync(ISession session, MatchSceneRegistered message, CancellationToken ct) =>
        RegisterSceneCoreAsync(session, message.EntityId, message.Nickname, message.UserNumber, false, ct);

    /// <summary>Checks native actor and display identity against the current capability lease without an invented account word.</summary>
    public ValueTask RegisterSceneIdentityAsync(ISession session, MatchSceneIdentityRegistered message, CancellationToken ct) =>
        RegisterSceneCoreAsync(session, message.EntityId, message.Nickname, null, true, ct);

    private async ValueTask RegisterSceneCoreAsync(ISession session, int entityId, string nickname, int? userNumber,
        bool capabilityOnly, CancellationToken ct)
    {
        var entry = Require(session);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stop.Token);
        var entered = false;
        try
        {
            await entry.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            var binding = RequireBinding(entry);
            if (entry.CapabilityOnly != capabilityOnly || (!capabilityOnly && userNumber != binding.UserNumber)
                || entityId != binding.EntityId || nickname != binding.Nickname)
                throw new InvalidOperationException("Match scene identity does not match the authenticated seat.");
            if (entry.Ready) throw new InvalidOperationException("Duplicate match scene registration.");
            var initialized = _initialized ?? throw new InvalidOperationException("This match dialect has no scene acknowledgement sender.");
            // Publish before the dialect's acknowledgement can report success on the independent game connection.
            lock (entry.Sync)
            {
                RequireBinding(entry);
                _directory.MarkInitialized(session, binding);
            }
            await initialized.SendAsync(session, new(binding.EntityId), linked.Token).ConfigureAwait(false);
            lock (entry.Sync)
            {
                RequireBinding(entry);
                entry.Ready = true;
            }
        }
        catch
        {
            Remove(session.Id, entry);
            await CloseObservedAsync(session, "match scene registration failed").ConfigureAwait(false);
            throw;
        }
        finally { if (entered) entry.Gate.Release(); }
    }

    /// <summary>Validates a map-naming message without allowing it to act as login or authentication.</summary>
    public async ValueTask NameMapAsync(ISession session, MatchMapNamed message, CancellationToken ct)
    {
        var entry = Require(session);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stop.Token);
        var entered = false;
        try
        {
            await entry.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            var binding = RequireBinding(entry);
            if (!entry.Ready || message.EntityId != binding.EntityId || message.Nickname != binding.Nickname || message.Map != binding.Map)
                throw new InvalidOperationException("Match map report does not match the initialized seat.");
        }
        catch
        {
            Remove(session.Id, entry);
            await CloseObservedAsync(session, "match map report failed").ConfigureAwait(false);
            throw;
        }
        finally { if (entered) entry.Gate.Release(); }
    }

    /// <summary>Revokes only transports attached to the closed connection.</summary>
    public void Disconnect(long connectionId)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.Where(e => e.Session.Connection.Id == connectionId).ToArray();
        foreach (var entry in entries) Remove(entry.Session.Id, entry);
    }

    private Entry Require(ISession session)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(session.Id, out var entry) && ReferenceEquals(entry.Session, session)
                ? entry : throw new InvalidOperationException("Match authentication is required.");
        }
    }

    private MatchSeatBinding RequireBinding(Entry entry)
    {
        lock (entry.Sync)
            return !entry.Revoked && entry.Binding is { } binding && _directory.IsCurrent(entry.Session, binding)
                ? binding : throw new InvalidOperationException("Match lease was revoked.");
    }

    private void Remove(long id, Entry expected)
    {
        lock (expected.Sync)
        {
            expected.Revoked = true;
            _directory.Release(expected.Session);
        }
        expected.Stop.Cancel();
        lock (_gate)
            if (_entries.TryGetValue(id, out var current) && current == expected) _entries.Remove(id);
    }

    private void Sweep()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            entries = _entries.Values.ToArray();
        }
        foreach (var entry in entries)
        {
            bool expired;
            lock (entry.Sync)
                expired = entry.Revoked
                    || (!entry.Ready && Environment.TickCount64 - entry.Started >= _options.HandshakeTimeoutMs)
                    || (entry.Binding is not null && !_directory.IsCurrent(entry.Session, entry.Binding));
            if (!expired) continue;
            Remove(entry.Session.Id, entry);
            _ = CloseObservedAsync(entry.Session, "match handshake expired or lease revoked");
        }
    }

    private async Task CloseObservedAsync(ISession session, string reason)
    {
        try { await session.Connection.CloseAsync(reason).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Closing match connection {Id} failed.", session.Id); }
    }

    /// <summary>Stops expiration work, cancels handlers, revokes all leases, and closes their transports.</summary>
    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }
        _timer.Dispose();
        foreach (var entry in entries)
        {
            Remove(entry.Session.Id, entry);
            _ = CloseObservedAsync(entry.Session, "match connection plugin unloading");
        }
    }
}
