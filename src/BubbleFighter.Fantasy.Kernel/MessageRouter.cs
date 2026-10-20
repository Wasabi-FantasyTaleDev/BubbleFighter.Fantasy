using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks.Sources;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Provides the replaceable typed message pipeline as an ordinary profile row.</summary>
[Plugin("core.message-router", Description = "Typed message handlers and middleware.", Provides = [typeof(IMessageRouter)])]
public sealed class MessageRouterPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MessageRouterOptions>();
        context.Effect(context.Services.Register<IMessageRouter>(new MessageRouter(context.Events, context.Logger, options)));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Deployment bounds on typed handler and middleware registrations.</summary>
public sealed class MessageRouterOptions
{
    /// <summary>Gets or sets the total number of live base, override and middleware registrations.</summary>
    [Range(1, 65536)]
    public int MaxRegistrations { get; set; } = 4096;
    /// <summary>Gets or sets the maximum middleware or override depth for any one message route.</summary>
    [Range(1, 256)]
    public int MaxPipelineDepth { get; set; } = 64;
    /// <summary>
    /// Gets or sets how long removing a handler or middleware waits for calls already running in
    /// it. Unload has already cancelled them through the owner's lifetime; a call still running
    /// past this limit is reported and left behind.
    /// </summary>
    [Range(1, 600_000)]
    public int DrainTimeoutMs { get; set; } = 10_000;
}

