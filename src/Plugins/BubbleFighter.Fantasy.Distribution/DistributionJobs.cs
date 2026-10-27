using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>
/// Owns the staging root for server-only downloads and drives one supervised job per admitted plan.
/// The root is claimed exclusively through a <c>.owner.lock</c> file held for the plugin's lifetime
/// (a second row or process on the same staging path fails activation), every path the jobs touch is
/// checked for reparse points, and receipts are written by flush-to-disk plus rename so a crash leaves
/// the previous phase readable. Each job's bytes are reserved against the volume's free space minus
/// the configured reserve before the first transfer, and the reservation is released when the job
/// finishes, fails or is cancelled. Staging only: no archive is extracted, no plugin is loaded and no
/// application path is replaced by anything in this class.
/// </summary>
internal sealed partial class DistributionJobs : IDistributionJobs, IAsyncDisposable
{
    private readonly IDistributionPlans _plans;
    private readonly IDistributionPackages _packages;
    private readonly IDistributionTransport _transport;
    private readonly Func<string, Func<CancellationToken, ValueTask>, CancellationToken, ValueTask<IOwnedTask>> _run;
    private readonly string _root;
    private readonly FileStream _rootLock;
    private readonly long _reserve;
    private readonly int _pageSize;
    private readonly Func<string, long> _availableSpace;
    private readonly CancellationTokenSource _stop;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _byPlan = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _inspections = new(StringComparer.Ordinal);
    private readonly Func<string, Stream> _openBlob;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admissions;
    private long _reservedBytes;
    private bool _disposed;
    private Task? _disposeTask;

