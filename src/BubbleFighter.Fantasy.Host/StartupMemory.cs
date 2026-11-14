using System.Diagnostics;
using System.Runtime;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Gives back the memory that starting the server used and no longer needs.</summary>
/// <remarks>
/// Activating a profile parses client data and packet tables that are many times larger than what stays
/// loaded. The runtime keeps that space committed until a later full collection happens to compact it,
/// which on an idle server may be hours away. One compacting collection at the moment the rows have
/// converged, before any player is admitted, makes the small footprint the normal case.
/// </remarks>
internal static class StartupMemory
{
    /// <summary>What starting the server allocated and what one release left committed.</summary>
    /// <param name="AllocatedBytes">Bytes the process allocated on the managed heap since it started.</param>
    /// <param name="Collections">Garbage collections that ran since the process started, before the release.</param>
    /// <param name="CommittedBeforeBytes">Managed heap bytes committed after the last collection before the release.</param>
    /// <param name="CommittedAfterBytes">Managed heap bytes committed after the release.</param>
    /// <param name="Pause">How long the release blocked the calling thread.</param>
    public readonly record struct Report(long AllocatedBytes, int Collections, long CommittedBeforeBytes, long CommittedAfterBytes,
        TimeSpan Pause);

    /// <summary>Runs one blocking, compacting collection of every generation, including the large object heap.</summary>
    /// <returns>The allocation totals of the start and the committed heap before and after.</returns>
    public static Report Release()
    {
        var allocated = GC.GetTotalAllocatedBytes();
        var collections = GC.CollectionCount(0);
        var committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        var watch = Stopwatch.StartNew();
        // The large object heap is swept, not compacted, unless asked: the parsed workbooks leave it mostly holes.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Aggressive also decommits the freed space instead of keeping it for the next allocation burst.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        watch.Stop();
        return new(allocated, collections, committedBefore, GC.GetGCMemoryInfo().TotalCommittedBytes, watch.Elapsed);
    }

    /// <summary>Releases startup memory and logs what the start cost.</summary>
    /// <param name="logger">Receives one information entry with the totals.</param>
    /// <returns>The same totals that were logged.</returns>
    public static Report ReleaseAndLog(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var report = Release();
        logger.LogInformation(
            "Startup allocated {AllocatedMb} MB in {Collections} collection(s); the managed heap was compacted from {BeforeMb} MB to {AfterMb} MB committed in {PauseMs} ms.",
            Megabytes(report.AllocatedBytes), report.Collections, Megabytes(report.CommittedBeforeBytes),
            Megabytes(report.CommittedAfterBytes), (long)report.Pause.TotalMilliseconds);
        return report;
    }

    private static long Megabytes(long bytes) => (bytes + 512 * 1024) / (1024 * 1024);
}
