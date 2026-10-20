using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// Kernel built-in plugin providing the default <see cref="IConnectionProtocolRegistry"/>.
/// Appears as row <c>core.protocol-registry</c> so profiles can replace it.
/// </summary>
[Plugin("core.protocol-registry", Description = "Named connection protocol registry.", Provides = [typeof(IConnectionProtocolRegistry)])]
public sealed class ConnectionProtocolRegistryPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Register<IConnectionProtocolRegistry>(new ConnectionProtocolRegistry()));
        return ValueTask.CompletedTask;
    }

    private sealed class ConnectionProtocolRegistry : IConnectionProtocolRegistry
    {
        private readonly ConcurrentDictionary<string, IConnectionProtocol> _protocols = new(StringComparer.Ordinal);

        public IDisposable Register(IConnectionProtocol protocol)
        {
            ArgumentNullException.ThrowIfNull(protocol);
            if (!_protocols.TryAdd(protocol.Name, protocol))
                throw new InvalidOperationException($"A connection protocol named '{protocol.Name}' is already registered.");
            return new Registration(this, protocol.Name);
        }

        public IConnectionProtocol Resolve(string name) =>
            _protocols.TryGetValue(name, out var protocol)
                ? protocol
                : throw new InvalidOperationException($"No connection protocol registered under name '{name}'.");

        private sealed class Registration(ConnectionProtocolRegistry owner, string name) : IDisposable
        {
            public void Dispose() => owner._protocols.TryRemove(name, out _);
        }
    }
}

/// <summary>Configuration for the <see cref="PacketRouterPlugin"/> row.</summary>
public sealed class PacketRouterOptions
{
    /// <summary>
    /// Gets or sets how long removing a handler waits for calls already running in it. The
    /// removal first stops new calls and cancels the running ones; a handler that ignores
    /// cancellation past this limit is reported and left behind.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(1, 600_000)]
    public int DrainTimeoutMs { get; set; } = 10_000;
}

/// <summary>
/// Kernel built-in plugin providing the default <see cref="IPacketRouter"/>. Appears as row
/// <c>core.packet-router</c> so profiles can replace it.
/// </summary>
/// <remarks>
/// Removing a registration drains it: new packets stop reaching the handler, the calls already
/// inside it are cancelled, and the removal returns once they have finished. A plugin row
/// registers its handlers after the services they use, and unload releases effects in reverse,
/// so a handler has finished before anything it depends on is released.
/// </remarks>
[Plugin("core.packet-router", Description = "Packet handler router keyed by protocol name and opcode.", Provides = [typeof(IPacketRouter)])]
public sealed class PacketRouterPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<PacketRouterOptions>();
        var router = new PacketRouter(context.Events, TimeSpan.FromMilliseconds(options.DrainTimeoutMs), context.Logger);
        context.Effect(context.Services.Register<IPacketRouter>(router));
        return ValueTask.CompletedTask;
    }

    /// <summary>The default router; internal so kernel tests can drive the drain directly.</summary>
    internal sealed class PacketRouter(IEventBus events, TimeSpan drainTimeout, ILogger logger) : IPacketRouter
    {
        private static readonly AsyncLocal<Registration?> Current = new();
        private readonly ConcurrentDictionary<(string Protocol, ushort Opcode), Registration> _handlers = new();

        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var key = (protocolName, opcode);
            var registration = new Registration(this, key, handler);
            if (!_handlers.TryAdd(key, registration))
            {
                registration.Release();
                throw new InvalidOperationException($"A handler is already registered for '{protocolName}' opcode {opcode}.");
            }
            return registration;
        }

        public async ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct)
        {
            var found = _handlers.TryGetValue((protocolName, packet.Opcode), out var registration)
                && registration.TryEnter();
            var previous = Current.Value;
            Current.Value = registration;
            try
            {
                // Await plugin observers before accepting the next frame, so asynchronous forwarding
                // preserves connection order and cannot outlive a reused transport buffer.
                await events.SerialAsync(new PacketReceivedEvent(session, protocolName, packet, found), ct).ConfigureAwait(false);
                if (!found)
                    return;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, registration!.Stopping);
                await registration.Handler(session, packet, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                Current.Value = previous;
                if (found) registration!.Exit();
            }
        }

        internal sealed class Registration(PacketRouter owner, (string Protocol, ushort Opcode) key, PacketHandler handler) : IDisposable
        {
            private readonly PacketRouter _owner = owner;
            private readonly CancellationTokenSource _stopping = new();
            private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Lock _gate = new();
            private int _running;
            private bool _removed;

            public PacketHandler Handler { get; } = handler;

            public CancellationToken Stopping => _stopping.Token;

            public bool TryEnter()
            {
                lock (_gate)
                {
                    if (_removed)
                        return false;
                    _running++;
                    return true;
                }
            }

            public void Exit()
            {
                lock (_gate)
                {
                    if (--_running == 0 && _removed)
                        _drained.TrySetResult();
                }
            }

            /// <summary>Stops new calls, cancels running ones, and waits for them within the drain limit.</summary>
            public void Dispose()
            {
                bool wait;
                lock (_gate)
                {
                    if (_removed)
                        return;
                    _removed = true;
                    wait = _running > 0;
                    if (!wait)
                        _drained.TrySetResult();
                }
                _owner._handlers.TryRemove(new KeyValuePair<(string, ushort), Registration>(key, this));
                _stopping.Cancel();
                // A handler that removes its own registration (directly or through an unload it
                // triggers) is one of the running calls; waiting for it would wait for itself.
                if (wait && !ReferenceEquals(Current.Value, this) && !_drained.Task.Wait(_owner.DrainTimeout))
                {
                    _owner.Logger.LogError(
                        "Handler for '{Protocol}' opcode {Opcode} still running {Timeout} after its removal; continuing the unload.",
                        key.Protocol, key.Opcode, _owner.DrainTimeout);
                }
            }

            public void Release() => _stopping.Dispose();
        }

        internal TimeSpan DrainTimeout => drainTimeout;

        internal ILogger Logger => logger;
    }
}

