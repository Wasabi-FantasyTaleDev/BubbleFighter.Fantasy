using System.Reflection;
using System.Runtime.ExceptionServices;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>The mutation surface of a registered service that supports decorators.</summary>
internal interface IDecoratableService
{
    /// <summary>Gets the live proxy: it dispatches to the base instance and every committed decorator layer, ordered by priority.</summary>
    object Reference { get; }
    /// <summary>Gets the ids of the committed decorator layers, highest priority first.</summary>
    IReadOnlyList<long> DecorationIds { get; }
    /// <summary>Returns a proxy frozen to the layers the given publication state shows, for a committing or retiring owner to capture.</summary>
    object CaptureReference(ActivationPublication? committing = null, ActivationPublication? excluding = null);
    /// <summary>Stages one layer's removal without publishing it; <see cref="IDecoratableChange.Commit"/> swaps it in.</summary>
    IDecoratableChange PrepareRemove(long id);
    /// <summary>Closes the service: further resolutions throw <see cref="ServiceUnavailableException"/>.</summary>
    void Close();
}

/// <summary>A staged layer mutation that publishes on <see cref="Commit"/> or vanishes on <see cref="IDisposable.Dispose"/>.</summary>
internal interface IDecoratableChange : IDisposable
{
    /// <summary>Gets a proxy fixed to the staged layers, callable before the change is committed.</summary>
    object Reference { get; }
    /// <summary>Publishes the staged snapshot atomically under the registry gate.</summary>
    void Commit();
}

/// <summary>
/// An ordered decorator chain over one registered service. The base instance plus every committed
/// layer is reachable through <see cref="Reference"/>, a DispatchProxy that walks the layers in
/// priority order per call. Layers added by an activation stay invisible to other consumers until
/// that activation's publication commits; layers of a retiring owner are excluded from fresh
/// captures before the retirement lands.
/// </summary>
/// <typeparam name="TService">The closed service interface being decorated.</typeparam>
internal sealed class DecoratableService<TService> : IDecoratableService where TService : class
{
    private sealed record Layer(long Id, int Priority, TService Instance, ActivationPublication? Publication);
    private sealed record Snapshot(TService Base, Layer[] Layers);
    private readonly object _gate = new();
    private readonly AsyncLocal<Snapshot?> _invocation = new();
    private Snapshot? _current;
    private bool _creating;

    public DecoratableService(TService instance)
    {
        ValidateInterface();
        _current = new Snapshot(instance, []);
        Reference = MakeProxy(null);
    }

    public object Reference { get; }

    public IReadOnlyList<long> DecorationIds
    {
        get
        {
            lock (_gate)
                return _current?.Layers.Select(layer => layer.Id).ToArray() ?? [];
        }
    }

    public IDisposable Add(Func<TService, TService> factory, int priority, long id)
    {
        using var change = PrepareAdd(factory, priority, id);
        change.Commit();
        return new Removal(this, id);
    }

    internal IDecoratableChange PrepareAdd(Func<TService, TService> factory, int priority, long id, ActivationPublication? publication = null)
    {
        Snapshot current;
        lock (_gate)
        {
            current = _current ?? throw new ServiceUnavailableException(typeof(TService));
            // The factory runs outside the lock because it may resolve services or register effects;
            // this flag re-enters only to keep the chain from mutating itself, not to hold the lock.
            if (_creating)
                throw new InvalidOperationException("Decorator factories cannot mutate their own chain.");
            _creating = true;
        }
        try
        {
            var next = MakeProxy(id);
            var instance = factory(next) ?? throw new InvalidOperationException("A decorator factory returned null.");
            if (ReferenceEquals(instance, Reference) || ReferenceEquals(instance, next))
                throw new InvalidOperationException("A decorator factory must return a wrapper, not a forwarding reference.");
            return new Change(this, new Snapshot(current.Base, current.Layers.Append(new Layer(id, priority, instance, publication))
                .OrderByDescending(layer => layer.Priority).ThenBy(layer => layer.Id).ToArray()));
        }
        catch { lock (_gate) _creating = false; throw; }
    }

    public object CaptureReference(ActivationPublication? committing = null, ActivationPublication? excluding = null)
    {
        lock (_gate) return MakeProxy(null, Visible(_current ?? throw new ServiceUnavailableException(typeof(TService)), committing, excluding));
    }

    // A fresh resolve sees committed layers only; the committing owner also sees the layer it is
    // publishing (its proxy is captured inside the same commit), and a retiring owner's layer drops
    // out of new captures before Retire() rewrites the bindings.
    private static Snapshot Visible(Snapshot snapshot, ActivationPublication? committing = null, ActivationPublication? excluding = null) =>
        new(snapshot.Base, snapshot.Layers.Where(layer => (excluding is null || !ReferenceEquals(layer.Publication, excluding)) &&
            (layer.Publication is null || layer.Publication.IsReady || ReferenceEquals(layer.Publication, committing))).ToArray());

