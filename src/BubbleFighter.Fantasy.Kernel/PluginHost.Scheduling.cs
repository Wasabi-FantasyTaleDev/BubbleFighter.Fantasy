using System.Collections;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private readonly AsyncLocal<IntentExecution?> _execution = new();
    private readonly HashSet<IntentExecution> _runningExecutions = [];
    private IntentExecution? CurrentExecution => _execution.Value is { } execution && Volatile.Read(ref execution.Active) ? execution : null;
    private TransitionRequest? _runningIntent => CurrentExecution?.Request;
    private PluginContext? _activatingContext
    {
        get => CurrentExecution?.Context;
        set { if (CurrentExecution is { } execution) execution.Context = value; }
    }
    private bool Touches(string rowId) => CurrentExecution is not { } execution || execution.Rows.Contains(rowId);

    private void AdmitExecution(IntentExecution execution, CancellationToken cancellationToken = default)
    {
        // All callers hold the intent gate; reserve the complete module set before publishing state.
        if (HasSourceOperationConflict(execution))
            throw new InvalidOperationException("Lifecycle execution conflicts with an owned definition source operation.");
        _modules.AddScheduledLeases(execution.Modules);
        try
        {
            execution.Request.Begin(cancellationToken);
            _runningExecutions.Add(execution);
        }
        catch
        {
            _modules.RemoveScheduledLeases(execution.Modules);
            throw;
        }
    }

    private sealed class LiveRows : IEnumerable<LiveRow>
    {
        private readonly object _gate = new();
        private readonly List<LiveRow> _items = [];
        internal void Add(LiveRow value)
        {
            lock (_gate)
            {
                if (_items.Any(item => item.Row.Id == value.Row.Id)) throw new InvalidOperationException($"Row '{value.Row.Id}' already owns an activation.");
                _items.Add(value);
            }
        }
        internal bool Remove(LiveRow value) { lock (_gate) return _items.Remove(value); }
        public IEnumerator<LiveRow> GetEnumerator()
        { lock (_gate) return ((IEnumerable<LiveRow>)_items.ToArray()).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class IntentExecution(TransitionRequest request, HashSet<string> rows, HashSet<ServiceKey> reads, HashSet<ServiceKey> writes, HashSet<string> modules, HashSet<string> entryReads)
    {
        internal TransitionRequest Request { get; } = request;
        internal HashSet<string> Rows { get; } = rows;
        internal HashSet<ServiceKey> Reads { get; } = reads;
        internal HashSet<ServiceKey> Writes { get; } = writes;
        internal HashSet<string> Modules { get; } = modules;
        internal HashSet<string> EntryReads { get; } = entryReads;
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Active = true;
        internal PluginContext? Context;
        internal object Stamp = new();
        internal bool Conflicts(IntentExecution other) => Rows.Overlaps(other.Rows) || Rows.Overlaps(other.EntryReads) || EntryReads.Overlaps(other.Rows) ||
            Writes.Overlaps(other.Reads) || Writes.Overlaps(other.Writes) || Reads.Overlaps(other.Writes) || Modules.Overlaps(other.Modules);
    }

    private IntentExecution DescribeExecution(TransitionRequest request, IReadOnlyList<PluginRow>? candidateRows = null, PluginEntryTree? candidateTree = null)
    {
        var current = Volatile.Read(ref _configuration);
        var views = new List<(IReadOnlyList<PluginRow> Rows, PluginEntryTree Tree)> { (current.Rows, current.Tree) };
        var retained = _live.ToArray();
        if (candidateRows is not null && candidateTree is not null) views.Add((candidateRows, candidateTree));
        var rows = new HashSet<string>(request.Roots, StringComparer.Ordinal);
        var writes = new HashSet<ServiceKey>();
        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var live in retained.Where(item => rows.Contains(item.Row.Id)))
                foreach (var contract in AdvertisedServices(live.Descriptor).Concat(Metadata(live.Descriptor).Decorates))
                    writes.Add(live.Context.NativeServices!.Scope.Resolve(contract));
            foreach (var view in views)
            {
                foreach (var row in view.Rows.Where(row => rows.Contains(row.Id)))
                    if (DefinitionFor(row) is { } descriptor)
                        foreach (var contract in AdvertisedServices(descriptor).Concat(Metadata(descriptor).Decorates))
                            writes.Add(view.Tree.Entries[row.Id].Scope.Resolve(contract));
                foreach (var row in view.Rows.Where(row => !rows.Contains(row.Id)))
                    if (DefinitionFor(row) is { } descriptor &&
                        (PluginServiceDeclarations.Reads(Metadata(descriptor)).Any(type => writes.Contains(view.Tree.Entries[row.Id].Scope.Resolve(type))) ||
                         RequiredEntryRows(row, Metadata(descriptor)).Any(rows.Contains)))
                    { rows.Add(row.Id); progress = true; }
            }
            foreach (var live in retained.Where(item => !rows.Contains(item.Row.Id)))
                if (retained.Any(provider => rows.Contains(provider.Row.Id) && live.DependsOn(provider)))
                { rows.Add(live.Row.Id); progress = true; }
        }
        var reads = new HashSet<ServiceKey>();
        var modules = new HashSet<string>(StringComparer.Ordinal);
        var entryReads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var live in retained.Where(item => rows.Contains(item.Row.Id)))
        {
            foreach (var contract in PluginServiceDeclarations.Reads(Metadata(live.Descriptor)))
                reads.Add(live.Context.NativeServices!.Scope.Resolve(contract));
            entryReads.UnionWith(live.RequiredRows);
            if (live.Descriptor.CoordinationKey is { } key) modules.Add(key);
        }
        foreach (var view in views)
            foreach (var row in view.Rows.Where(row => rows.Contains(row.Id)))
                if (DefinitionFor(row) is { } descriptor)
                {
                    foreach (var contract in PluginServiceDeclarations.Reads(Metadata(descriptor)))
                        reads.Add(view.Tree.Entries[row.Id].Scope.Resolve(contract));
                    entryReads.UnionWith(RequiredEntryRows(row, Metadata(descriptor)));
                    if (descriptor.CoordinationKey is { } key) modules.Add(key);
                }
        return new(request, rows, reads, writes, modules, entryReads);
    }
    private void ValidateReservedDescriptor(PluginRow row, PluginDescriptor descriptor)
    {
        if (!IsReservedDescriptor(row, descriptor))
            throw new InvalidOperationException($"Row '{row.Id}' changed contracts outside its reserved dependency scope; configuration reconciliation is required.");
    }

    private bool IsReservedDescriptor(PluginRow row, PluginDescriptor descriptor)
    {
        if (CurrentExecution is not { } execution) return true;
        var metadata = Metadata(descriptor);
        return !PluginServiceDeclarations.Reads(metadata).Any(type => !execution.Reads.Contains(ScopeFor(row).Resolve(type))) &&
            !AdvertisedServices(descriptor).Concat(metadata.Decorates).Any(type => !execution.Writes.Contains(ScopeFor(row).Resolve(type))) &&
            metadata.Overrides.Order().SequenceEqual(Metadata(DefinitionFor(row)!).Overrides.Order());
    }
}
