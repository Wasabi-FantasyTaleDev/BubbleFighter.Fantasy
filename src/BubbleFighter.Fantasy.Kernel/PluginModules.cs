using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Names a holder that keeps one module revision referenced; every source is counted on its own.</summary>
internal enum LeaseKind
{
    /// <summary>Execution reservation held from scheduling until that execution is released.</summary>
    Scheduled,

    /// <summary>Activation executor slot held until the activation's effects drain.</summary>
    Activation,

    /// <summary>Committed entry definition that pins the code resolved for that entry.</summary>
    DefinitionCache,

    /// <summary>Failed backend kept because its drain was never proven.</summary>
    RetainedFailure,
}

/// <summary>
/// Bounded coordination-key accounting. Its generation value counts successful explicit refreshes;
/// it is not a code-revision identity. Actual code holders belong to <see cref="PluginModuleOwnership"/>.
/// </summary>
/// <remarks>
/// The host uses this ledger for scheduled reservations; it neither unloads nor reclaims code. A key is the
/// coordination key a resolved definition already carries, so reloading one module advances the
/// generation of its single record instead of adding records, and the record count stays bounded by
/// the capacity declared at construction. Callers own the surrounding lifetime discipline: every
/// method is synchronous and never runs plugin code, and a key the ledger has not been opened for is
/// rejected rather than treated as an empty record, so a missing record is never a silent default.
/// </remarks>
internal sealed class PluginModules
{
    /// <summary>Generation every record starts at; only a successful explicit reload advances it.</summary>
    internal const long InitialGeneration = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, ModuleRecord> _records;
    private readonly int _capacity;

    /// <summary>Creates a ledger that admits at most the declared number of distinct module records.</summary>
    /// <param name="capacity">Maximum number of distinct module keys the ledger tracks; must be positive.</param>
    /// <exception cref="ConfigException">The capacity is not positive.</exception>
    internal PluginModules(int capacity)
    {
        if (capacity <= 0) throw new ConfigException("The module ledger capacity must be positive.");
        _capacity = capacity;
        // The declared capacity is a hard bound checked on every new key, never a growth hint, so a
        // large declared bound allocates nothing until records actually appear.
        _records = new Dictionary<string, ModuleRecord>(StringComparer.Ordinal);
    }

    /// <summary>Gets the capacity-derived bound on records, and therefore on the entries a snapshot reports.</summary>
    internal int MaxSnapshotEntries => _capacity;

