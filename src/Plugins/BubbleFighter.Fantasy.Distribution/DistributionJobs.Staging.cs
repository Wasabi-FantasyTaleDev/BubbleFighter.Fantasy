using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

internal sealed partial class DistributionJobs
{
    /// <summary>
    /// Re-reads one staged job's full inventory and every payload byte, and reports what the staging
    /// volume actually holds. Only a staged job can be verified, one readback per job runs at a time
    /// (a concurrent removal is refused while the readback owns the job), and the persisted inventory
    /// must agree with the receipt — same plan, source, package, version, file count and total — with
    /// no duplicated or nested destination. Every blob is re-hashed and compared by constant time
    /// against its declared SHA-256. A passing result still grants no installation authority; it only
    /// states what was verified at that instant.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the job is not staged.</exception>
    /// <exception cref="InvalidDataException">Thrown when the inventory or any payload disagrees with its declared identity.</exception>
    public async ValueTask<DistributionStagingVerification> VerifyAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ValidateId(jobId);
        DistributionDownloadJob job;
        string directory;
        CancellationToken lifetime;
        lock (_gate)
        {
            job = Get(jobId);
            if (job.Phase != "staged") throw new InvalidOperationException("distribution_job_not_staged");
            cancellationToken.ThrowIfCancellationRequested();
            directory = OwnedDirectory(jobId);
            lifetime = _stop.Token;
            _inspections.TryGetValue(jobId, out var inspections);
            _inspections[jobId] = checked(inspections + 1);
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            var token = linked.Token;
            var inventoryPath = Path.Combine(directory, "inventory.json");
            RejectLink(inventoryPath);
            DistributionFilePlan inventory;
            await using (var input = new FileStream(inventoryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                inventory = await JsonSerializer.DeserializeAsync<DistributionFilePlan>(input, cancellationToken: token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The staged inventory is empty.");
            if (inventory.Selection?.Selection?.Package is not { } offer || inventory.Selection.Source is null ||
                inventory.Selection.Id != job.PlanId || inventory.Selection.Source.Id != job.SourceId ||
                offer.Identity != job.Package || offer.Version != job.Version || inventory.Files is null ||
                inventory.Files.Count != job.FileCount || inventory.TotalBytes != job.TotalBytes || inventory.Files.Count == 0)
                throw new InvalidDataException("The staged inventory disagrees with its download job.");
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in inventory.Files)
            {
                if (file is null || file.Path is null || file.Size < 0 || file.Sha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
                    throw new InvalidDataException("The staged inventory contains an invalid file declaration.");
                DistributionPackages.ValidatePath(file.Path);
                if (!destinations.Add(file.Path)) throw new InvalidDataException("The staged inventory repeats a destination.");
                if (file.Size > job.TotalBytes - total) throw new InvalidDataException("The staged inventory exceeds its recorded total.");
                total += file.Size;
            }
            if (total != job.TotalBytes) throw new InvalidDataException("The staged inventory size disagrees with its receipt.");
            foreach (var destination in destinations)
                for (var i = destination.IndexOf('/'); i >= 0; i = destination.IndexOf('/', i + 1))
                    if (destinations.Contains(destination[..i])) throw new InvalidDataException("A staged destination is also a parent directory.");

            var objects = Path.Combine(directory, "objects");
            RejectLink(objects);
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                for (var index = 0; index < inventory.Files.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var file = inventory.Files[index];
                    var path = Path.Combine(objects, index.ToString("D8", CultureInfo.InvariantCulture) + ".blob");
                    RejectLink(path);
                    await using var input = _openBlob(path);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long length = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (count > file.Size - length) throw new InvalidDataException("A staged file exceeds its declared size.");
                        length += count;
                        hash.AppendData(buffer, 0, count);
                    }
                    if (length != file.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                        throw new InvalidDataException("A staged file no longer matches its declared size or digest.");
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            token.ThrowIfCancellationRequested();
            return new(jobId, inventory.Files.Count, total, DateTimeOffset.UtcNow);
        }
        finally
        {
            lock (_gate)
            {
                if (_inspections[jobId] == 1) _inspections.Remove(jobId);
                else _inspections[jobId]--;
                SignalDrained();
            }
        }
    }
}
