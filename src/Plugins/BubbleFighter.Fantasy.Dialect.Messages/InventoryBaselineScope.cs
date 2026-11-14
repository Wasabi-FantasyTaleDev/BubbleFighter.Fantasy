using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Bounds and serializes each connection's inventory publications and their shared time origin.
/// Full ordinary lists establish the origin (kr-812 sub_977960, kr-1256 sub_804220), while
/// delta consumers retain it, so origin selection and the complete send sequence share one lease.
/// </summary>
internal sealed class InventoryBaselineScope : IDisposable
{
    internal const int DefaultPublicationCapacity = 8;
    internal const int MaximumPublicationCapacity = 256;
    private const string RetiredScopeKey = "dialect.messages.retired_inventory_publication_scope";
    private readonly Lock _gate = new();
    private readonly object _retirementMarker = new();
    private readonly Dictionary<long, Entry> _entries = new();
    private readonly int _capacity;
    private readonly int _publicationCapacity;
    private readonly TimeProvider _clock;
    private bool _disposed;

    internal InventoryBaselineScope(int capacity, TimeProvider clock,
        int publicationCapacity = DefaultPublicationCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(publicationCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(publicationCapacity, MaximumPublicationCapacity);
        ArgumentNullException.ThrowIfNull(clock);
        _capacity = capacity;
        _publicationCapacity = publicationCapacity;
        _clock = clock;
    }

    /// <summary>Reserves bounded running-or-pending capacity before waiting for this connection's publisher.</summary>
    internal async ValueTask<Publication> EnterAsync(IConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ct.ThrowIfCancellationRequested();
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (connection.IsClosed) throw new OperationCanceledException("Inventory connection is closed.", ct);
            if (connection.Items.TryGetValue(RetiredScopeKey, out var marker) && ReferenceEquals(marker, _retirementMarker))
                throw new OperationCanceledException("Inventory publication epoch is retired.", ct);
            if (!_entries.TryGetValue(connection.Id, out entry!))
            {
                if (_entries.Count >= _capacity)
                    throw new InvalidOperationException("Inventory publications reached their connection limit.");
                entry = new Entry(connection);
                _entries.Add(connection.Id, entry);
            }
            if (entry.Retired) throw new OperationCanceledException("Inventory publication epoch is retired.", ct);
            if (!ReferenceEquals(entry.Connection, connection))
                throw new InvalidOperationException("Inventory connection identity was reused before retirement completed.");
            if (entry.Admissions >= _publicationCapacity)
                throw new InvalidOperationException("Inventory publications reached their per-connection limit.");
            entry.Admissions++;
        }

        CancellationTokenSource? cancellation = null;
        var acquired = false;
        try
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Retirement.Token);
            await entry.Publisher.WaitAsync(cancellation.Token).ConfigureAwait(false);
            acquired = true;
            Check(entry, cancellation.Token);
            return new Publication(this, entry, cancellation);
        }
        catch
        {
            cancellation?.Dispose();
            Release(entry, acquired);
            throw;
        }
    }

    /// <summary>Returns an installed origin for diagnostics without admitting or creating a publisher.</summary>
    internal DateTimeOffset Require(long connectionId) => Find(connectionId)
        ?? throw new InvalidOperationException("An inventory update requires a previously published full inventory.");

    /// <summary>Returns an installed live origin, or null when no full publication has established one.</summary>
    internal DateTimeOffset? Find(long connectionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(connectionId, out var entry) && !entry.Retired ? entry.Baseline : null;
        }
    }

    /// <summary>Retires the existing epoch and cancels its work while retaining its budget until every admission drains.</summary>
    internal void Remove(long connectionId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(connectionId, out entry) || entry.Retired) return;
            Retire(entry);
        }
        CancelRetired(entry);
    }

    /// <summary>Closes admission and retires every retained publication without invoking callbacks under the scope lock.</summary>
    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.Where(entry => !entry.Retired).ToArray();
            foreach (var entry in entries) Retire(entry);
        }
        List<Exception>? failures = null;
        foreach (var entry in entries)
        {
            try { CancelRetired(entry); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is not null) throw new AggregateException("Inventory publication cancellation failed.", failures);
    }

    private void Retire(Entry entry)
    {
        entry.Retired = true;
        entry.Cancelling = true;
        entry.Baseline = null;
        // Keep one marker on the connection itself, without retaining drained connections in the row.
        entry.Connection.Items[RetiredScopeKey] = _retirementMarker;
    }

    private void CancelRetired(Entry entry)
    {
        try { entry.Retirement.Cancel(); }
        finally
        {
            Entry? detached;
            lock (_gate)
            {
                entry.Cancelling = false;
                detached = DetachUnused(entry);
            }
            detached?.Dispose();
        }
    }

    private Entry? DetachUnused(Entry entry)
    {
        if (entry.Admissions != 0 || entry.Cancelling || !entry.Retired && entry.Baseline is not null) return null;
        if (!_entries.TryGetValue(entry.Connection.Id, out var current) || !ReferenceEquals(current, entry)) return null;
        _entries.Remove(entry.Connection.Id);
        return entry;
    }

    private void Release(Entry entry, bool acquired)
    {
        if (acquired) entry.Publisher.Release();
        Entry? detached;
        lock (_gate)
        {
            entry.Admissions--;
            detached = DetachUnused(entry);
        }
        detached?.Dispose();
    }

    private void Check(Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) CheckLocked(entry, ct);
    }

    private void CheckLocked(Entry entry, CancellationToken ct)
    {
        if (_disposed || entry.Retired || entry.Connection.IsClosed
            || !_entries.TryGetValue(entry.Connection.Id, out var current) || !ReferenceEquals(current, entry))
            throw new OperationCanceledException("Inventory publication epoch is no longer active.", ct);
        ct.ThrowIfCancellationRequested();
    }

    private async ValueTask AbortAsync(Entry entry)
    {
        Exception? retirementFailure = null;
        try { Remove(entry.Connection.Id); }
        catch (Exception exception) { retirementFailure = exception; }
        try { await entry.Connection.CloseAsync("Inventory publication failed.").ConfigureAwait(false); }
        catch (Exception exception) when (retirementFailure is not null)
        {
            throw new AggregateException("Inventory publication retirement and connection close failed.", retirementFailure, exception);
        }
        if (retirementFailure is not null)
            throw new AggregateException("Inventory publication retirement failed.", retirementFailure);
    }

    internal sealed class Entry(IConnection connection) : IDisposable
    {
        internal IConnection Connection { get; } = connection;
        internal SemaphoreSlim Publisher { get; } = new(1, 1);
        internal CancellationTokenSource Retirement { get; } = new();
        internal DateTimeOffset? Baseline { get; set; }
        internal int Admissions { get; set; }
        internal bool Retired { get; set; }
        internal bool Cancelling { get; set; }

        /// <summary>Releases synchronization resources only after every admission and retirement callback has drained.</summary>
        public void Dispose()
        {
            Publisher.Dispose();
            Retirement.Dispose();
        }
    }

    /// <summary>Owns one connection epoch from asynchronous preparation through its last origin-dependent frame.</summary>
    internal sealed class Publication : IDisposable
    {
        private readonly InventoryBaselineScope _owner;
        private readonly Entry _entry;
        private readonly CancellationTokenSource _cancellation;
        private int _released;

        internal Publication(InventoryBaselineScope owner, Entry entry, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _entry = entry;
            _cancellation = cancellation;
        }

        internal CancellationToken CancellationToken => _cancellation.Token;

        internal void ThrowIfInvalid()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            _owner.Check(_entry, CancellationToken);
        }

        internal DateTimeOffset PrepareReset()
        {
            ThrowIfInvalid();
            return _owner._clock.GetUtcNow();
        }

        internal DateTimeOffset? Find()
        {
            lock (_owner._gate)
            {
                _owner.CheckLocked(_entry, CancellationToken);
                return _entry.Baseline;
            }
        }

        internal DateTimeOffset Require() => Find()
            ?? throw new InvalidOperationException("An inventory update requires a previously published full inventory.");

        internal void Reset(DateTimeOffset baseline)
        {
            lock (_owner._gate)
            {
                _owner.CheckLocked(_entry, CancellationToken);
                _entry.Baseline = baseline;
            }
        }

        internal async ValueTask SendAsync(ISession session, ushort opcode, ReadOnlyMemory<byte> payload)
        {
            try
            {
                ThrowIfInvalid();
                if (!ReferenceEquals(session.Connection, _entry.Connection))
                    throw new InvalidOperationException("Inventory publication lease belongs to another connection.");
                await session.SendAsync(opcode, payload, CancellationToken).ConfigureAwait(false);
                ThrowIfInvalid();
            }
            catch (Exception failure)
            {
                try { await _owner.AbortAsync(_entry).ConfigureAwait(false); }
                catch (Exception cleanup) { failure.Data["InventoryPublicationCleanupFailure"] = cleanup; }
                throw;
            }
        }

        /// <summary>Releases this publisher and its admission exactly once, including canceled or failed sequences.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _cancellation.Dispose();
            _owner.Release(_entry, acquired: true);
        }
    }
}