internal sealed class MessageRouter(IEventBus events, ILogger logger, MessageRouterOptions options) : IMessageRouter
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Protocol, Type Type), Route> _routes = [];
    private readonly int _capacity = options.MaxRegistrations;
    private readonly int _depth = options.MaxPipelineDepth;
    private readonly TimeSpan _drainTimeout = TimeSpan.FromMilliseconds(options.DrainTimeoutMs);
    private static readonly AsyncLocal<Entry[]?> Running = new();
    private int _count;
    private long _sequence;

    private sealed class Route
    {
        public List<Entry> Handlers { get; } = [];
        public List<Entry> Middleware { get; } = [];
    }

    private sealed class Entry(MessageRouter router, (string Protocol, Type Type) key, IPluginContext owner,
        Delegate callback, long id, bool middleware, int priority, string? replaces) : IDisposable
    {
        public IPluginContext Owner { get; } = owner;
        public Delegate Callback { get; } = callback;
        public long Id { get; } = id;
        public int Priority { get; } = priority;
        public string? Replaces { get; } = replaces;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private bool _removed;

        // Called under the router gate while the entry is still listed, so it always succeeds.
        public void Enter() => Interlocked.Increment(ref _running);

        public void Exit()
        {
            if (Interlocked.Decrement(ref _running) == 0 && Volatile.Read(ref _removed))
                _drained.TrySetResult();
        }

        /// <summary>Unlists the entry, then waits for calls already inside it within the drain limit.</summary>
        public void Dispose()
        {
            router.Remove(key, this, middleware);
            if (Volatile.Read(ref _removed))
                return;
            Volatile.Write(ref _removed, true);
            if (Volatile.Read(ref _running) == 0)
            {
                _drained.TrySetResult();
                return;
            }
            // A call that removes its own entry would otherwise wait for itself.
            if (Running.Value is { } current && Array.IndexOf(current, this) >= 0)
                return;
            if (!_drained.Task.Wait(router._drainTimeout))
                router.ReportUndrained(key, Owner.Row.Id);
        }
    }

    private void ReportUndrained((string Protocol, Type Type) key, string rowId) =>
        logger.LogError("Typed {Message} handler of row '{Row}' on {Protocol} still running after its removal; continuing the unload.",
            key.Type.FullName, rowId, key.Protocol);

    private sealed class Scoped(MessageRouter router, IPluginContext owner) : IMessageRoutes
    {
        public IDisposable On<T>(string protocol, MessageHandler<T> handler) where T : IMessage =>
            router.Add<T>(protocol, owner, handler, false, 0, null);
        public IDisposable Override<T>(string protocol, string targetRowId, MessageHandler<T> handler) where T : IMessage =>
            router.Add<T>(protocol, owner, handler, false, 0, targetRowId);
        public IDisposable Use<T>(string protocol, MessageMiddleware<T> middleware, int priority = 0) where T : IMessage =>
            router.Add<T>(protocol, owner, middleware, true, priority, null);
    }

    public IMessageRoutes For(IPluginContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Lifetime.ThrowIfCancellationRequested();
        return new Scoped(this, owner);
    }

    public IReadOnlyList<MessageHandlerRegistration> Handlers
    {
        get
        {
            lock (_gate) return _routes.SelectMany(pair => pair.Value.Handlers.Select(entry =>
                new MessageHandlerRegistration(entry.Id, entry.Owner.Row.Id, pair.Key.Protocol, pair.Key.Type, entry.Replaces))).ToArray();
        }
    }

    private IDisposable Add<T>(string protocol, IPluginContext owner, Delegate callback, bool middleware, int priority, string? target) where T : IMessage
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(callback);
        if (protocol.Length > 128) throw new ArgumentException("Protocol names are limited to 128 characters.", nameof(protocol));
        if (typeof(T).IsInterface || typeof(T).IsAbstract || typeof(T).ContainsGenericParameters)
            throw new ArgumentException("Message routes require a concrete, closed message contract.");
        var key = (protocol, typeof(T));
        var declaration = target is null ? null : $"{target}:{typeof(T).FullName}";
        lock (_gate)
        {
            owner.Lifetime.ThrowIfCancellationRequested();
            if (_count >= _capacity) throw new InvalidOperationException($"Message router reached max_registrations={_capacity}.");
            _routes.TryGetValue(key, out var existing);
            var route = existing ?? new Route();
            var list = middleware ? route.Middleware : route.Handlers;
            if (list.Count >= _depth) throw new InvalidOperationException($"Message route '{protocol}:{typeof(T).FullName}' reached max_pipeline_depth={_depth}.");
            if (!middleware)
            {
                if (target is null && route.Handlers.Count != 0)
                    throw new InvalidOperationException($"A typed handler already exists for '{protocol}:{typeof(T).FullName}'. Declare an explicit override.");
                if (target is not null)
                {
                    if (!owner.OverrideTargets.Contains(declaration!, StringComparer.Ordinal))
                        throw new InvalidOperationException($"Row '{owner.Row.Id}' has no Overrides declaration for '{declaration}'.");
                    if (route.Handlers.Count == 0 || route.Handlers[^1].Owner.Row.Id != target)
                        throw new InvalidOperationException($"Override target '{declaration}' is not the currently selected handler for '{protocol}'.");
                    if (target == owner.Row.Id || route.Handlers.Any(entry => entry.Owner.Row.Id == owner.Row.Id))
                        throw new InvalidOperationException("A row cannot override itself or occur twice in one handler chain.");
                }
            }
            var entry = new Entry(this, key, owner, callback, ++_sequence, middleware, priority, declaration);
            list.Add(entry);
            _routes[key] = route;
            _count++;
            return entry;
        }
    }

    private void Remove((string Protocol, Type Type) key, Entry entry, bool middleware)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(key, out var route)) return;
            var list = middleware ? route.Middleware : route.Handlers;
            var index = list.IndexOf(entry);
            if (index < 0) return;
            // A removed target invalidates all replacements based on that registration.
            var removed = middleware ? 1 : list.Count - index;
            list.RemoveRange(index, removed);
            _count -= removed;
            if (route.Handlers.Count == 0 && route.Middleware.Count == 0) _routes.Remove(key);
        }
    }

    public async ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();
        Entry? selected;
        Entry[] middleware;
        lock (_gate)
        {
            _routes.TryGetValue((protocol, typeof(T)), out var route);
            selected = route?.Handlers.LastOrDefault();
            middleware = route?.Middleware.OrderByDescending(entry => entry.Priority).ThenBy(entry => entry.Id).ToArray() ?? [];
            selected?.Enter();
            foreach (var entry in middleware)
                entry.Enter();
        }
        var entered = selected is null ? middleware : [.. middleware, selected];
        var previous = Running.Value;
        Running.Value = previous is null ? entered : [.. previous, .. entered];
        try
        {
            return await DispatchEnteredAsync(protocol, session, message, selected, middleware, ct).ConfigureAwait(false);
        }
        finally
        {
            Running.Value = previous;
            foreach (var entry in entered)
                entry.Exit();
        }
    }

    private async ValueTask<bool> DispatchEnteredAsync<T>(string protocol, ISession session, T message, Entry? selected,
        Entry[] middleware, CancellationToken ct) where T : IMessage
    {
        if (selected is null)
        {
            logger.LogWarning("No typed handler for {Protocol}:{Message} on session {SessionId}.", protocol, typeof(T).FullName, session.Id);
            events.Emit(new MessageUnhandledEvent(session, protocol, typeof(T)));
            return false;
        }
        MessageHandler<T> pipeline = async (s, m, token) =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, selected.Owner.Lifetime);
            lifetime.Token.ThrowIfCancellationRequested();
            await ((MessageHandler<T>)selected.Callback)(s, m, lifetime.Token).ConfigureAwait(false);
        };
        for (var i = middleware.Length - 1; i >= 0; i--)
        {
            var entry = middleware[i];
            var next = pipeline;
            pipeline = (s, m, token) => InvokeMiddleware(entry, next, s, m, token);
        }
        await pipeline(session, message, ct).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask InvokeMiddleware<T>(Entry entry, MessageHandler<T> next, ISession session, T message, CancellationToken ct) where T : IMessage
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Owner.Lifetime);
        lifetime.Token.ThrowIfCancellationRequested();
        var gate = new object();
        var closed = false;
        ObservedContinuation? continuation = null;
        MessageHandler<T> guarded = (s, m, token) =>
        {
            ObservedContinuation invocation;
            lock (gate)
            {
                if (closed || continuation is not null)
                    throw new InvalidOperationException("Message middleware next is single-use and cannot escape its invocation.");
                invocation = continuation = new ObservedContinuation();
            }
            // Never execute user code while holding the continuation admission lock.
            invocation.Run(async () =>
            {
                using var downstreamLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                downstreamLifetime.Token.ThrowIfCancellationRequested();
                await next(s, m, downstreamLifetime.Token).ConfigureAwait(false);
            });
            return new ValueTask(invocation, 0);
        };
        try { await ((MessageMiddleware<T>)entry.Callback)(session, message, guarded, lifetime.Token).ConfigureAwait(false); }
        finally
        {
            ObservedContinuation? pending;
            lock (gate) { closed = true; pending = continuation; }
            // Drain forgotten awaits, but do not rethrow a failure already observed and handled by middleware.
            if (pending is not null && !pending.Observed) await pending.Completion.ConfigureAwait(false);
        }
    }

    private sealed class ObservedContinuation : IValueTaskSource
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _observed;
        public Task Completion => _completion.Task;
        public bool Observed => Volatile.Read(ref _observed) != 0;

        public void Run(Func<ValueTask> next) => _ = CompleteAsync(next);

        private async Task CompleteAsync(Func<ValueTask> next)
        {
            try { await next().ConfigureAwait(false); _completion.TrySetResult(); }
            catch (OperationCanceledException ex) { _completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { _completion.TrySetException(ex); }
        }

        public void GetResult(short token)
        {
            if (!Completion.IsCompleted) throw new InvalidOperationException("Await the middleware continuation before reading its result.");
            if (Interlocked.Exchange(ref _observed, 1) != 0) throw new InvalidOperationException("A middleware continuation may only be awaited once.");
            Completion.GetAwaiter().GetResult();
        }

        public ValueTaskSourceStatus GetStatus(short token) => !Completion.IsCompleted ? ValueTaskSourceStatus.Pending
            : Completion.IsCanceled ? ValueTaskSourceStatus.Canceled
            : Completion.IsFaulted ? ValueTaskSourceStatus.Faulted : ValueTaskSourceStatus.Succeeded;

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            var awaiter = Completion.ConfigureAwait((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0).GetAwaiter();
            if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
                awaiter.OnCompleted(() => continuation(state));
            else
                awaiter.UnsafeOnCompleted(() => continuation(state));
        }
    }
}
