namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// <see cref="ServiceRegistry"/> half that runs the two-phase publication handshake: a row stages its
/// publication before registering contributions, then commits them in one gate-held swap, with waiter
/// callbacks deferred to after the swap.
/// </summary>
public sealed partial class ServiceRegistry
{
    /// <summary>
    /// Opens the owner's publication for registration. Must be called before the owner contributes any
    /// registration; an owner that registered first has already been observed by readers and cannot
    /// publish atomically, so this throws instead of reordering.
    /// </summary>
    internal void StageOwner(ServiceActivation owner)
    {
        Runtime.RequireOwner(owner.Runtime);
        lock (_gate)
        {
            if (AllRegistrations().Any(entry => entry.BaseBinding.Registration.Provider == owner) ||
                _services.Values.Any(entry => entry.Layers.Values.Any(layer => layer.Registration.Provider == owner)))
                throw new InvalidOperationException("An owner must stage publication before registering contributions.");
            owner.Publication.Begin();
        }
    }

    /// <summary>
    /// Commits the owner's staged publication and returns the announcement function. The swap and the
    /// capture of waiting resolvers happen under the registry gate; the announcement itself runs after
    /// the swap and outside it, so a waiter callback cannot reenter a half-published registry. The
    /// announcement raises <see cref="BindingChanged"/> first, then runs every waiter and aggregates
    /// their errors instead of throwing, so one failing callback cannot skip the others.
    /// </summary>
    internal Func<IReadOnlyList<Exception>> CommitOwner(ServiceActivation owner)
    {
        Runtime.RequireOwner(owner.Runtime);
        var callbacks = new List<Action>();
        ServiceKey[] changed;
        lock (_gate)
        {
            owner.Publication.EnsureOpen();
            if (owner.Publication.IsReady) throw new InvalidOperationException("The owner is not awaiting publication.");
            var entries = AllRegistrations().Where(entry => entry.BaseBinding.Registration.Provider == owner ||
                entry.Layers.Values.Any(layer => layer.Registration.Provider == owner)).ToArray();
            var projections = new List<(Registration Entry, ServiceBinding Binding)>();
            foreach (var entry in entries)
                if (entry.Chain is { } chain && entry.Layers.Count > 0)
                {
                    var layers = entry.Layers.Values.Where(layer => layer.Registration.Provider == owner ||
                        layer.Registration.Provider.Publication.IsReady).ToArray();
                    projections.Add((entry, entry.Binding.WithDecorations(chain.CaptureReference(owner.Publication), layers)));
                }
            foreach (var projection in projections) projection.Entry.Binding = projection.Binding;
            owner.Publication.Commit();
            changed = entries.Select(entry => entry.Key).Distinct().ToArray();
            foreach (var entry in entries.Where(entry => entry.BaseBinding.Registration.Provider == owner))
                if (_waiters.Remove(entry.Key, out var waiters))
                    foreach (var waiter in waiters) callbacks.Add(() => waiter.Invoke(entry.Instance));
        }
        return () =>
        {
            var errors = new List<Exception>();
            foreach (var key in changed) BindingChanged?.Invoke(key);
            foreach (var callback in callbacks)
                try { callback(); } catch (Exception error) { errors.Add(error); }
            return errors.AsReadOnly();
        };
    }
}
