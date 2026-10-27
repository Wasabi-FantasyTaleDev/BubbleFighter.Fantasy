namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// <see cref="ServiceRegistry"/> half that keeps consumers safe from provider retirement: a consumer
/// pins the provider publications it read through a <see cref="DependencyLease"/>, and a retiring
/// owner stays discoverable through its drain task until the last dependent lease is gone.
/// </summary>
public sealed partial class ServiceRegistry
{
    /// <summary>
    /// Pins the ready provider publications the accessor depends on and returns their lease. The lease
    /// must be disposed when the consumer finishes reading; while it lives, none of the pinned
    /// publications can retire. Returns false without pinning anything when the accessor's own
    /// publication is stale or when a dependency is not published yet.
    /// </summary>
    internal bool TryRetainDependencies(ActivationServiceAccess access, out IDisposable? lease)
    {
        lock (_gate)
        {
            lease = null;
            if (!access.MatchesCurrent()) return false;
            var providers = access.View.Providers.Where(provider => provider != _rootOwner).ToArray();
            if (providers.Any(provider => !provider.Publication.IsReady)) return false;
            if (providers.Length == 0) return true;
            Reserve();
            foreach (var provider in providers) provider.Publication.Dependents++;
            lease = new DependencyLease(this, providers);
            return true;
        }
    }

    /// <summary>
    /// Retires one owner's contributions and returns the task that completes when no dependent lease
    /// still holds them. Bindings are rewritten to the surviving decorations before the publication is
    /// retired, so a reader arriving mid-retirement resolves a pre-retirement projection instead of a
    /// half-decorated chain. The drain task is created only when dependents exist.
    /// </summary>
    internal Task RetireOwner(ServiceActivation owner)
    {
        Runtime.RequireOwner(owner.Runtime);
        ServiceKey[] changed = [];
        Task drained;
        lock (_gate)
        {
            if (!owner.Publication.IsRetired)
            {
                var entries = AllRegistrations().Where(entry => entry.BaseBinding.Registration.Provider == owner ||
                    entry.Layers.Values.Any(layer => layer.Registration.Provider == owner)).ToArray();
                var projections = new List<(Registration Entry, ServiceBinding Binding)>();
                if (owner.Publication.IsReady)
                {
                    changed = entries.Select(entry => entry.Key).Distinct().ToArray();
                    foreach (var entry in entries.Where(entry => entry.Chain is not null && entry.BaseBinding.Registration.Provider != owner))
                    {
                        var layers = entry.Layers.Values.Where(layer => layer.Registration.Provider != owner && layer.Registration.Provider.Publication.IsReady).ToArray();
                        projections.Add((entry, entry.Binding.WithDecorations(entry.Chain!.CaptureReference(excluding: owner.Publication), layers)));
                    }
                }
                foreach (var projection in projections) projection.Entry.Binding = projection.Binding;
                owner.Publication.Retire();
            }
            if (owner.Publication.Dependents == 0) drained = Task.CompletedTask;
            else drained = (owner.Publication.DependentsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        foreach (var key in changed) BindingChanged?.Invoke(key);
        return drained;
    }

    /// <summary>
    /// One consumer's pin over a fixed set of provider publications. Disposing it is the only way the
    /// pins are released; a second dispose is a no-op, and the owning registry stops counting
    /// dependents once every pin of a publication is gone.
    /// </summary>
    private sealed class DependencyLease(ServiceRegistry owner, ServiceActivation[] providers) : IDisposable
    {
        private ServiceRegistry? _owner = owner;
        public void Dispose()
        {
            var registry = Interlocked.Exchange(ref _owner, null);
            if (registry is null) return;
            lock (registry._gate)
            {
                foreach (var provider in providers)
                    if (--provider.Publication.Dependents == 0) provider.Publication.DependentsDrained?.TrySetResult();
                registry._retained--;
            }
        }
    }
}