    internal DistributionJobs(IDistributionPlans plans, IDistributionPackages packages, IDistributionTransport transport,
        DistributionJobOptions options, Func<string, Func<CancellationToken, ValueTask>, CancellationToken, ValueTask<IOwnedTask>> run,
        CancellationToken lifetime = default, Func<string, long>? availableSpace = null, Func<string, Stream>? openBlob = null)
    {
        _plans = plans;
        _packages = packages;
        _transport = transport;
        _run = run;
        _root = Path.GetFullPath(options.StagingPath);
        _reserve = options.FreeSpaceReserveBytes;
        _pageSize = options.HistoryPageSize;
        _availableSpace = availableSpace ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
        _openBlob = openBlob ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
        Directory.CreateDirectory(_root);
        RejectLink(_root);
        var lockPath = Path.Combine(_root, ".owner.lock");
        if (File.Exists(lockPath)) RejectLink(lockPath);
        _rootLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    /// <summary>
    /// Admits one supervised download for the plan and waits for its first persisted receipt. Callers
    /// that repeat an already-running plan id converge on that job instead of racing a second one; the
    /// work itself runs under the supervisor's named activation, so admission consumes real runtime
    /// capacity and is refused when the process is stopping.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown for an empty plan id or a non-server download target.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the plan fails revalidation, its selection changed
    /// underneath the job, or the plan was withdrawn before the transfer could claim it.</exception>
    public async ValueTask<DistributionDownloadJob> StartAsync(string planId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Entry? active;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_byPlan.TryGetValue(planId, out active)) _admissions++;
        }
        if (active is not null) return await AwaitReceiptAsync(active, cancellationToken).ConfigureAwait(false);
        Entry? entry = null;
        var ownsEntry = false;
        try
        {
            var plan = await _plans.ValidateAsync(planId, admission.Token).ConfigureAwait(false);
            if (plan.Selection.Package.Identity.Target != "server") throw new ArgumentException("Only server downloads can be admitted.");
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                admission.Token.ThrowIfCancellationRequested();
                if (!_byPlan.TryGetValue(planId, out entry))
                {
                    var now = DateTimeOffset.UtcNow;
                    var snapshot = new DistributionDownloadJob(Guid.CreateVersion7().ToString("N"), planId, plan.Source.Id,
                        plan.Selection.Package.Identity, plan.Selection.Package.Version, "preparing", 0, 0, 0, 0, null, now, now);
                    entry = new(snapshot, plan);
                    _active.Add(snapshot.Id, entry);
                    _byPlan.Add(planId, entry);
                    ownsEntry = true;
                }
            }
            if (!ownsEntry) return await AwaitReceiptAsync(entry, cancellationToken).ConfigureAwait(false);
            // The supervisor reserves the activation's real resource capacity; there is no independent unbounded worker queue.
            var owned = await _run("distribution-download:" + entry.Snapshot.Id, token => ExecuteAsync(entry, token), _stop.Token).ConfigureAwait(false);
            // Supervision can cancel before invoking the body; completion must release that admission as well.
            _ = owned.Completion.ContinueWith(_ => Finish(entry), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await AwaitReceiptAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (ownsEntry && entry is not null) Finish(entry);
            throw;
        }
        finally
        {
            lock (_gate) { _admissions--; SignalDrained(); }
        }
    }

    private async ValueTask<DistributionDownloadJob> AwaitReceiptAsync(Entry entry, CancellationToken cancellationToken)
    {
        // Recorded completes true when the receipt is persisted and false when the body ended first;
        // the latter is an internal fault, never a caller-visible state.
        if (!await entry.Recorded.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
            throw new IOException("The download task ended before its first receipt could be persisted.");
        lock (_gate) return entry.Snapshot;
    }

    /// <summary>
    /// Returns one bounded history page, active jobs first and persisted jobs after, ordered by
    /// descending job id. The cursor is the next page's first id; jobs deleted concurrently while the
    /// page is assembled are skipped rather than failing the call.
    /// </summary>
    public DistributionDownloadJobPage List(string? after = null)
    {
        if (after is not null) ValidateId(after);
        string[] active;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); active = _active.Keys.ToArray(); }
        var activeSet = active.ToHashSet(StringComparer.Ordinal);
        var selected = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var id in active.Concat(Directory.EnumerateDirectories(_root).Select(Path.GetFileName).OfType<string>().Where(id => !activeSet.Contains(id))))
        {
            if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c)) || after is not null && StringComparer.Ordinal.Compare(id, after) >= 0) continue;
            selected.Enqueue(id, id);
            if ((long)selected.Count > (long)_pageSize + 1) selected.Dequeue();
        }
        var ids = selected.UnorderedItems.Select(item => item.Element).OrderDescending(StringComparer.Ordinal).ToArray();
        var entries = new List<DistributionDownloadJob>();
        foreach (var id in ids.Take(_pageSize))
        {
            try { entries.Add(Get(id)); }
            catch (KeyNotFoundException) { /* A concurrent explicit deletion no longer belongs in the current page. */ }
        }
        return new(entries.AsReadOnly(), ids.Length > _pageSize ? ids[_pageSize - 1] : null);
    }

    /// <summary>
    /// Reads current progress for a live job, or the persisted receipt for a finished one. A receipt
    /// is accepted only after schema validation, and a process that died mid-job reports phase
    /// <c>interrupted</c> instead of its last in-flight phase, so a restart never advertises work as
    /// still running.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when no job owns the id.</exception>
    /// <exception cref="InvalidDataException">Thrown when the receipt is unreadable or inconsistent with its owned schema.</exception>
    public DistributionDownloadJob Get(string jobId)
    {
        ValidateId(jobId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active.TryGetValue(jobId, out var entry)) return entry.Snapshot;
            var directory = OwnedDirectory(jobId);
            var receipt = Path.Combine(directory, "receipt.json");
            RejectLink(receipt);
            using var input = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read);
            var snapshot = JsonSerializer.Deserialize<DistributionDownloadJob>(input)
                ?? throw new InvalidDataException("The download receipt is empty.");
            ValidateReceipt(snapshot, jobId);
            return Terminal(snapshot.Phase) ? snapshot : snapshot with { Phase = "interrupted", ErrorCode = "distribution_job_interrupted" };
        }
    }

    /// <summary>
    /// Requests cancellation of a live download and returns its current state. An already-terminal
    /// job is returned unchanged, and a completion that won the race leaves the terminal receipt
    /// authoritative.
    /// </summary>
    public DistributionDownloadJob Cancel(string jobId)
    {
        ValidateId(jobId);
        Entry? entry;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _active.TryGetValue(jobId, out entry); }
        if (entry is not null)
        {
            try { entry.Stop.Cancel(); }
            catch (ObjectDisposedException) { /* Completion won the cancellation race; the terminal receipt remains authoritative. */ }
        }
        return Get(jobId);
    }

    /// <summary>
    /// Deletes one terminal job and its entire owned staging tree. Active jobs and jobs whose
    /// verification readback is in progress are refused, and the deletion re-checks the root prefix
    /// and every reparse point before removing anything.
    /// </summary>
    /// <returns><see langword="true"/> when the job existed and was deleted; <see langword="false"/> when nothing owns the id.</returns>
    public bool Remove(string jobId)
    {
        ValidateId(jobId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active.ContainsKey(jobId)) throw new InvalidOperationException("distribution_job_active");
            if (_inspections.ContainsKey(jobId)) throw new InvalidOperationException("distribution_job_in_use");
            if (!Directory.Exists(Path.Combine(_root, jobId))) return false;
            var directory = OwnedDirectory(jobId);
            DeleteOwnedTree(directory);
            return true;
        }
    }

    /// <summary>
    /// Runs one job's phase machine: create the owned directory, persist the preparing receipt, hand
    /// the plan to package preparation, reserve the transfer's bytes, copy every file under the
    /// reservation, then persist the inventory and the staged receipt. Any failure removes the
    /// reserved bytes, deletes the copied objects and writes a failed or cancelled receipt; unexpected
    /// exception kinds rethrow after cleanup so the supervisor observes them.
    /// </summary>
    private async ValueTask ExecuteAsync(Entry entry, CancellationToken supervisorToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(supervisorToken, entry.Stop.Token, _stop.Token);
        var token = linked.Token;
        var directory = Path.Combine(_root, entry.Snapshot.Id);
        var created = false;
        try
        {
            RejectLink(_root);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("A download job directory already exists.");
            Directory.CreateDirectory(directory);
            created = true;
            File.WriteAllText(Path.Combine(directory, "owner"), entry.Snapshot.Id);
            WriteReceipt(directory, entry.Snapshot);
            entry.Recorded.TrySetResult(true);
            token.ThrowIfCancellationRequested();
            var prepared = await _packages.PrepareAsync(entry.Snapshot.PlanId, token).ConfigureAwait(false);
            if (prepared.Selection.Source != entry.Selection.Source || prepared.Selection.SourceRevision != entry.Selection.SourceRevision ||
                prepared.Selection.Selection.SelectionFingerprint != entry.Selection.Selection.SelectionFingerprint || prepared.Selection.Asset != entry.Selection.Asset ||
                prepared.Selection.Selection.Package.Identity != entry.Snapshot.Package || prepared.Selection.Selection.Package.Version != entry.Snapshot.Version)
                throw new InvalidOperationException("distribution_selection_changed");
            if (!_plans.Remove(entry.Snapshot.PlanId)) throw new InvalidOperationException("distribution_plan_withdrawn");
            Reserve(entry, prepared.TotalBytes);
            Update(entry, state => state with { Phase = "transferring", TotalBytes = prepared.TotalBytes, FileCount = prepared.Files.Count });
            var objects = Path.Combine(directory, "objects");
            Directory.CreateDirectory(objects);
            for (var index = 0; index < prepared.Files.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var file = prepared.Files[index];
                var path = Path.Combine(objects, index.ToString("D8", CultureInfo.InvariantCulture) + ".blob");
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    using var progress = new ProgressStream(output, bytes => Progress(entry, bytes));
                    if (file.Asset is { } asset) await _transport.CopyRepositoryAssetAsync(asset, progress, token).ConfigureAwait(false);
                    else await _transport.CopyFileAsync(prepared.Selection.Source, file.SourcePath ?? throw new InvalidDataException("A source file path is missing."),
                        file.Size, file.Sha256, progress, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                Update(entry, state => state with { CompletedFiles = state.CompletedFiles + 1 });
            }
            token.ThrowIfCancellationRequested();
            // Downloaded blobs stay opaque. No archive is extracted and no running plugin/application path is replaced here.
            AtomicJson(Path.Combine(directory, "inventory.json"), prepared);
            token.ThrowIfCancellationRequested();
            DistributionDownloadJob ready;
            lock (_gate) ready = entry.Snapshot with { Phase = "staged", UpdatedAt = DateTimeOffset.UtcNow };
            WriteReceipt(directory, ready);
            lock (_gate) { entry.Snapshot = ready; Finish(entry); }
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            var code = cancelled ? null : error switch
            {
                InvalidDataException => "distribution_integrity_failed",
                HttpRequestException => "distribution_download_unavailable",
                OperationCanceledException => "distribution_download_timeout",
                UnauthorizedAccessException or IOException => "distribution_storage_failed",
                InvalidOperationException when error.Message.StartsWith("distribution_", StringComparison.Ordinal) => error.Message,
                NotSupportedException => "distribution_release_format_unavailable",
                _ => "distribution_preparation_failed"
            };
            DistributionDownloadJob failed;
            lock (_gate) failed = entry.Snapshot with { Phase = cancelled ? "cancelled" : "failed", ErrorCode = code, UpdatedAt = DateTimeOffset.UtcNow };
            Update(entry, state => state with { Phase = "cleaning" });
            if (created)
            {
                try
                {
                    var objects = Path.Combine(directory, "objects");
                    if (Directory.Exists(objects)) DeleteOwnedTree(objects);
                    var inventory = Path.Combine(directory, "inventory.json");
                    if (File.Exists(inventory)) { RejectLink(inventory); File.Delete(inventory); }
                    WriteReceipt(directory, failed);
                    entry.Recorded.TrySetResult(true);
                }
                catch (Exception cleanup) { throw new AggregateException("Download cleanup failed.", error, cleanup); }
            }
            lock (_gate) { entry.Snapshot = failed; Finish(entry); }
            if (error is not (OperationCanceledException or InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException or
                ArgumentException or NotSupportedException or KeyNotFoundException) &&
                (error is not InvalidOperationException || !error.Message.StartsWith("distribution_", StringComparison.Ordinal))) throw;
        }
        finally { Finish(entry); }
    }

    private void Reserve(Entry entry, long bytes)
    {
        if (bytes < 0) throw new InvalidDataException("The download total is invalid.");
        lock (_gate)
        {
            var available = _availableSpace(_root);
            if (_reserve > available || _reservedBytes > available - _reserve || bytes > available - _reserve - _reservedBytes)
                throw new IOException("The staging volume has insufficient unreserved free space.");
            _reservedBytes = checked(_reservedBytes + bytes);
            entry.RemainingBytes = bytes;
            entry.ReservedBytes = bytes;
        }
    }

    private void Progress(Entry entry, int bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > entry.RemainingBytes) throw new InvalidDataException("The transfer exceeded its reserved bytes.");
            entry.RemainingBytes -= bytes;
            entry.Snapshot = entry.Snapshot with { BytesReceived = checked(entry.Snapshot.BytesReceived + bytes), UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    private DistributionDownloadJob Update(Entry entry, Func<DistributionDownloadJob, DistributionDownloadJob> update)
    {
        lock (_gate) return entry.Snapshot = update(entry.Snapshot) with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    private void Finish(Entry entry)
    {
        lock (_gate)
        {
            if (!_active.Remove(entry.Snapshot.Id)) return;
            entry.Recorded.TrySetResult(false);
            _byPlan.Remove(entry.Snapshot.PlanId);
            _reservedBytes -= entry.ReservedBytes;
            entry.RemainingBytes = 0;
            entry.ReservedBytes = 0;
            entry.Stop.Dispose();
            SignalDrained();
        }
    }

    private void SignalDrained() { if (_disposed && _active.Count == 0 && _admissions == 0 && _inspections.Count == 0) _drained.TrySetResult(); }

    private string OwnedDirectory(string jobId)
    {
        var path = Path.Combine(_root, jobId);
        if (!Directory.Exists(path)) throw new KeyNotFoundException("The download job is missing.");
        RejectLink(_root);
        RejectLink(path);
        var marker = Path.Combine(path, "owner");
        RejectLink(marker);
        if (new FileInfo(marker).Length != 32) throw new InvalidDataException("The staging ownership marker is malformed.");
        if (File.ReadAllText(marker) != jobId) throw new InvalidDataException("The download directory is not owned by this job.");
        return path;
    }

    private static void ValidateId(string id)
    {
        if (id is not { Length: 32 } || id.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("A download job ID must be a hexadecimal GUID.");
    }
    private static bool Terminal(string phase) => phase is "staged" or "failed" or "cancelled" or "interrupted";

    private static void ValidateReceipt(DistributionDownloadJob snapshot, string jobId)
    {
        if (snapshot.Id != jobId || !Printable(snapshot.PlanId) || !Printable(snapshot.SourceId) || !Printable(snapshot.Version) ||
            snapshot.Package is not { Target: "server", Kind: "plugin" or "application" } package || !Printable(package.Id) ||
            package.Game is null || package.Game.Any(char.IsControl) ||
            snapshot.Phase is not ("preparing" or "transferring" or "cleaning" or "staged" or "failed" or "cancelled" or "interrupted") ||
            snapshot.BytesReceived < 0 || snapshot.TotalBytes < snapshot.BytesReceived || snapshot.CompletedFiles < 0 ||
            snapshot.FileCount < snapshot.CompletedFiles || snapshot.UpdatedAt < snapshot.CreatedAt ||
            snapshot.Phase == "staged" && (snapshot.BytesReceived != snapshot.TotalBytes || snapshot.CompletedFiles != snapshot.FileCount))
            throw new InvalidDataException("The download receipt is inconsistent with its owned job schema.");
    }

    private static bool Printable(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static void WriteReceipt(string directory, DistributionDownloadJob snapshot) => AtomicJson(Path.Combine(directory, "receipt.json"), snapshot);
    private static void AtomicJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, value); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Staging cannot traverse a symbolic link or junction.");
    }
    private void DeleteOwnedTree(string directory)
    {
        var absolute = Path.GetFullPath(directory);
        var prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("A staging cleanup target is outside the owned root.");
        RejectLink(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(path);
            if (Directory.Exists(path)) DeleteOwnedTree(path);
            else File.Delete(path);
        }
        Directory.Delete(directory);
    }

    /// <summary>
    /// Stops new admissions and waits until every live job, in-flight admission and verification
    /// readback has drained, then releases the staging root lock. Safe to call once; repeated calls
    /// return the same drain task.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            SignalDrained();
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { _stop.Cancel(); }
        finally
        {
            try { await _drained.Task.ConfigureAwait(false); }
            finally { _rootLock.Dispose(); _stop.Dispose(); }
        }
    }

    private sealed class Entry(DistributionDownloadJob snapshot, DistributionPlan selection)
    {
        internal DistributionDownloadJob Snapshot = snapshot;
        internal readonly DistributionPlan Selection = selection;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<bool> Recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long RemainingBytes;
        internal long ReservedBytes;
    }

    private sealed class ProgressStream(Stream output, Action<int> progress) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            progress(buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count) { output.Write(buffer, offset, count); progress(count); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