    /// <summary>Pins the captured distinct keys of a source until its definitions and consumers have fully retired.</summary>
    /// <remarks>Only records first opened by this admission become reclaimable; legacy records preserve their existing lifetime.</remarks>
    internal IDisposable RetainSourceKeys(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var expected = keys.Count;
        if (expected < 0 || expected > _capacity)
            throw new ConfigException($"Module source reservation exceeds ledger capacity {_capacity}.");
        var captured = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var key in keys)
        {
            if (count++ >= expected) throw new ConfigException("Module source key enumeration exceeds its reported count.");
            ValidateKey(key);
            captured.Add(key);
        }
        if (count != expected || keys.Count != expected)
            throw new ConfigException("Module source keys changed during capture.");
        lock (_gate)
        {
            var missing = 0;
            foreach (var key in captured)
            {
                if (!_records.TryGetValue(key, out var record)) missing++;
                else if (record.SourcePins == int.MaxValue)
                    throw new ConfigException($"Module '{key}' source lease count exceeds the supported range.");
            }
            if ((long)_records.Count + missing > _capacity)
                throw new ConfigException($"Module source reservation exceeds ledger capacity {_capacity}.");
            var held = new ModuleRecord[captured.Count];
            var index = 0;
            foreach (var key in captured)
            {
                if (!_records.TryGetValue(key, out var record))
                {
                    record = new ModuleRecord(key) { SourceManaged = true };
                    _records.Add(key, record);
                }
                record.SourcePins++;
                held[index++] = record;
            }
            return new SourceLease(this, held);
        }
    }

    /// <summary>Claims the single reload slot of a module, opening its record when the key is first seen.</summary>
    /// <param name="key">Coordination key of the module being refreshed.</param>
    /// <returns><c>true</c> when this caller owns the reload; <c>false</c> when one is already in progress.</returns>
    /// <exception cref="ConfigException">The key is blank or the ledger is full.</exception>
    internal bool TryBeginReload(string key)
    {
        lock (_gate)
        {
            var record = Open(key);
            return record.TryClaimReload();
        }
    }

    /// <summary>Ends the reload a module currently owns and advances its generation only when that reload succeeded.</summary>
    /// <param name="key">Coordination key of the module whose reload finished.</param>
    /// <param name="success">Whether the reload resolved new code for the module.</param>
    /// <returns><c>true</c> when the generation advanced; <c>false</c> when a failed reload left it unchanged.</returns>
    /// <exception cref="ConfigException">The key is unknown, no reload was begun for it, or the generation would overflow.</exception>
    internal bool CompleteReload(string key, bool success)
    {
        lock (_gate)
        {
            var record = Find(key);
            if (!record.ReleaseReload())
                throw new ConfigException($"Module '{key}' has no reload in progress to complete.");
            if (!success) { Prune(record); return false; }
            try
            {
                record.Generation = checked(record.Generation + 1);
            }
            catch (OverflowException)
            {
                throw new ConfigException($"Module '{key}' generation exceeds the supported range.");
            }
            finally { Prune(record); }
            return true;
        }
    }

    /// <summary>Adds one reference from a lease source, opening the record when the key is first seen.</summary>
    /// <param name="key">Coordination key of the referenced module.</param>
    /// <param name="kind">Lease source that starts holding the module revision.</param>
    /// <exception cref="ConfigException">The key is invalid or the ledger is full.</exception>
    internal void AddLease(string key, LeaseKind kind)
    {
        ValidateKind(kind);
        lock (_gate)
        {
            var record = Open(key);
            var count = Count(record, kind);
            if (count == int.MaxValue)
                throw new ConfigException($"Module '{key}' {kind} lease count exceeds the supported range.");
            SetCount(record, kind, count + 1);
        }
    }

    /// <summary>Reserves every scheduled module atomically after validating the complete bounded set.</summary>
    internal void AddScheduledLeases(IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_gate)
        {
            if (keys.Count > _capacity)
                throw new ConfigException($"Module reservation exceeds ledger capacity {_capacity}.");
            var missing = 0;
            foreach (var key in keys)
            {
                ValidateKey(key);
                if (!_records.TryGetValue(key, out var record)) missing++;
                else if (record.Scheduled == int.MaxValue)
                    throw new ConfigException($"Module '{key}' scheduled lease count exceeds the supported range.");
            }
            if ((long)_records.Count + missing > _capacity)
                throw new ConfigException($"Module reservation exceeds ledger capacity {_capacity}.");
            foreach (var key in keys) Open(key).Scheduled++;
        }
    }

    /// <summary>Releases a complete scheduled reservation without partially decrementing a malformed batch.</summary>
    internal void RemoveScheduledLeases(IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_gate)
        {
            foreach (var key in keys)
                if (Find(key).Scheduled == 0)
                    throw new ConfigException($"Module '{key}' holds no scheduled lease to remove.");
            foreach (var key in keys)
            {
                var record = _records[key];
                record.Scheduled--;
                Prune(record);
            }
        }
    }

    /// <summary>Removes one reference from a lease source; removing a reference that was never taken fails loud.</summary>
    /// <param name="key">Coordination key of the module whose reference ends.</param>
    /// <param name="kind">Lease source that stops holding the module revision.</param>
    /// <exception cref="ConfigException">The key is unknown or that source holds no reference to remove.</exception>
    internal void RemoveLease(string key, LeaseKind kind)
    {
        ValidateKind(kind);
        lock (_gate)
        {
            var record = Find(key);
            var count = Count(record, kind);
            if (count == 0)
                throw new ConfigException($"Module '{key}' holds no {kind} lease to remove.");
            SetCount(record, kind, count - 1);
            Prune(record);
        }
    }

    /// <summary>Records whether a failed backend for this module is retained because its drain is unproven.</summary>
    /// <param name="key">Coordination key of the failed module.</param>
    /// <param name="retained"><c>true</c> while the failure stays retained; <c>false</c> once its drain is proven.</param>
    /// <exception cref="ConfigException">The key is unknown.</exception>
    internal void MarkRetainedFailure(string key, bool retained)
    {
        lock (_gate)
        {
            var record = Find(key);
            record.RetainedFailureMarked = retained;
            Prune(record);
        }
    }

    /// <summary>Reports whether any record still holds a scheduled lease; the host asserts this is false once every execution has settled.</summary>
    internal bool HasScheduledLeases()
    {
        lock (_gate)
            return _records.Values.Any(record => record.Scheduled > 0);
    }

    /// <summary>Reports whether new acquisition through this module key has to be refused.</summary>
    /// <param name="key">Coordination key a factory acquisition would use.</param>
    /// <returns><c>true</c> while the module carries a retained failure; otherwise <c>false</c>.</returns>
    /// <exception cref="ConfigException">The key is unknown.</exception>
    internal bool ShouldRefuseAcquisition(string key)
    {
        lock (_gate) return Find(key).RetainedFailureMarked;
    }

    /// <summary>Copies the ledger's diagnostic state ordered by key; the copy never exceeds the declared capacity.</summary>
    /// <returns>One tuple per record with its generation and every lease counter.</returns>
    internal IReadOnlyList<(string Key, long Generation, int Scheduled, int Activation, int DefinitionCache, int RetainedFailure, bool RetainedFailureMarked)> Snapshot()
    {
        lock (_gate)
        {
            return _records.Values
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, entry.Generation, entry.Scheduled, entry.Activation,
                    entry.DefinitionCache, entry.RetainedFailure, entry.RetainedFailureMarked))
                .ToArray();
        }
    }

    private ModuleRecord Open(string key)
    {
        ValidateKey(key);
        if (_records.TryGetValue(key, out var existing)) return existing;
        if (_records.Count >= _capacity)
            throw new ConfigException($"Module ledger capacity of {_capacity} is exceeded by key '{key}'.");
        var record = new ModuleRecord(key);
        _records.Add(key, record);
        return record;
    }

    private void ReleaseSource(ModuleRecord[] records)
    {
        lock (_gate)
        {
            foreach (var record in records)
            {
                record.SourcePins--;
                Prune(record);
            }
        }
    }

    private void Prune(ModuleRecord record)
    {
        if (record.SourceManaged && record.SourcePins == 0 && record.Scheduled == 0 && record.Activation == 0 &&
            record.DefinitionCache == 0 && record.RetainedFailure == 0 && !record.RetainedFailureMarked &&
            !record.ReloadInProgress && _records.TryGetValue(record.Key, out var current) && ReferenceEquals(current, record))
            _records.Remove(record.Key);
    }

    private sealed class SourceLease(PluginModules owner, ModuleRecord[] records) : IDisposable
    {
        private PluginModules? _owner = owner;
        private ModuleRecord[]? _records = records;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            var held = _records!;
            _records = null;
            current.ReleaseSource(held);
        }
    }

    private ModuleRecord Find(string key)
    {
        ValidateKey(key);
        if (!_records.TryGetValue(key, out var record))
            throw new ConfigException($"Module '{key}' is not tracked by the module ledger.");
        return record;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ConfigException("A module key must be a nonempty coordination key.");
        // Keys reuse already captured catalog identities; the ledger bounds record count,
        // rather than imposing a second, incompatible definition-validation contract.
    }

    // Validated before any record is opened so an unsupported kind can never leave a phantom record.
    private static void ValidateKind(LeaseKind kind)
    {
        switch (kind)
        {
            case LeaseKind.Scheduled:
            case LeaseKind.Activation:
            case LeaseKind.DefinitionCache:
            case LeaseKind.RetainedFailure:
                return;
            default: throw new ConfigException($"Lease kind '{kind}' is not a supported module lease source.");
        }
    }

    private static int Count(ModuleRecord record, LeaseKind kind) => kind switch
    {
        LeaseKind.Scheduled => record.Scheduled,
        LeaseKind.Activation => record.Activation,
        LeaseKind.DefinitionCache => record.DefinitionCache,
        LeaseKind.RetainedFailure => record.RetainedFailure,
        _ => throw new ConfigException($"Lease kind '{kind}' is not a supported module lease source."),
    };

    private static void SetCount(ModuleRecord record, LeaseKind kind, int value)
    {
        switch (kind)
        {
            case LeaseKind.Scheduled: record.Scheduled = value; break;
            case LeaseKind.Activation: record.Activation = value; break;
            case LeaseKind.DefinitionCache: record.DefinitionCache = value; break;
            case LeaseKind.RetainedFailure: record.RetainedFailure = value; break;
            default: throw new ConfigException($"Lease kind '{kind}' is not a supported module lease source.");
        }
    }

    /// <summary>Mutable ledger record for one module key; counters are only touched under the ledger lock.</summary>
    private sealed class ModuleRecord(string key)
    {
        private int _reloadInProgress;

        /// <summary>Immutable coordination key this record accounts for; the ledger never renames a record.</summary>
        internal string Key { get; } = key;

        /// <summary>Current module generation; the ledger's initial generation until a successful explicit reload advances it.</summary>
        internal long Generation { get; set; } = InitialGeneration;

        /// <summary>Whether explicit source ownership, rather than legacy ledger lifetime, governs reclamation.</summary>
        internal bool SourceManaged { get; init; }

        /// <summary>Published or retiring sources that can still issue work through this record incarnation.</summary>
        internal int SourcePins { get; set; }

        /// <summary>Whether a claimed refresh operation still owns this record incarnation.</summary>
        internal bool ReloadInProgress => Volatile.Read(ref _reloadInProgress) != 0;

        /// <summary>Scheduled execution reservations currently holding this module's revision.</summary>
        internal int Scheduled { get; set; }

        /// <summary>Activation executor slots holding this module's revision until their effects drain.</summary>
        internal int Activation { get; set; }

        /// <summary>Committed entry definitions currently pinning the code resolved for their entry.</summary>
        internal int DefinitionCache { get; set; }

        /// <summary>Failed backends still counted as holding this module because their drain was never proven.</summary>
        internal int RetainedFailure { get; set; }

        /// <summary>Whether this module refuses new acquisition because a failed backend is still retained.</summary>
        internal bool RetainedFailureMarked { get; set; }

        /// <summary>Claims the one reload slot of this module atomically and reports whether this caller won it.</summary>
        internal bool TryClaimReload() => Interlocked.CompareExchange(ref _reloadInProgress, 1, 0) == 0;

        /// <summary>Releases the reload slot and reports whether a reload was actually in progress.</summary>
        internal bool ReleaseReload() => Interlocked.Exchange(ref _reloadInProgress, 0) == 1;
    }
}
