using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Configuration for the <see cref="EventBus"/>.</summary>
public sealed class EventBusOptions
{
    /// <summary>
    /// Gets or sets how long removing a subscription waits for calls already running in it.
    /// The removal first stops new calls and cancels the running ones; a handler still running
    /// past this limit is reported and left behind.
    /// </summary>
    [Range(1, 600_000)]
    public int DrainTimeoutMs { get; set; } = 10_000;
}

/// <summary>
/// Default <see cref="IEventBus"/>. Handlers are stored per event type (and per result type
/// for waterfall/bail subscriptions) in priority order: higher priority first, ties in
/// registration order.
/// </summary>
/// <remarks>
/// Removing a subscription drains it: later dispatches skip it (including dispatches that
/// snapshotted it earlier), calls already inside it are cancelled through a token linked to
/// the dispatch token, and the removal returns once they have finished or the drain limit
/// passes. A handler that removes its own subscription does not wait for itself.
/// </remarks>
public sealed class EventBus : IEventBus
{
    private sealed record Key(Type EventType, Type? ResultType);

    private sealed record Frame(Entry Entry, Frame? Parent);

    private static readonly AsyncLocal<Frame?> Running = new();

    private readonly object _gate = new();
    private readonly Dictionary<Key, List<Entry>> _handlers = [];
    private readonly ILogger _logger;
    private readonly TimeSpan _drainTimeout;
    private long _sequence;

    /// <summary>Creates the bus with the default drain limit.</summary>
    /// <param name="logger">Receives handler exception and drain reports.</param>
    public EventBus(ILogger logger) : this(logger, new EventBusOptions())
    {
    }

    /// <summary>Creates the bus.</summary>
    /// <param name="logger">Receives handler exception and drain reports.</param>
    /// <param name="options">Validated bus configuration.</param>
    public EventBus(ILogger logger, EventBusOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
        _logger = logger;
        _drainTimeout = TimeSpan.FromMilliseconds(options.DrainTimeoutMs);
    }

    private IDisposable Add(Key key, Delegate handler, int priority)
    {
        lock (_gate)
        {
            var entry = new Entry(this, key, priority, ++_sequence, handler);
            if (!_handlers.TryGetValue(key, out var list))
                _handlers[key] = list = [];
            list.Add(entry);
            return entry;
        }
    }

    private void Remove(Key key, Entry entry)
    {
        lock (_gate)
            if (_handlers.TryGetValue(key, out var list))
            {
                list.Remove(entry);
                if (list.Count == 0)
                    _handlers.Remove(key);
            }
    }

    private Entry[] Snapshot(Key key)
    {
        lock (_gate)
            return _handlers.TryGetValue(key, out var list)
                ? list.OrderByDescending(e => e.Priority).ThenBy(e => e.Sequence).ToArray()
                : [];
    }

