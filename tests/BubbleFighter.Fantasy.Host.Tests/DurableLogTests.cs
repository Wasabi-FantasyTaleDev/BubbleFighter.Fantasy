using System.Collections.Concurrent;
using System.Threading.Channels;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Host.Tests;

/// <summary>Checks retained exception evidence and the host logger's resource and filtering boundaries.</summary>
public sealed class DurableLogTests
{
    /// <summary>An unset file path leaves ordinary logging alone without creating a writer.</summary>
    [Fact]
    public void NoPathDisablesTheProvider()
    {
        Assert.Null(DurableLogOptions.Parse([], _ => null));
        Assert.Null(DurableLogOptions.Parse([], name => name == "BFF_LOG_FILE" ? "" : "invalid-unused-setting"));
    }

    /// <summary>Explicit path and level flags override the environment while resource defaults remain bounded.</summary>
    [Fact]
    public void CommandLineOverridesEnvironment()
    {
        var options = DurableLogOptions.Parse(["run", "--log-file", "chosen.log", "--log-file-level", "Error"],
            name => name switch { "BFF_LOG_FILE" => "environment.log", "BFF_LOG_FILE_LEVEL" => "Debug", _ => null });
        Assert.NotNull(options);
        Assert.Equal("chosen.log", options.File);
        Assert.Equal(LogLevel.Error, options.Level);
        Assert.Equal(16 * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(5, options.FileCount);
        Assert.Equal(256, options.QueueCapacity);
        Assert.Equal(16 * 1024 * 1024, options.MaxQueuedBytes);
        Assert.Equal(5000, options.ShutdownTimeoutMs);
        Assert.Equal(1000, options.RetryInitialMs);
        Assert.Equal(60000, options.RetryMaxMs);
        Assert.Equal(LogLevel.Warning, DurableLogOptions.Parse(["--log-file", "default.log"], _ => null)!.Level);
    }

    /// <summary>Malformed resource values and undefined logging levels fail before a destination is opened.</summary>
    [Theory]
    [InlineData("BFF_LOG_FILE_MAX_BYTES", "0")]
    [InlineData("BFF_LOG_FILE_COUNT", "33")]
    [InlineData("BFF_LOG_FILE_QUEUE_CAPACITY", "0")]
    [InlineData("BFF_LOG_FILE_MAX_QUEUED_BYTES", "unbounded")]
    [InlineData("BFF_LOG_FILE_SHUTDOWN_TIMEOUT_MS", "0")]
    [InlineData("BFF_LOG_FILE_RETRY_INITIAL_MS", "0")]
    [InlineData("BFF_LOG_FILE_RETRY_MAX_MS", "60001")]
    [InlineData("BFF_LOG_FILE_RETRY_MAX_MS", "100")]
    [InlineData("BFF_LOG_FILE_LEVEL", "99")]
    [InlineData("BFF_LOG_FILE_LEVEL", "2")]
    public void InvalidEnvironmentIsRejected(string key, string value)
    {
        Assert.Throws<ConfigException>(() => DurableLogOptions.Parse(["--log-file", "file.log"], name => name == key ? value : null));
    }

    /// <summary>A missing flag value cannot accidentally become a profile or another logging option.</summary>
    [Theory]
    [InlineData("--log-file")]
    [InlineData("--log-file-level")]
    public void MissingCommandLineValueIsRejected(string option)
    {
        Assert.Throws<ConfigException>(() => DurableLogOptions.Parse([option], _ => null));
        Assert.Throws<ConfigException>(() => DurableLogOptions.Parse([option, "--profile", "kr-1256"], _ => null));
    }

    /// <summary>Rotation retains only the configured fixed slots, including after a restart reduces the count.</summary>
    [Fact]
    public async Task RotationAndRestartRespectTheRetentionCount()
    {
        using var directory = new LogDirectory();
        var options = directory.Options with { MaxFileBytes = 1024, FileCount = 3 };
        var first = new RotatingFileLoggerProvider(options);
        var logger = first.CreateLogger("rotation");
        for (var index = 0; index < 5; index++) logger.LogWarning("event-{Index} {Payload}", index, new string('a', 650));
        await first.DisposeAsync();
        Assert.Equal(3, directory.LogFiles.Length);
        Assert.All(directory.LogFiles, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        var retained = directory.ReadAll();
        Assert.DoesNotContain("event-0", retained);
        Assert.DoesNotContain("event-1", retained);
        Assert.Contains("event-2", retained);
        Assert.Contains("event-4", retained);

        var restarted = new RotatingFileLoggerProvider(options with { FileCount = 2 });
        restarted.CreateLogger("rotation").LogWarning("after-restart {Payload}", new string('b', 650));
        await restarted.DisposeAsync();
        Assert.Equal(2, directory.LogFiles.Length);
        retained = directory.ReadAll();
        Assert.DoesNotContain("event-2", retained);
        Assert.DoesNotContain("event-3", retained);
        Assert.Contains("event-4", retained);
        Assert.Contains("after-restart", retained);
        Assert.Equal(0, restarted.QueuedBytes);
    }

    /// <summary>A single retained slot still rotates without creating a backup or growing past its byte limit.</summary>
    [Fact]
    public async Task OneFileRetentionReplacesTheActiveFile()
    {
        using var directory = new LogDirectory();
        var provider = new RotatingFileLoggerProvider(directory.Options with { MaxFileBytes = 1024, FileCount = 1 });
        var logger = provider.CreateLogger("single");
        logger.LogWarning("old {Payload}", new string('a', 650));
        logger.LogWarning("new {Payload}", new string('b', 650));
        await provider.DisposeAsync();
        Assert.Single(directory.LogFiles);
        Assert.DoesNotContain("old ", directory.ReadAll());
        Assert.Contains("new ", directory.ReadAll());
    }

    /// <summary>Queued exception evidence preserves the entire outer and inner exception text and throw-site stacks.</summary>
    [Fact]
    public async Task FullExceptionIsRetained()
    {
        using var directory = new LogDirectory();
        var failure = CreateFailure();
        var provider = new RotatingFileLoggerProvider(directory.Options);
        provider.CreateLogger("purchase.publication").LogError(failure, "delivery publication failed");
        await provider.DisposeAsync();
        var text = directory.ReadAll();
        Assert.Contains("purchase.publication", text);
        Assert.Contains("delivery publication failed", text);
        Assert.Contains(failure.ToString(), text);
        Assert.Contains(nameof(CreateFailure), text);
        Assert.Contains("inner storage failure", text);
    }

    /// <summary>File thresholds and category overrides do not lower the threshold of other factory providers.</summary>
    [Fact]
    public async Task NormalProviderAndCategoryFiltersRemainIndependent()
    {
        using var directory = new LogDirectory();
        var provider = new RotatingFileLoggerProvider(directory.Options);
        var other = new ProbeProvider();
        using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Critical)
            .AddProvider(other).AddDurableFile(provider)
            .AddFilter<RotatingFileLoggerProvider>("hidden", LogLevel.None)
            .AddFilter<RotatingFileLoggerProvider>("verbose", LogLevel.Debug)))
        {
            factory.CreateLogger("normal").LogInformation("filtered-information");
            factory.CreateLogger("normal").LogWarning("file-warning");
            factory.CreateLogger("normal").LogCritical("both-critical");
            factory.CreateLogger("hidden").LogWarning("hidden-warning");
            factory.CreateLogger("verbose").LogDebug("category-debug");
        }
        await provider.DisposeAsync();
        var text = directory.ReadAll();
        Assert.DoesNotContain("filtered-information", text);
        Assert.DoesNotContain("hidden-warning", text);
        Assert.Contains("file-warning", text);
        Assert.Contains("both-critical", text);
        Assert.Contains("category-debug", text);
        Assert.Equal("both-critical", Assert.Single(other.Messages));
    }

    /// <summary>A blocked writer cannot stall log callers; channel overflow drops the newest payload and counts it.</summary>
    [Fact]
    public async Task QueueOverflowDropsNewestWithoutWaitingForDisk()
    {
        using var directory = new LogDirectory();
        var entered = Signal();
        var release = Signal();
        var writes = 0;
        var provider = new RotatingFileLoggerProvider(directory.Options with { QueueCapacity = 1 }, async ct =>
        {
            if (Interlocked.Increment(ref writes) != 1) return;
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        try
        {
            var logger = provider.CreateLogger("overflow");
            logger.LogWarning("inflight");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            logger.LogWarning("queued");
            logger.LogWarning("lost-newest");
            Assert.False(release.Task.IsCompleted);
            Assert.InRange(provider.QueuedBytes, 1, directory.Options.MaxQueuedBytes);
        }
        finally { release.TrySetResult(); await provider.DisposeAsync(); }
        var text = directory.ReadAll();
        Assert.Contains("inflight", text);
        Assert.Contains("queued", text);
        Assert.DoesNotContain("lost-newest", text);
        Assert.Contains("dropped_entries=1", text);
        Assert.Equal(0, provider.QueuedBytes);
    }

    /// <summary>The byte budget bounds large payloads even when the channel still has spare record slots.</summary>
    [Fact]
    public async Task QueuedBytesHaveAnIndependentLimit()
    {
        using var directory = new LogDirectory();
        var entered = Signal();
        var release = Signal();
        var provider = new RotatingFileLoggerProvider(directory.Options with { MaxQueuedBytes = 1024 }, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        try
        {
            var logger = provider.CreateLogger("bytes");
            logger.LogWarning("accepted {Payload}", new string('a', 650));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            logger.LogWarning("byte-budget-rejected {Payload}", new string('b', 650));
            Assert.InRange(provider.QueuedBytes, 1, 1024);
        }
        finally { release.TrySetResult(); await provider.DisposeAsync(); }
        Assert.Contains("accepted", directory.ReadAll());
        Assert.DoesNotContain("byte-budget-rejected", directory.ReadAll());
        Assert.Contains("dropped_entries=1", directory.ReadAll());
        Assert.Equal(0, provider.QueuedBytes);
    }

    /// <summary>UTF-8 byte limits reject an oversized record completely and retain a loss report instead of partial text.</summary>
    [Fact]
    public async Task OversizedUtf8EntryIsCountedWithoutTruncation()
    {
        using var directory = new LogDirectory();
        var provider = new RotatingFileLoggerProvider(directory.Options with { MaxFileBytes = 1024 });
        provider.CreateLogger("utf8").LogWarning("{Payload}", new string('\uD55C', 400));
        await provider.DisposeAsync();
        Assert.DoesNotContain("\uD55C", directory.ReadAll());
        Assert.Contains("dropped_entries=1", directory.ReadAll());
        Assert.Equal(0, provider.QueuedBytes);
    }

    /// <summary>Warning evidence is flushed while the process is still running, before the following write begins.</summary>
    [Fact]
    public async Task WarningIsReadableBeforeShutdown()
    {
        using var directory = new LogDirectory();
        var secondEntered = Signal();
        var release = Signal();
        var writes = 0;
        var provider = new RotatingFileLoggerProvider(directory.Options, async ct =>
        {
            if (Interlocked.Increment(ref writes) != 2) return;
            secondEntered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        try
        {
            var logger = provider.CreateLogger("flush");
            logger.LogWarning("flushed-warning");
            logger.LogWarning("blocked-following-write");
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var stream = new FileStream(directory.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            Assert.Contains("flushed-warning", text);
            Assert.DoesNotContain("blocked-following-write", text);
        }
        finally { release.TrySetResult(); await provider.DisposeAsync(); }
    }

    /// <summary>A blocked shutdown ends visibly within its budget rather than claiming every record was drained.</summary>
    [Fact]
    public async Task ShutdownHasABoundedFailurePath()
    {
        using var directory = new LogDirectory();
        var entered = Signal();
        var release = Signal();
        var provider = new RotatingFileLoggerProvider(directory.Options with { ShutdownTimeoutMs = 100 }, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        provider.CreateLogger("shutdown").LogWarning("waiting-write");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Assert.ThrowsAsync<IOException>(() => provider.DisposeAsync().AsTask()); }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Completion);
        }
        Assert.Equal(0, provider.QueuedBytes);
    }

    /// <summary>A transient writer failure drops outage records, releases byte reservations and resumes after reopening.</summary>
    [Fact]
    public async Task RuntimeWriterFailureRecoversWithoutClosingAdmission()
    {
        using var directory = new LogDirectory();
        var failure = new IOException("fixture writer failure");
        var retryEntered = Signal();
        var release = Signal();
        var writes = 0;
        var provider = new RotatingFileLoggerProvider(directory.Options,
            _ => Interlocked.Increment(ref writes) == 1 ? ValueTask.FromException(failure) : ValueTask.CompletedTask,
            async (duration, ct) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(1), duration);
                retryEntered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });
        try
        {
            var logger = provider.CreateLogger("writer");
            logger.LogWarning("cannot-write");
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var recovered = provider.Recovery;
            logger.LogWarning("during-outage");
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(provider.Completion.IsCompleted);
            Assert.Equal(0, provider.QueuedBytes);
            release.TrySetResult();
            await recovered.WaitAsync(TimeSpan.FromSeconds(10));
            logger.LogWarning("after-recovery");
        }
        finally { release.TrySetResult(); await provider.DisposeAsync(); }
        await provider.DisposeAsync();
        var text = directory.ReadAll();
        Assert.DoesNotContain("cannot-write", text);
        Assert.DoesNotContain("during-outage", text);
        Assert.Contains("dropped_entries=2", text);
        Assert.Contains("after-recovery", text);
        Assert.True(text.IndexOf("dropped_entries=2", StringComparison.Ordinal)
            < text.IndexOf("after-recovery", StringComparison.Ordinal));
        Assert.Equal(0, provider.QueuedBytes);
    }

    /// <summary>On Windows, a viewer without delete sharing blocks rotation until released, then the writer recovers with capped backoff.</summary>
    [Fact]
    public async Task WindowsViewerLockRecoversWithoutRepeatingBackupMoves()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new LogDirectory();
        var original = new string('a', 850) + "\n";
        System.IO.File.WriteAllText(directory.File, original);
        System.IO.File.WriteAllText(Path.Combine(directory.Root, "server.1.log"), "prior-backup");
        System.IO.File.WriteAllText(Path.Combine(directory.Root, "server.2.log"), "oldest-backup");
        var requests = Channel.CreateBounded<(TimeSpan Duration, TaskCompletionSource Release)>(1);
        var cleaning = 0;
        var provider = new RotatingFileLoggerProvider(directory.Options with { MaxFileBytes = 1024, FileCount = 3 },
            retryDelay: async (duration, ct) =>
            {
                if (Volatile.Read(ref cleaning) != 0) return;
                var release = Signal();
                await requests.Writer.WriteAsync((duration, release), ct);
                await release.Task.WaitAsync(ct);
            });
        using var viewer = new FileStream(directory.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        TaskCompletionSource? currentRelease = null;
        try
        {
            var logger = provider.CreateLogger("viewer-lock");
            logger.LogWarning("rotation-rejected {Payload}", new string('b', 650));
            Task? recovered = null;
            int[] expected = [1000, 2000, 4000, 8000, 16000, 32000, 60000, 60000];
            for (var index = 0; index < expected.Length; index++)
            {
                var request = await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                currentRelease = request.Release;
                Assert.Equal(TimeSpan.FromMilliseconds(expected[index]), request.Duration);
                if (index == 0)
                {
                    recovered = provider.Recovery;
                    logger.LogWarning("during-viewer-lock");
                    Assert.Equal(0, provider.QueuedBytes);
                }
                Assert.Equal(original, System.IO.File.ReadAllText(directory.File));
                Assert.Equal("prior-backup", System.IO.File.ReadAllText(Path.Combine(directory.Root, "server.2.log")));
                if (index + 1 == expected.Length) viewer.Dispose();
                currentRelease.TrySetResult();
            }
            await recovered!.WaitAsync(TimeSpan.FromSeconds(10));
            logger.LogWarning("after-viewer-release");
        }
        finally
        {
            Volatile.Write(ref cleaning, 1);
            viewer.Dispose();
            currentRelease?.TrySetResult();
            while (requests.Reader.TryRead(out var pending)) pending.Release.TrySetResult();
            await provider.DisposeAsync();
        }
        var text = directory.ReadAll();
        Assert.DoesNotContain("rotation-rejected", text);
        Assert.DoesNotContain("during-viewer-lock", text);
        Assert.DoesNotContain("oldest-backup", text);
        Assert.Contains("prior-backup", text);
        Assert.Contains("dropped_entries=2", text);
        Assert.Contains("after-viewer-release", text);
        Assert.Equal(0, provider.QueuedBytes);
    }

    /// <summary>An unusable startup destination fails synchronously without creating a hidden background failure.</summary>
    [Fact]
    public void BadStartupPathIsAnError()
    {
        using var directory = new LogDirectory();
        var blocker = Path.Combine(directory.Root, "not-a-directory");
        System.IO.File.WriteAllText(blocker, "owned fixture");
        Assert.ThrowsAny<IOException>(() => new RotatingFileLoggerProvider(directory.Options with { File = Path.Combine(blocker, "server.log") }));
    }

    /// <summary>An active owner holds the rotation lock so another writer cannot claim the same retained files.</summary>
    [Fact]
    public async Task ConcurrentOwnersCannotShareTheSameLog()
    {
        using var directory = new LogDirectory();
        var provider = new RotatingFileLoggerProvider(directory.Options);
        try { Assert.ThrowsAny<IOException>(() => new RotatingFileLoggerProvider(directory.Options)); }
        finally { await provider.DisposeAsync(); }
    }

    /// <summary>A smaller startup size bound rotates existing active evidence into the first backup and replaces the oldest slot.</summary>
    [Fact]
    public async Task ExistingOversizedActiveEvidenceRotatesAtStartup()
    {
        using var directory = new LogDirectory();
        var evidence = new string('x', 2048);
        var backup = Path.Combine(directory.Root, "server.1.log");
        System.IO.File.WriteAllText(directory.File, evidence);
        System.IO.File.WriteAllText(backup, "older evidence");
        var provider = new RotatingFileLoggerProvider(directory.Options with { MaxFileBytes = 1024, FileCount = 2 });
        provider.CreateLogger("startup").LogWarning("after-startup-rotation");
        await provider.DisposeAsync();
        Assert.Equal(evidence, System.IO.File.ReadAllText(backup));
        Assert.Contains("after-startup-rotation", System.IO.File.ReadAllText(directory.File));
        Assert.DoesNotContain("older evidence", directory.ReadAll());
        Assert.Equal(2, directory.LogFiles.Length);
        var restarted = new RotatingFileLoggerProvider(directory.Options with { MaxFileBytes = 1024, FileCount = 2 });
        restarted.CreateLogger("startup").LogWarning("existing-oversized-backup-is-allowed");
        await restarted.DisposeAsync();
        Assert.Equal(evidence, System.IO.File.ReadAllText(backup));
        Assert.Contains("existing-oversized-backup-is-allowed", System.IO.File.ReadAllText(directory.File));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Exception CreateFailure()
    {
        try
        {
            try { throw new IOException("inner storage failure"); }
            catch (IOException inner) { throw new InvalidOperationException("outer publication failure", inner); }
        }
        catch (Exception failure) { return failure; }
    }

    private sealed class ProbeProvider : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new ProbeLogger(Messages);
        public void Dispose() { }
        private sealed class ProbeLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class LogDirectory : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bff-host-log-tests-" + Guid.NewGuid().ToString("N"));
        internal string File => Path.Combine(Root, "server.log");
        internal DurableLogOptions Options => new() { File = File };
        internal string[] LogFiles => Directory.GetFiles(Root, "*.log");
        internal LogDirectory() => Directory.CreateDirectory(Root);
        internal string ReadAll() => string.Join("\n", LogFiles.Select(System.IO.File.ReadAllText));
        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(target), Path.TrimEndingDirectorySeparator(parent),
                StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("bff-host-log-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("The fixture cleanup target is outside its temporary directory.");
            Directory.Delete(target, true);
        }
    }
}
