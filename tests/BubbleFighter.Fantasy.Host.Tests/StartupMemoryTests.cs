using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Host.Tests;

/// <summary>Checks that the startup release returns dead large allocations and reports what it did.</summary>
public sealed class StartupMemoryTests
{
    /// <summary>Space held by unreachable large arrays is no longer committed after the release.</summary>
    [Fact]
    public void ReleaseReturnsDeadLargeObjectSpace()
    {
        const int arrays = 48, bytes = 4 * 1024 * 1024;
        var survivor = AllocateAndDrop(arrays, bytes);
        // A non-compacting collection frees the arrays but may keep their space committed for reuse.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

        var report = StartupMemory.Release();

        Assert.True(report.AllocatedBytes >= (long)arrays * bytes, $"allocated {report.AllocatedBytes}");
        Assert.True(report.Collections >= 1);
        Assert.True(report.CommittedAfterBytes > 0);
        // Half of the dropped bytes is far above anything a parallel test can allocate in the same instant.
        Assert.True(report.CommittedAfterBytes < report.CommittedBeforeBytes + (long)arrays * bytes / 2,
            $"committed {report.CommittedBeforeBytes} -> {report.CommittedAfterBytes}");
        Assert.True(GC.GetTotalMemory(forceFullCollection: false) < (long)arrays * bytes / 2 + survivor.Length);
        GC.KeepAlive(survivor);
    }

    /// <summary>The log entry carries the numbers the performance harness reads.</summary>
    [Fact]
    public void ReleaseIsLoggedWithItsTotals()
    {
        var logger = new CollectingLogger();

        var report = StartupMemory.ReleaseAndLog(logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Matches(@"^Startup allocated \d+ MB in \d+ collection\(s\); the managed heap was compacted from \d+ MB to \d+ MB committed in \d+ ms\.$",
            entry.Message);
        Assert.Contains($"to {(report.CommittedAfterBytes + 512 * 1024) / (1024 * 1024)} MB committed", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>A missing logger is a caller error, not a silent release.</summary>
    [Fact]
    public void ReleaseAndLogRequiresALogger() => Assert.Throws<ArgumentNullException>(() => StartupMemory.ReleaseAndLog(null!));

    // Kept out of line so that no local of the caller keeps the arrays reachable.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static byte[] AllocateAndDrop(int arrays, int bytes)
    {
        var held = new byte[arrays][];
        for (var i = 0; i < arrays; i++)
        {
            held[i] = new byte[bytes];
            held[i][bytes - 1] = 1;
        }
        return new byte[1024];
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