    /// <inheritdoc />
    public IDisposable On<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new Key(typeof(TEvent), null), handler, priority);
    }

    /// <inheritdoc />
    public IDisposable OnAsync<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler, int priority = 0) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new Key(typeof(TEvent), null), handler, priority);
    }

    /// <inheritdoc />
    public IDisposable Intercept<TEvent, TResult>(Func<TEvent, Func<CancellationToken, ValueTask<TResult>>, CancellationToken, ValueTask<TResult>> middleware, int priority = 0) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return Add(new Key(typeof(TEvent), typeof(TResult)), middleware, priority);
    }

    /// <inheritdoc />
    public IDisposable OnBail<TEvent, TResult>(Func<TEvent, TResult?> handler, int priority = 0)
        where TEvent : IEvent where TResult : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new Key(typeof(TEvent), typeof(TResult)), handler, priority);
    }

    /// <inheritdoc />
    public void Emit<TEvent>(TEvent e) where TEvent : IEvent
    {
        foreach (var entry in Snapshot(new Key(typeof(TEvent), null)))
        {
            if (entry.Handler is Action<TEvent> handler)
                InvokeSync(entry, handler, e);
        }
    }

    private void InvokeSync<TEvent>(Entry entry, Action<TEvent> handler, TEvent e) where TEvent : IEvent
    {
        if (!entry.TryEnter())
            return;
        var previous = Running.Value;
        Running.Value = new Frame(entry, previous);
        try
        {
            handler(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synchronous handler for event {EventType} threw; continuing.", typeof(TEvent).Name);
        }
        finally
        {
            Running.Value = previous;
            entry.Exit();
        }
    }

    private static async Task InvokeAsync<TEvent>(Entry entry, Func<TEvent, CancellationToken, ValueTask> handler, TEvent e, CancellationToken ct)
        where TEvent : IEvent
    {
        if (!entry.TryEnter())
            return;
        Running.Value = new Frame(entry, Running.Value);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stopping);
            await handler(e, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.Stopping.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Cancelled by its own removal, not by the dispatcher: the event continues without it.
        }
        finally
        {
            entry.Exit();
        }
    }

    /// <inheritdoc />
    public async ValueTask SerialAsync<TEvent>(TEvent e, CancellationToken ct) where TEvent : IEvent
    {
        foreach (var entry in Snapshot(new Key(typeof(TEvent), null)))
        {
            ct.ThrowIfCancellationRequested();
            switch (entry.Handler)
            {
                case Func<TEvent, CancellationToken, ValueTask> asyncHandler:
                    await InvokeAsync(entry, asyncHandler, e, ct).ConfigureAwait(false);
                    break;
                case Action<TEvent> syncHandler:
                    InvokeSync(entry, syncHandler, e);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask ParallelAsync<TEvent>(TEvent e, CancellationToken ct) where TEvent : IEvent
    {
        var tasks = Snapshot(new Key(typeof(TEvent), null))
            .Select(entry => entry.Handler switch
            {
                Func<TEvent, CancellationToken, ValueTask> asyncHandler => InvokeAsync(entry, asyncHandler, e, ct),
                Action<TEvent> syncHandler => Task.Run(() => InvokeSync(entry, syncHandler, e), ct),
                _ => Task.CompletedTask,
            })
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TResult> WaterfallAsync<TEvent, TResult>(TEvent e, Func<CancellationToken, ValueTask<TResult>> terminal, CancellationToken ct) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(terminal);
        Func<CancellationToken, ValueTask<TResult>> next = terminal;
        foreach (var entry in Snapshot(new Key(typeof(TEvent), typeof(TResult))).Reverse())
        {
            if (entry.Handler is not Func<TEvent, Func<CancellationToken, ValueTask<TResult>>, CancellationToken, ValueTask<TResult>> middleware)
                continue;
            var inner = next;
            next = token => InvokeMiddlewareAsync(entry, middleware, e, inner, token);
        }
        return await next(ct).ConfigureAwait(false);
    }

    private static async ValueTask<TResult> InvokeMiddlewareAsync<TEvent, TResult>(Entry entry,
        Func<TEvent, Func<CancellationToken, ValueTask<TResult>>, CancellationToken, ValueTask<TResult>> middleware,
        TEvent e, Func<CancellationToken, ValueTask<TResult>> inner, CancellationToken ct) where TEvent : IEvent
    {
        // A middleware removed after the chain was built is skipped, like a removed handler.
        if (!entry.TryEnter())
            return await inner(ct).ConfigureAwait(false);
        Running.Value = new Frame(entry, Running.Value);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stopping);
            return await middleware(e, inner, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            entry.Exit();
        }
    }

    /// <inheritdoc />
    public TResult? Bail<TEvent, TResult>(TEvent e) where TEvent : IEvent where TResult : class
    {
        foreach (var entry in Snapshot(new Key(typeof(TEvent), typeof(TResult))))
        {
            if (entry.Handler is not Func<TEvent, TResult?> handler || !entry.TryEnter())
                continue;
            var previous = Running.Value;
            Running.Value = new Frame(entry, previous);
            TResult? result;
            try
            {
                result = handler(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bail handler for event {EventType} threw; continuing.", typeof(TEvent).Name);
                continue;
            }
            finally
            {
                Running.Value = previous;
                entry.Exit();
            }
            if (result is not null)
                return result;
        }
        return null;
    }

    private static bool IsRunningHere(Entry entry)
    {
        for (var frame = Running.Value; frame is not null; frame = frame.Parent)
            if (ReferenceEquals(frame.Entry, entry))
                return true;
        return false;
    }

    private sealed class Entry(EventBus owner, Key key, int priority, long sequence, Delegate handler) : IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private int _running;
        private bool _removed;

        public int Priority { get; } = priority;
        public long Sequence { get; } = sequence;
        public Delegate Handler { get; } = handler;
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
            owner.Remove(key, this);
            _stopping.Cancel();
            // A handler that removes its own subscription (directly or through an unload it
            // triggers) is one of the running calls; waiting for it would wait for itself.
            if (wait && !IsRunningHere(this) && !_drained.Task.Wait(owner._drainTimeout))
            {
                owner._logger.LogError(
                    "Handler for event {EventType} still running {Timeout} after its removal; continuing the unload.",
                    key.EventType.Name, owner._drainTimeout);
            }
        }
    }
}