    public void Close()
    {
        lock (_gate)
        {
            if (_creating)
                throw new InvalidOperationException("Decorator factories cannot close their own service.");
            Volatile.Write(ref _current, null);
        }
    }

    private void Remove(long id)
    {
        lock (_gate)
        {
            if (_current is null) return;
            using var change = PrepareRemove(id);
            change.Commit();
        }
    }

    public IDecoratableChange PrepareRemove(long id)
    {
        lock (_gate)
        {
            if (_creating) throw new InvalidOperationException("Decorator factories cannot mutate their own chain.");
            var current = _current ?? throw new ServiceUnavailableException(typeof(TService));
            var change = new Change(this, new Snapshot(current.Base, current.Layers.Where(layer => layer.Id != id).ToArray()));
            _creating = true;
            return change;
        }
    }

    private TService MakeProxy(long? after, Snapshot? fixedSnapshot = null)
    {
        var proxy = DispatchProxy.Create<TService, Forwarder>();
        ((Forwarder)(object)proxy).InvokeTarget = (method, args) => Invoke(after, method, args, fixedSnapshot);
        return proxy;
    }

    private object? Invoke(long? after, MethodInfo method, object?[]? args, Snapshot? fixedSnapshot)
    {
        // The chain a decorator calls through is fixed at invocation entry: the AsyncLocal snapshot
        // keeps a nested inner call on the same layers even if another thread commits a change
        // mid-call, and an inner reference used outside any invocation has no chain to walk.
        var previous = _invocation.Value;
        if (after is not null && previous is null)
            throw new InvalidOperationException("A decorator's inner reference is only valid inside a service invocation.");
        var snapshot = after is null ? fixedSnapshot ?? Volatile.Read(ref _current) : previous;
        if (snapshot is null)
            throw new ServiceUnavailableException(typeof(TService));
        if (after is null && fixedSnapshot is null) snapshot = Visible(snapshot);
        var index = -1;
        if (after is { } id)
        {
            index = Array.FindIndex(snapshot.Layers, layer => layer.Id == id);
            if (index < 0)
                throw new InvalidOperationException("A decorator may invoke its inner reference only while its registered wrapper is being called.");
        }
        var target = index + 1 < snapshot.Layers.Length ? snapshot.Layers[index + 1].Instance : snapshot.Base;
        _invocation.Value = snapshot;
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        finally { _invocation.Value = previous; }
    }

    private static void ValidateInterface()
    {
        var type = typeof(TService);
        if (!type.IsInterface || type.ContainsGenericParameters)
            throw new ArgumentException($"Decoratable service '{type.FullName}' must be a closed interface.");
        foreach (var contract in type.GetInterfaces().Append(type))
            foreach (var method in contract.GetMethods())
            {
                if (method.IsStatic || method.ReturnType.IsByRef || Unsupported(method.ReturnType)
                    || method.GetParameters().Any(parameter => Unsupported(parameter.ParameterType)))
                    throw new NotSupportedException($"Decoratable service '{type.FullName}' has unsupported method '{method.Name}'. Use ordinary registration for static, pointer, byref-like or ref-return contracts.");
            }
    }

    private static bool Unsupported(Type type) => type.IsByRefLike || type.IsPointer || type.IsFunctionPointer
        || (type.HasElementType && Unsupported(type.GetElementType()!));

    private sealed class Change : IDecoratableChange
    {
        private readonly DecoratableService<TService> _owner;
        private readonly Snapshot _snapshot;
        private bool _settled;
        public Change(DecoratableService<TService> owner, Snapshot snapshot)
        {
            _owner = owner;
            _snapshot = snapshot;
            Reference = owner.MakeProxy(null, Visible(snapshot));
        }
        public object Reference { get; }
        public void Commit()
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_settled, this);
                Volatile.Write(ref _owner._current, _snapshot);
                _owner._creating = false;
                _settled = true;
            }
        }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_settled) return;
                _owner._creating = false;
                _settled = true;
            }
        }
    }

    private sealed class Removal(DecoratableService<TService> owner, long id) : IDisposable
    {
        public void Dispose() => owner.Remove(id);
    }

    // DispatchProxy derives its generated implementation from this non-sealed type.
    public class Forwarder : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> InvokeTarget { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            InvokeTarget(targetMethod ?? throw new InvalidOperationException("Missing proxy method."), args);
    }
}
