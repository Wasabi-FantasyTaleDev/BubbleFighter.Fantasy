using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Tracks bounded pending acquisitions, executor ownership, definition caches and explicit catalog pins.</summary>
/// <remarks>Removing a record releases this ledger's references; it does not prove backend or assembly reclamation.</remarks>
internal sealed class PluginModuleOwnership
{
    private readonly object _gate = new();
    private readonly Dictionary<PluginModuleCode, Revision> _revisions = new(CodeComparer.Instance);
    private readonly Dictionary<string, AcquisitionState> _acquisitions = new(StringComparer.Ordinal);
    private readonly int _entryCapacity;
    private readonly int _cacheCapacity;
    private readonly int _catalogCapacity;
    private long _nextRevision;
    private int _pending;
    private int _pendingRetained;
    private int _definitionCaches;
    private int _catalogPins;

    /// <summary>Captures bootstrap roots and fixes the record budget to admitted definition capacity plus two revisions per entry.</summary>
    internal PluginModuleOwnership(IReadOnlyList<PluginDescriptor> catalog, int entryCapacity, int? catalogCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var catalogCount = catalog.Count;
        _catalogCapacity = catalogCapacity ?? catalogCount;
        if (entryCapacity < 0 || catalogCount < 0 || _catalogCapacity < catalogCount)
            throw new ConfigException("Module ownership capacities must be nonnegative and fit the bootstrap catalog.");
        try { Capacity = checked(_catalogCapacity + checked(2 * entryCapacity)); }
        catch (OverflowException) { throw new ConfigException("Module ownership capacity exceeds the supported range."); }
        _entryCapacity = entryCapacity;
        _cacheCapacity = checked(entryCapacity + 1);
        _ = RetainCatalogBatch(catalog);
    }

    /// <summary>Gets the fixed combined bound on retained revisions and pending acquisitions.</summary>
    internal int Capacity { get; }
    /// <summary>Gets acquisitions whose executor provenance has not yet been bound.</summary>
    internal int PendingCount { get { lock (_gate) return _pending; } }
    /// <summary>Gets unbound acquisitions whose cleanup failed without releasing their ownership.</summary>
    internal int PendingRetainedCount { get { lock (_gate) return _pendingRetained; } }

