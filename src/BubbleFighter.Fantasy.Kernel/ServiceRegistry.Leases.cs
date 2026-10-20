namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class ServiceRegistry
{
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