/// <summary>
/// Kernel built-in plugin providing the default <see cref="ISessionRegistry"/>. Appears as
/// row <c>core.session-registry</c> so profiles can replace it.
/// </summary>
[Plugin("core.session-registry", Description = "Registry of live protocol sessions.", Provides = [typeof(ISessionRegistry)])]
public sealed class SessionRegistryPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Register<ISessionRegistry>(new SessionRegistry()));
        return ValueTask.CompletedTask;
    }

    private sealed class SessionRegistry : ISessionRegistry
    {
        private readonly ConcurrentDictionary<long, ISession> _sessions = new();

        public void Add(ISession session) => _sessions[session.Id] = session;

        public void Remove(ISession session) => _sessions.TryRemove(session.Id, out _);

        public IReadOnlyList<ISession> All => _sessions.Values.ToList();
    }
}

/// <summary>
/// Kernel built-in plugin providing the default <see cref="IClientProfileRegistry"/>.
/// Appears as row <c>core.client-profiles</c> so profiles can replace it.
/// </summary>
[Plugin("core.client-profiles", Description = "Registry of client profiles served by this deployment.", Provides = [typeof(IClientProfileRegistry)])]
public sealed class ClientProfileRegistryPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Register<IClientProfileRegistry>(new ClientProfileRegistry()));
        return ValueTask.CompletedTask;
    }

    private sealed class ClientProfileRegistry : IClientProfileRegistry
    {
        private readonly ConcurrentDictionary<string, ClientProfile> _profiles = new(StringComparer.Ordinal);

        public IDisposable Register(ClientProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (!_profiles.TryAdd(profile.Id, profile))
                throw new InvalidOperationException($"A client profile named '{profile.Id}' is already registered.");
            return new Registration(this, profile.Id);
        }

        public IReadOnlyList<ClientProfile> All => _profiles.Values.ToList();

        public ClientProfile Get(string id) =>
            _profiles.TryGetValue(id, out var profile)
                ? profile
                : throw new InvalidOperationException($"No client profile registered under id '{id}'.");

        private sealed class Registration(ClientProfileRegistry owner, string id) : IDisposable
        {
            public void Dispose() => owner._profiles.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Kernel built-in plugin providing the default <see cref="IOpcodeTableRegistry"/>.
/// Appears as row <c>core.opcode-tables</c> so profiles can replace it.
/// </summary>
[Plugin("core.opcode-tables", Description = "Registry of opcode tables keyed by client profile id.", Provides = [typeof(IOpcodeTableRegistry)])]
public sealed class OpcodeTableRegistryPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Register<IOpcodeTableRegistry>(new OpcodeTableRegistry()));
        return ValueTask.CompletedTask;
    }

    private sealed class OpcodeTableRegistry : IOpcodeTableRegistry
    {
        private readonly ConcurrentDictionary<string, IOpcodeTable> _tables = new(StringComparer.Ordinal);

        public IDisposable Register(IOpcodeTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (!_tables.TryAdd(table.Profile.Id, table))
                throw new InvalidOperationException($"An opcode table is already registered for profile '{table.Profile.Id}'.");
            return new Registration(this, table.Profile.Id);
        }

        public IReadOnlyList<IOpcodeTable> All => _tables.Values.ToList();

        public IOpcodeTable Get(string profileId) =>
            _tables.TryGetValue(profileId, out var table)
                ? table
                : throw new InvalidOperationException($"No opcode table registered for profile '{profileId}'.");

        public bool TryGet(string profileId, out IOpcodeTable? table)
        {
            var found = _tables.TryGetValue(profileId, out var resolved);
            table = resolved;
            return found;
        }

        private sealed class Registration(OpcodeTableRegistry owner, string id) : IDisposable
        {
            public void Dispose() => owner._tables.TryRemove(id, out _);
        }
    }
}
