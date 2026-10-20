namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// Owns a bounded set of asynchronous acquisitions and releases. Closing cancels admission,
/// joins unfinished acquisitions and early releases, and awaits remaining releases in reverse order.
/// </summary>
internal sealed class EffectScope : IAsyncDisposable
{
    private static readonly AsyncLocal<CallbackFrame?> CurrentCallback = new();
    private readonly object _gate;
    private readonly LinkedList<Effect> _effects = [];
    private readonly HashSet<EffectScope> _children = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Budget _budget;
    private readonly EffectScope? _parent;
    private TaskCompletionSource? _closed;
    private TaskCompletionSource? _cancellation;

    /// <summary>Creates a scope with an explicit limit on acquiring, live, releasing, and failed effects.</summary>
    public EffectScope(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _budget = new Budget(capacity);
        _gate = _budget.Gate;
        _budget.Scopes.Add(this);
        Lifetime = _lifetime.Token;
    }

    internal EffectScope(EffectScope parent)
    {
        _parent = parent;
        _budget = parent._budget;
        _gate = _budget.Gate;
        Lifetime = _lifetime.Token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_budget.Sealed || parent.IsClosingCore(), parent);
            _budget.Scopes.Add(this);
            parent._children.Add(this);
        }
    }

    internal int TotalRetainedCount { get { lock (_gate) return _budget.Retained; } }
    internal int ScopeCount { get { lock (_gate) return _budget.Scopes.Count; } }

    /// <summary>Gets the token canceled after admission closes and before owned resources release.</summary>
    public CancellationToken Lifetime { get; }

    /// <summary>Gets the number of effects still acquiring, live, releasing, or awaiting failure reporting.</summary>
    public int RetainedCount
    {
        get { lock (_gate) return _effects.Count; }
    }

    /// <summary>Gets whether admission has closed, including while asynchronous cleanup is pending.</summary>
    public bool IsClosing
    {
        get { lock (_gate) return IsClosingCore(); }
    }

    internal void EnsureCanClose() => ThrowIfInsideCallback(this, null);
    internal static bool IsInsideCallback
    {
        get
        {
            for (var frame = CurrentCallback.Value; frame is not null; frame = frame.Previous)
                if (Volatile.Read(ref frame.Active)) return true;
            return false;
        }
    }
    internal void SealAdmission() { lock (_gate) _budget.Sealed = true; }
    internal bool IsSealed { get { lock (_gate) return _budget.Sealed; } }
    internal IDisposable EnterCallback() => new CallbackFrame(this, null);

    /// <summary>
    /// Reserves ownership before calling the factory. A factory finishing after closure has its
    /// resource released before this operation fails with <see cref="ObjectDisposedException"/>.
    /// The returned handle joins one release on every call; acquisition failures belong to the caller.
    /// </summary>
    public async ValueTask<IAsyncDisposable> AcquireAsync(
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        cancellationToken.ThrowIfCancellationRequested();
        Effect effect;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_budget.Sealed || IsClosingCore(), this);
            if (_budget.Retained == _budget.Capacity)
                throw new InvalidOperationException($"The effect scope capacity of {_budget.Capacity} is exhausted.");
            effect = new Effect(this);
            effect.Node = _effects.AddLast(effect);
            _budget.Retained++;
        }

        IAsyncDisposable resource;
        try
        {
            using var callback = new CallbackFrame(this, effect);
            if (cancellationToken.CanBeCanceled)
                effect.Link = CancellationTokenSource.CreateLinkedTokenSource(Lifetime, cancellationToken);
            resource = await acquire(effect.Link?.Token ?? Lifetime).ConfigureAwait(false)
                ?? throw new InvalidOperationException("An effect factory returned no resource.");
            if (resource is IResourceHandle)
                throw new InvalidOperationException("An ownership handle cannot be acquired as a new resource.");
        }
        catch
        {
            effect.Ready.TrySetResult();
            await effect.StartRelease().ConfigureAwait(false);
            throw;
        }

        bool closing;
        bool sealedAdmission;
        bool canceled;
        lock (_gate)
        {
            effect.Resource = resource;
            effect.Ready.TrySetResult();
            closing = IsClosingCore();
            sealedAdmission = _budget.Sealed;
            canceled = cancellationToken.IsCancellationRequested;
        }
        if (!closing && !sealedAdmission && !canceled)
            return effect;

        Exception rejection = closing || sealedAdmission ? new ObjectDisposedException(nameof(EffectScope)) : new OperationCanceledException(cancellationToken);
        // Ancestor closure owns ordering; caller-only cancellation releases its reservation early.
        if (!closing) _ = Task.Run(async () => { try { await effect.DisposeAsync().ConfigureAwait(false); } catch { /* Reported through Released below. */ } });
        try { await effect.Released.Task.ConfigureAwait(false); }
        catch (Exception ex)
        {
            throw new AggregateException("A late acquisition could not be released cleanly.",
                rejection, ex);
        }
        throw rejection;
    }

    /// <summary>
    /// Closes admission once and joins complete teardown on every call. All releases are attempted;
    /// cancellation and release failures are aggregated. Calling from this scope's own acquisition
    /// or cleanup callback is rejected because waiting for that callback would wait for itself.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        ThrowIfInsideCallback(this, null);
        Effect[] effects;
        TaskCompletionSource closed;
        List<EffectScope> cancel = [];
        lock (_gate)
        {
            if (_closed is not null)
                return new ValueTask(_closed.Task);
            closed = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            effects = _effects.ToArray();
            var pending = new Stack<EffectScope>();
            pending.Push(this);
            while (pending.TryPop(out var scope))
            {
                // A canceled subtree cannot admit new descendants and was already fully visited.
                if (scope._cancellation is not null) continue;
                scope._cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancel.Add(scope);
                foreach (var child in scope._children) pending.Push(child);
            }
        }
        // Flat fan-out avoids a recursive linked-token callback chain in deep scope trees.
        foreach (var scope in cancel) _ = scope.SignalCancellationAsync();
        _ = Task.Run(() => CloseAsync(effects, closed, _cancellation!.Task));
        return new ValueTask(closed.Task);
    }

    private async Task CloseAsync(Effect[] effects, TaskCompletionSource closed, Task cancellation)
    {
        var failures = new List<Exception>();
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(OwnedWorkException.FromCleanup(ex, null)); }

        for (var i = effects.Length - 1; i >= 0; i--)
        {
            try { await effects[i].StartRelease().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
            finally { effects[i].ForgetReportedFailure(); }
        }
        lock (_gate)
        {
            _budget.Scopes.Remove(this);
            _parent?._children.Remove(this);
        }
        _lifetime.Dispose();
        if (failures.Count == 0)
            closed.TrySetResult();
        else
            closed.TrySetException(new AggregateException("The effect scope did not close cleanly.", failures));
    }

    private async Task SignalCancellationAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); _cancellation!.TrySetResult(); }
        catch (Exception ex) { _cancellation!.TrySetException(ex); }
    }

    private bool IsClosingCore()
    {
        for (var scope = this; scope is not null; scope = scope._parent)
            if (scope._closed is not null) return true;
        return false;
    }

    private bool IsDescendantOf(EffectScope ancestor)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
            if (ReferenceEquals(scope, ancestor)) return true;
        return false;
    }

    private sealed class Budget(int capacity)
    {
        public object Gate { get; } = new();
        public int Capacity { get; } = capacity;
        public int Retained;
        public bool Sealed;
        public HashSet<EffectScope> Scopes { get; } = [];
    }

    private static void ThrowIfInsideCallback(EffectScope owner, Effect? effect)
    {
        for (var frame = CurrentCallback.Value; frame is not null; frame = frame.Previous)
        {
            if (Volatile.Read(ref frame.Active) &&
                (effect is null ? frame.Owner.IsDescendantOf(owner) : ReferenceEquals(frame.Effect, effect)))
                throw new InvalidOperationException("An effect callback cannot await its own release or its owning scope's closure.");
        }
    }

    private sealed class CallbackFrame : IDisposable
    {
        public readonly EffectScope Owner;
        public readonly Effect? Effect;
        public readonly CallbackFrame? Previous;
        public bool Active = true;

        public CallbackFrame(EffectScope owner, Effect? effect)
        {
            Owner = owner;
            Effect = effect;
            Previous = CurrentCallback.Value;
            CurrentCallback.Value = this;
        }

        public void Dispose()
        {
            // A task that captured this context may outlive the callback without remaining inside it.
            Volatile.Write(ref Active, false);
            CurrentCallback.Value = Previous;
        }
    }

    private sealed class Effect(EffectScope owner) : IAsyncDisposable, IResourceHandle
    {
        private EffectScope? _owner = owner;
        private bool _releasing;
        public LinkedListNode<Effect>? Node { get; set; }
        public IAsyncDisposable? Resource { get; set; }
        public CancellationTokenSource? Link { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() => StartRelease(fromCloser: false);

        public ValueTask StartRelease(bool fromCloser = true)
        {
            if (Released.Task.IsCompleted)
                return new ValueTask(Released.Task);
            var currentOwner = Volatile.Read(ref _owner);
            if (currentOwner is null)
                return new ValueTask(Released.Task);
            ThrowIfInsideCallback(currentOwner, this);
            var start = false;
            lock (currentOwner._gate)
            {
                if (!fromCloser && currentOwner.IsClosingCore())
                {
                    // Closure owns ordering. A callback cannot wait for an entry not yet reached.
                    ThrowIfInsideCallback(currentOwner, null);
                }
                else if (!_releasing)
                    start = _releasing = true;
            }
            if (start)
                _ = ReleaseAsync(currentOwner);
            return new ValueTask(Released.Task);
        }

        private async Task ReleaseAsync(EffectScope currentOwner)
        {
            Exception? failure = null;
            IAsyncDisposable? resource = null;
            try
            {
                await Ready.Task.ConfigureAwait(false);
                resource = Resource;
                Resource = null;
                if (resource is not null)
                {
                    using var callback = new CallbackFrame(currentOwner, this);
                    await resource.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { failure = OwnedWorkException.FromCleanup(ex, resource); }
            finally
            {
                Link?.Dispose();
                Link = null;
                lock (currentOwner._gate)
                {
                    // A failed release occupies its slot until closure reports it; it is never retried.
                    if (failure is null) Detach(currentOwner);
                    // Publication stays atomic with detachment so closure cannot miss completed work.
                    if (resource is IResourceReleaseObserver observer) observer.OnReleaseSettled();
                    if (failure is null) Released.TrySetResult();
                    else Released.TrySetException(failure);
                }
            }
        }

        public void ForgetReportedFailure()
        {
            var currentOwner = Volatile.Read(ref _owner);
            if (currentOwner is null) return;
            lock (currentOwner._gate)
            {
                if (Node is not null && Released.Task.IsCompleted)
                    Detach(currentOwner);
            }
        }

        private void Detach(EffectScope currentOwner)
        {
            currentOwner._effects.Remove(Node!);
            currentOwner._budget.Retained--;
            Node = null;
            Volatile.Write(ref _owner, null);
        }
    }
}