    /// <summary>Atomically pins a bounded captured definition batch until its source has completely retired.</summary>
    internal IDisposable RetainCatalogBatch(IReadOnlyList<PluginDescriptor> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var expected = definitions.Count;
        if (expected < 0 || expected > _catalogCapacity)
            throw new ConfigException("The catalog definition batch exceeds the ownership capacity.");
        var captured = new PluginModuleCode[expected];
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var definition in definitions)
        {
            if (index >= expected) throw new ConfigException("The catalog definition batch exceeds its reported count.");
            var code = CaptureCode(definition);
            if (string.IsNullOrWhiteSpace(definition.Id) || !identities.Add(definition.Id))
                throw new ConfigException("A catalog definition batch requires distinct nonempty identities.");
            captured[index++] = code;
        }
        if (index != expected || definitions.Count != expected)
            throw new ConfigException("The catalog definition batch changed during ownership capture.");
        lock (_gate)
        {
            if ((long)_catalogPins + expected > _catalogCapacity)
                throw new ConfigException("The catalog definition ownership capacity is exhausted.");
            var staged = new Dictionary<PluginModuleCode, PluginModuleCode>(CodeComparer.Instance);
            foreach (var code in captured)
            {
                if (staged.TryGetValue(code, out var pending)) staged[code] = MergeCode(pending, code);
                else if (_revisions.TryGetValue(code, out var known))
                    staged.Add(code, MergeCode(known.Code with { LifetimeAnchor = known.LifetimeAnchor, Lifetime = known.Lifetime }, code));
                else staged.Add(code, code);
            }
            var missing = staged.Keys.Count(code => !_revisions.ContainsKey(code));
            if ((long)_revisions.Count + _pending + missing > Capacity)
                throw new ConfigException("The module revision capacity is exhausted.");
            if (_nextRevision > long.MaxValue - missing)
                throw new ConfigException("Module revision identities exceed the supported range.");
            // Every association and bound is validated before either a new pin or a metadata promotion is visible.
            foreach (var code in staged.Values) _ = FindOrCreate(code);
            var held = new Revision[expected];
            for (index = 0; index < captured.Length; index++)
            {
                held[index] = _revisions[captured[index]];
                held[index].CatalogPins++;
            }
            _catalogPins += expected;
            return new CatalogLease(this, held);
        }
    }

    /// <summary>Reserves one entry's acquisition before a factory may execute.</summary>
    internal Acquisition Reserve(string rowId)
    {
        if (string.IsNullOrWhiteSpace(rowId)) throw new ConfigException("A module acquisition requires a nonempty entry identity.");
        lock (_gate)
        {
            if (_acquisitions.ContainsKey(rowId))
                throw new InvalidOperationException($"Entry '{rowId}' already owns a module acquisition.");
            if (_acquisitions.Count >= _entryCapacity || (long)_revisions.Count + _pending >= Capacity)
                throw new ConfigException("The module ownership capacity is exhausted.");
            var ticket = new Acquisition(this, rowId);
            _acquisitions.Add(rowId, new(ticket));
            _pending++;
            return ticket;
        }
    }

    /// <summary>Copies retained revisions as immutable values without exposing code objects or mutable counters.</summary>
    internal IReadOnlyList<SnapshotEntry> Snapshot()
    {
        lock (_gate)
            return Array.AsReadOnly(_revisions.Values.OrderBy(revision => revision.Id).Select(revision =>
                new SnapshotEntry(revision.Id, revision.Code.ModuleId, revision.Code.Attribution,
                    revision.CatalogPins, revision.Activations, revision.DefinitionCaches, revision.RetainedFailures, revision.Lifetime)).ToArray());
    }

    /// <summary>Reports one retained code revision; retained failures are a subset of activation ownership.</summary>
    internal sealed record SnapshotEntry(long RevisionId, string ModuleId, string Attribution, int CatalogPins,
        int Activations, int DefinitionCaches, int RetainedFailures, PluginCodeLifetime Lifetime);

    /// <summary>Owns one entry's pending or bound executor until its host proves complete cleanup.</summary>
    internal sealed class Acquisition : IDisposable
    {
        private PluginModuleOwnership? _owner;
        private long _revisionId;
        internal Acquisition(PluginModuleOwnership owner, string rowId) { _owner = owner; RowId = rowId; }
        internal string RowId { get; }
        /// <summary>Gets the bound revision identity, retained for diagnostics after this ticket is released.</summary>
        internal long? RevisionId => Volatile.Read(ref _revisionId) is var revision && revision != 0 ? revision : null;
        /// <summary>Gets whether the host has released this ticket.</summary>
        internal bool IsReleased => Volatile.Read(ref _owner) is null;
        /// <summary>Associates the completed acquisition with immutable resolved provenance exactly once.</summary>
        internal void Bind(PluginDescriptor resolved) => Owner.Bind(this, resolved);
        /// <summary>Retains a separate cache reference that survives the executor's normal teardown.</summary>
        internal IDisposable RetainDefinition() => Owner.RetainDefinition(this);
        /// <summary>Marks unproven cleanup while retaining the existing acquisition allocation.</summary>
        internal void MarkRetainedFailure() => Owner.MarkRetainedFailure(this);
        /// <summary>Releases this ticket once after executor cleanup and host descriptor ownership have drained.</summary>
        public void Dispose() => Volatile.Read(ref _owner)?.Release(this);
        internal void SetRevision(long revision) => Volatile.Write(ref _revisionId, revision);
        internal void SetReleased() => Volatile.Write(ref _owner, null);
        private PluginModuleOwnership Owner => Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(Acquisition));
    }

    private void Bind(Acquisition ticket, PluginDescriptor resolved)
    {
        var code = CaptureCode(resolved);
        lock (_gate)
        {
            var state = FindOwned(ticket);
            if (state.Revision is not null) throw new InvalidOperationException("An acquisition can bind its code revision only once.");
            var revision = FindOrCreate(code);
            revision.Activations++;
            if (state.Retained) { revision.RetainedFailures++; _pendingRetained--; }
            state.Revision = revision;
            _pending--;
            ticket.SetRevision(revision.Id);
        }
    }

    private IDisposable RetainDefinition(Acquisition ticket)
    {
        lock (_gate)
        {
            var revision = FindOwned(ticket).Revision ?? throw new InvalidOperationException("A pending acquisition cannot retain a definition.");
            if (_definitionCaches >= _cacheCapacity) throw new ConfigException("The module definition-cache capacity is exhausted.");
            var lease = new DefinitionLease(this, revision);
            revision.DefinitionCaches++;
            _definitionCaches++;
            return lease;
        }
    }

    private void MarkRetainedFailure(Acquisition ticket)
    {
        lock (_gate)
        {
            var state = FindOwned(ticket);
            if (state.Retained) return;
            state.Retained = true;
            if (state.Revision is { } revision) revision.RetainedFailures++;
            else _pendingRetained++;
        }
    }

    private void Release(Acquisition ticket)
    {
        lock (_gate)
        {
            if (ticket.IsReleased) return;
            var state = FindOwned(ticket);
            if (state.Revision is { } revision)
            {
                revision.Activations--;
                if (state.Retained) revision.RetainedFailures--;
                Prune(revision);
            }
            else
            {
                _pending--;
                if (state.Retained) _pendingRetained--;
            }
            _acquisitions.Remove(ticket.RowId);
            ticket.SetReleased();
        }
    }

    private void ReleaseDefinition(Revision revision)
    {
        lock (_gate)
        {
            revision.DefinitionCaches--;
            _definitionCaches--;
            Prune(revision);
        }
    }

    private void ReleaseCatalog(Revision[] revisions)
    {
        lock (_gate)
        {
            foreach (var revision in revisions)
            {
                revision.CatalogPins--;
                Prune(revision);
            }
            _catalogPins -= revisions.Length;
        }
    }

    private static PluginModuleCode MergeCode(PluginModuleCode current, PluginModuleCode incoming)
    {
        if (!StringComparer.Ordinal.Equals(current.Attribution, incoming.Attribution))
            throw new ConfigException($"Module '{incoming.ModuleId}' changed attribution for the same code identity.");
        if (current.LifetimeAnchor is { } existing && incoming.LifetimeAnchor is { } anchor && !ReferenceEquals(existing, anchor))
            throw new ConfigException($"Module '{incoming.ModuleId}' changed its lifetime anchor for the same code identity.");
        if (current.Lifetime != PluginCodeLifetime.Unknown && incoming.Lifetime != PluginCodeLifetime.Unknown && current.Lifetime != incoming.Lifetime)
            throw new ConfigException($"Module '{incoming.ModuleId}' changed its code lifetime for the same code identity.");
        return current with { LifetimeAnchor = current.LifetimeAnchor ?? incoming.LifetimeAnchor,
            Lifetime = current.Lifetime == PluginCodeLifetime.Unknown ? incoming.Lifetime : current.Lifetime };
    }

    private AcquisitionState FindOwned(Acquisition ticket) =>
        !ticket.IsReleased && _acquisitions.TryGetValue(ticket.RowId, out var state) && ReferenceEquals(state.Ticket, ticket)
            ? state : throw new ObjectDisposedException(nameof(Acquisition));

    private Revision FindOrCreate(PluginModuleCode code)
    {
        if (_revisions.TryGetValue(code, out var revision))
        {
            if (!StringComparer.Ordinal.Equals(revision.Code.Attribution, code.Attribution))
                throw new ConfigException($"Module '{code.ModuleId}' changed attribution for the same code identity.");
            if (code.LifetimeAnchor is { } anchor && revision.LifetimeAnchor is { } existing && !ReferenceEquals(existing, anchor))
                throw new ConfigException($"Module '{code.ModuleId}' changed its lifetime anchor for the same code identity.");
            if (code.Lifetime != PluginCodeLifetime.Unknown && revision.Lifetime != PluginCodeLifetime.Unknown &&
                revision.Lifetime != code.Lifetime)
                throw new ConfigException($"Module '{code.ModuleId}' changed its code lifetime for the same code identity.");
            // Validate both associations before promoting either; a rejected bind must not mutate retained provenance.
            revision.LifetimeAnchor ??= code.LifetimeAnchor;
            if (revision.Lifetime == PluginCodeLifetime.Unknown) revision.Lifetime = code.Lifetime;
            return revision;
        }
        if (_revisions.Count >= Capacity) throw new ConfigException("The module revision capacity is exhausted.");
        if (_nextRevision == long.MaxValue) throw new ConfigException("Module revision identities exceed the supported range.");
        revision = new(++_nextRevision, code);
        _revisions.Add(code, revision);
        return revision;
    }

    private void Prune(Revision revision)
    {
        if (revision.CatalogPins == 0 && revision.Activations == 0 && revision.DefinitionCaches == 0)
            _revisions.Remove(revision.Code);
    }

    private static PluginModuleCode CaptureCode(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var code = descriptor.ModuleCode ?? new PluginModuleCode("unattributed:" + descriptor.Id, descriptor, "unattributed");
        if (string.IsNullOrWhiteSpace(code.ModuleId) || code.Identity is null || string.IsNullOrWhiteSpace(code.Attribution))
            throw new ConfigException("Module provenance requires a nonempty identity, attribution and revision object.");
        if (!Enum.IsDefined(code.Lifetime))
            throw new ConfigException("Module provenance declares an undefined code lifetime capability.");
        return code;
    }

    private sealed class Revision(long id, PluginModuleCode code)
    {
        internal long Id { get; } = id;
        internal PluginModuleCode Code { get; } = code;
        internal object? LifetimeAnchor = code.LifetimeAnchor;
        internal PluginCodeLifetime Lifetime = code.Lifetime;
        internal int CatalogPins;
        internal int Activations;
        internal int DefinitionCaches;
        internal int RetainedFailures;
    }

    private sealed class AcquisitionState(Acquisition ticket)
    {
        internal Acquisition Ticket { get; } = ticket;
        internal Revision? Revision;
        internal bool Retained;
    }

    private sealed class DefinitionLease(PluginModuleOwnership owner, Revision revision) : IDisposable
    {
        private PluginModuleOwnership? _owner = owner;
        private Revision? _revision = revision;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            var held = _revision!;
            _revision = null;
            current.ReleaseDefinition(held);
        }
    }

    private sealed class CatalogLease(PluginModuleOwnership owner, Revision[] revisions) : IDisposable
    {
        private PluginModuleOwnership? _owner = owner;
        private Revision[]? _revisions = revisions;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            var held = _revisions!;
            _revisions = null;
            current.ReleaseCatalog(held);
        }
    }

    private sealed class CodeComparer : IEqualityComparer<PluginModuleCode>
    {
        internal static readonly CodeComparer Instance = new();
        public bool Equals(PluginModuleCode? left, PluginModuleCode? right) => ReferenceEquals(left, right) ||
            (left is not null && right is not null && StringComparer.Ordinal.Equals(left.ModuleId, right.ModuleId) &&
             ReferenceEquals(left.Identity, right.Identity));
        public int GetHashCode(PluginModuleCode code) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(code.ModuleId), RuntimeHelpers.GetHashCode(code.Identity));
    }
}
