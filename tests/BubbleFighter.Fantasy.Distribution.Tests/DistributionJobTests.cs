using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionJobTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SuccessfulJobPersistsOpaqueVerifiedBlobsWithoutInstallingTheirDestinationNames()
    {
        await using var fixture = new Fixture();
        var accepted = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        var result = fixture.Jobs.Get(accepted.Id);
        Assert.Equal("staged", result.Phase);
        Assert.Equal(3, result.BytesReceived);
        Assert.Equal(3, result.TotalBytes);
        Assert.Equal(1, result.CompletedFiles);
        Assert.Null(result.ErrorCode);
        Assert.Equal(Fixture.Bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, result.Id, "objects", "00000000.blob")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, result.Id, "inventory.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, result.Id, "plugin.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, result.Id, "files")));
        var verification = await fixture.Jobs.VerifyAsync(result.Id);
        Assert.Equal(result.Id, verification.JobId);
        Assert.Equal(1, verification.Files);
        Assert.Equal(3, verification.Bytes);
    }

    [Fact]
    public async Task CancellationCleansPartialBlobsAndKeepsAnHonestTerminalReceipt()
    {
        await using var fixture = new Fixture();
        fixture.Transport.Block = true;
        var accepted = await fixture.Jobs.StartAsync("plan");
        await fixture.Transport.Started.Task.WaitAsync(Limit);
        Assert.Equal(1, fixture.Jobs.Get(accepted.Id).BytesReceived);
        Assert.Throws<InvalidOperationException>(() => fixture.Jobs.Remove(accepted.Id));
        _ = fixture.Jobs.Cancel(accepted.Id);
        await fixture.Supervisor.WaitAsync();
        var result = fixture.Jobs.Get(accepted.Id);
        Assert.Equal("cancelled", result.Phase);
        Assert.Null(result.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, accepted.Id, "objects")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, accepted.Id, "inventory.json")));
    }

    [Fact]
    public async Task IntegrityFailureNeverPublishesAStagedInventoryAndReturnsReservations()
    {
        await using var fixture = new Fixture();
        fixture.Transport.FailDigest = true;
        var first = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        var failed = fixture.Jobs.Get(first.Id);
        Assert.Equal("failed", failed.Phase);
        Assert.Equal("distribution_integrity_failed", failed.ErrorCode);
        Assert.Equal(0, failed.CompletedFiles);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, first.Id, "objects")));
        fixture.Transport.FailDigest = false;
        var second = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("staged", fixture.Jobs.Get(second.Id).Phase);
    }

    [Fact]
    public async Task SimultaneousRequestsForOneActivePlanShareTheOwnedJob()
    {
        await using var fixture = new Fixture();
        fixture.Transport.Block = true;
        var first = await fixture.Jobs.StartAsync("plan");
        await fixture.Transport.Started.Task.WaitAsync(Limit);
        var second = await fixture.Jobs.StartAsync("plan");
        Assert.Equal(first.Id, second.Id);
        Assert.Single(fixture.Supervisor.Work);
        fixture.Jobs.Cancel(first.Id);
        await fixture.Supervisor.WaitAsync();
    }

    [Fact]
    public async Task PluginDisposalCancelsAndDrainsTheTransferBeforeReleasingTheDirectory()
    {
        await using var fixture = new Fixture();
        fixture.Transport.Block = true;
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Transport.Started.Task.WaitAsync(Limit);
        await fixture.Jobs.DisposeAsync().AsTask().WaitAsync(Limit);
        await fixture.Supervisor.WaitAsync();
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, job.Id, "objects")));
        await using var restored = fixture.CreateJobs();
        Assert.Equal("cancelled", restored.Get(job.Id).Phase);
    }

    [Fact]
    public async Task RootIsExclusiveAndTerminalReceiptsSurviveAServiceRestart()
    {
        await using var fixture = new Fixture();
        Assert.Throws<IOException>(() => fixture.CreateJobs());
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        await fixture.Jobs.DisposeAsync();
        await using var restored = fixture.CreateJobs();
        Assert.Equal("staged", restored.Get(job.Id).Phase);
        Assert.True(File.Exists(Path.Combine(fixture.Root, job.Id, "objects", "00000000.blob")));
    }

    [Fact]
    public async Task PreviouslyRunningReceiptBecomesInterruptedWithoutAnAutomaticRetryOrDeletion()
    {
        await using var fixture = new Fixture();
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        var state = fixture.Jobs.Get(job.Id);
        await fixture.Jobs.DisposeAsync();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, job.Id, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(state with { Phase = "transferring" }));
        await using var restored = fixture.CreateJobs();
        Assert.Equal("interrupted", restored.Get(job.Id).Phase);
        Assert.Equal("distribution_job_interrupted", restored.Get(job.Id).ErrorCode);
        Assert.True(File.Exists(Path.Combine(fixture.Root, job.Id, "objects", "00000000.blob")));
        Assert.Single(fixture.Supervisor.Work);
        Assert.True(restored.Remove(job.Id));
    }

    [Fact]
    public async Task ExplicitRemovalOnlyTouchesTheNamedOwnedTerminalDirectory()
    {
        await using var fixture = new Fixture();
        var sentinel = Path.Combine(fixture.Root, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "owner file");
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        Assert.Throws<ArgumentException>(() => fixture.Jobs.Remove("../keep.txt"));
        Assert.True(fixture.Jobs.Remove(job.Id));
        Assert.False(fixture.Jobs.Remove(job.Id));
        Assert.Equal("owner file", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task HistoryPaginationDoesNotLimitTheNumberOfRetainedJobs()
    {
        await using var fixture = new Fixture(pageSize: 1);
        for (var index = 0; index < 3; index++)
        {
            _ = await fixture.Jobs.StartAsync("plan-" + index);
            await fixture.Supervisor.WaitAsync();
        }
        var first = fixture.Jobs.List();
        var second = fixture.Jobs.List(first.NextCursor);
        var third = fixture.Jobs.List(second.NextCursor);
        Assert.Single(first.Entries);
        Assert.Single(second.Entries);
        Assert.Single(third.Entries);
        Assert.NotNull(first.NextCursor);
        Assert.NotNull(second.NextCursor);
        Assert.Null(third.NextCursor);
        Assert.Equal(3, first.Entries.Concat(second.Entries).Concat(third.Entries).Select(job => job.Id).Distinct().Count());
        Assert.Throws<ArgumentException>(() => fixture.Jobs.List("../outside"));
    }

    [Fact]
    public async Task IncorrectOwnershipMarkerPreventsDeletion()
    {
        await using var fixture = new Fixture();
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, job.Id, "owner"), new string('0', 32));
        Assert.Throws<InvalidDataException>(() => fixture.Jobs.Remove(job.Id));
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, job.Id)));
    }

    [Fact]
    public async Task InsufficientActualFreeSpaceRefusesPayloadBeforeTransfer()
    {
        await using var fixture = new Fixture(reserve: long.MaxValue);
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        Assert.Equal("failed", fixture.Jobs.Get(job.Id).Phase);
        Assert.Equal("distribution_storage_failed", fixture.Jobs.Get(job.Id).ErrorCode);
        Assert.Equal(0, fixture.Transport.Copies);
    }

    [Fact]
    public async Task ConcurrentJobsReserveTheVolumeAndCancellationReturnsThatReservation()
    {
        await using var fixture = new Fixture(available: _ => 5);
        fixture.Transport.Block = true;
        var first = await fixture.Jobs.StartAsync("first");
        await fixture.Transport.Started.Task.WaitAsync(Limit);
        var second = await fixture.Jobs.StartAsync("second");
        await fixture.Supervisor.Work[1].Completion.WaitAsync(Limit);
        Assert.Equal("distribution_storage_failed", fixture.Jobs.Get(second.Id).ErrorCode);
        fixture.Jobs.Cancel(first.Id);
        await fixture.Supervisor.WaitAsync();
        fixture.Transport.Block = false;
        var third = await fixture.Jobs.StartAsync("third");
        await fixture.Supervisor.WaitAsync();
        Assert.Equal("staged", fixture.Jobs.Get(third.Id).Phase);
    }

    [Fact]
    public async Task SourceRetargetingDuringPreparationCannotChangeAnAdmittedJob()
    {
        await using var fixture = new Fixture();
        fixture.Packages.ReplaceSource = true;
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        Assert.Equal("failed", fixture.Jobs.Get(job.Id).Phase);
        Assert.Equal(0, fixture.Transport.Copies);
    }

    [Fact]
    public async Task ClientTargetsAreRefusedBeforeAnyBackgroundTaskIsAdmitted()
    {
        await using var fixture = new Fixture();
        fixture.Plans.Client = true;
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Jobs.StartAsync("plan").AsTask());
        Assert.Empty(fixture.Supervisor.Work);
    }

    [Fact]
    public async Task SupervisorCancellationBeforeTheBodyStartsDoesNotLeaveDisposalWaitingForever()
    {
        await using var fixture = new Fixture();
        fixture.Supervisor.SkipBody = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Jobs.StartAsync("plan").AsTask());
        await fixture.Supervisor.WaitAsync();
        await fixture.Jobs.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    [Fact]
    public async Task AdmissionAndDuplicateWaitForTheFirstPersistedReceipt()
    {
        await using var fixture = new Fixture();
        fixture.Transport.Block = true;
        fixture.Supervisor.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Jobs.StartAsync("plan").AsTask();
        await fixture.Supervisor.Scheduled.Task.WaitAsync(Limit);
        var duplicate = fixture.Jobs.StartAsync("plan").AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(duplicate.IsCompleted);
        fixture.Supervisor.StartGate.SetResult();
        var accepted = await first.WaitAsync(Limit);
        Assert.Equal(accepted.Id, (await duplicate.WaitAsync(Limit)).Id);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, accepted.Id, "receipt.json"));
        Assert.Equal(accepted.Id, JsonSerializer.Deserialize<DistributionDownloadJob>(bytes)!.Id);
        fixture.Jobs.Cancel(accepted.Id);
        await fixture.Supervisor.WaitAsync();
    }

    [Fact]
    public async Task CancellingADuplicateWaitDoesNotWithdrawTheOriginalAdmission()
    {
        await using var fixture = new Fixture();
        fixture.Transport.Block = true;
        fixture.Supervisor.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Jobs.StartAsync("plan").AsTask();
        await fixture.Supervisor.Scheduled.Task.WaitAsync(Limit);
        using var cancellation = new CancellationTokenSource();
        var duplicate = fixture.Jobs.StartAsync("plan", cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => duplicate);
        fixture.Supervisor.StartGate.SetResult();
        var accepted = await first.WaitAsync(Limit);
        Assert.True(File.Exists(Path.Combine(fixture.Root, accepted.Id, "receipt.json")));
        Assert.Single(fixture.Supervisor.Work);
        fixture.Jobs.Cancel(accepted.Id);
        await fixture.Supervisor.WaitAsync();
    }

    [Fact]
    public async Task InitialStorageFailureCannotReturnAnAcceptedJobOrDeleteAnUnownedFile()
    {
        await using var fixture = new Fixture();
        fixture.Supervisor.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = fixture.Jobs.StartAsync("plan").AsTask();
        await fixture.Supervisor.Scheduled.Task.WaitAsync(Limit);
        var pending = Assert.Single(fixture.Jobs.List().Entries);
        var blocked = Path.Combine(fixture.Root, pending.Id);
        await File.WriteAllTextAsync(blocked, "unowned");
        fixture.Supervisor.StartGate.SetResult();
        await Assert.ThrowsAsync<IOException>(() => admission);
        await fixture.Supervisor.WaitAsync();
        Assert.Equal("unowned", await File.ReadAllTextAsync(blocked));
        Assert.Empty(fixture.Jobs.List().Entries);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("phase")]
    [InlineData("bytes")]
    [InlineData("files")]
    [InlineData("version")]
    public async Task CorruptReceiptFieldsFailInsteadOfPublishingAFalseStagedResult(string field)
    {
        await using var fixture = new Fixture();
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        var state = fixture.Jobs.Get(job.Id);
        var corrupt = field switch
        {
            "package" => state with { Package = null! },
            "phase" => state with { Phase = "invented" },
            "bytes" => state with { BytesReceived = state.TotalBytes + 1 },
            "files" => state with { CompletedFiles = 0 },
            "version" => state with { Version = "\n" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, job.Id, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(corrupt));
        Assert.Throws<InvalidDataException>(() => fixture.Jobs.Get(job.Id));
    }

    [Theory]
    [InlineData("abd")]
    [InlineData("ab")]
    [InlineData("abcd")]
    public async Task StagedReadbackRefusesChangedDigestOrLengthWithoutDeletingTheEvidence(string content)
    {
        await using var fixture = new Fixture();
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        var path = Path.Combine(fixture.Root, job.Id, "objects", "00000000.blob");
        await File.WriteAllTextAsync(path, content);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Jobs.VerifyAsync(job.Id).AsTask());
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.True(fixture.Jobs.Remove(job.Id));
    }

    [Fact]
    public async Task StagedReadbackRejectsAnInventoryFromADifferentSelection()
    {
        await using var fixture = new Fixture();
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        var path = Path.Combine(fixture.Root, job.Id, "inventory.json");
        var inventory = JsonSerializer.Deserialize<DistributionFilePlan>(await File.ReadAllBytesAsync(path))!;
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(inventory with { Selection = inventory.Selection with { Id = "different-plan" } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Jobs.VerifyAsync(job.Id).AsTask());
    }

    [Fact]
    public async Task StagedReadbackCannotRaceDeletionAndDisposalCancelsAndDrainsItsReader()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(openBlob: _ => new BlockingReadStream(started));
        var job = await fixture.Jobs.StartAsync("plan");
        await fixture.Supervisor.WaitAsync();
        var verification = fixture.Jobs.VerifyAsync(job.Id).AsTask();
        await started.Task.WaitAsync(Limit);
        Assert.Equal("distribution_job_in_use", Assert.Throws<InvalidOperationException>(() => fixture.Jobs.Remove(job.Id)).Message);
        await fixture.Jobs.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
        Assert.True(File.Exists(Path.Combine(fixture.Root, job.Id, "objects", "00000000.blob")));
        await using var restored = fixture.CreateJobs();
        Assert.True(restored.Remove(job.Id));
    }

    private sealed class BlockingReadStream(TaskCompletionSource started) : MemoryStream(Fixture.Bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly byte[] Bytes = Encoding.UTF8.GetBytes("abc");
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "bff-distribution-jobs-" + Guid.NewGuid().ToString("N"));
        internal readonly Plans Plans = new();
        internal readonly Transport Transport = new();
        internal readonly Supervisor Supervisor = new();
        internal readonly Packages Packages;
        internal readonly DistributionJobs Jobs;
        private readonly long _reserve;
        private readonly Func<string, long>? _available;
        private readonly int _pageSize;
        private readonly Func<string, Stream>? _openBlob;
        internal Fixture(long reserve = 0, Func<string, long>? available = null, int pageSize = 20, Func<string, Stream>? openBlob = null)
        {
            _reserve = reserve;
            _available = available;
            _pageSize = pageSize;
            _openBlob = openBlob;
            Packages = new(Plans);
            Jobs = CreateJobs();
        }
        internal DistributionJobs CreateJobs() => new(Plans, Packages, Transport, new() { StagingPath = Root, FreeSpaceReserveBytes = _reserve, HistoryPageSize = _pageSize }, Supervisor.RunAsync, availableSpace: _available, openBlob: _openBlob);
        public async ValueTask DisposeAsync()
        {
            await Jobs.DisposeAsync();
            await Supervisor.WaitAsync();
            var full = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private sealed class Plans : IDistributionPlans
    {
        internal bool Client;
        public ValueTask<DistributionPlan> ValidateAsync(string planId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DistributionPlan(planId, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, 1,
                new("chosen", "Chosen", "http", "https://chosen.example/", "source.toml"),
                new("chosen", "Chosen", new(new(Client ? "client" : "server", "plugin", "bubblefighter", "example"), "1.0.0", "Example", null, "release.toml", new string('a', 64)))
                    { SelectionFingerprint = new string('b', 64) }, null));
        }
        public ValueTask<DistributionPlan> CreateAsync(DistributionPlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool Remove(string planId) => true;
    }

    private sealed class Packages(Plans plans) : IDistributionPackages
    {
        internal bool ReplaceSource;
        public async ValueTask<DistributionFilePlan> PrepareAsync(string planId, CancellationToken cancellationToken = default)
        {
            var selection = await plans.ValidateAsync(planId, cancellationToken);
            if (ReplaceSource) selection = selection with { Source = selection.Source with { BaseUrl = "https://other.example/" } };
            return new(selection, "test-manifest", new string('a', 64), [], [],
                [new("plugin.dll", "objects/plugin", 3, Convert.ToHexStringLower(SHA256.HashData(Fixture.Bytes)), null)], 3);
        }
    }

    private sealed class Transport : IDistributionTransport
    {
        internal bool Block;
        internal bool FailDigest;
        internal int Copies;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask CopyFileAsync(DistributionSource source, string relativePath, long expectedSize, string sha256, Stream staging, CancellationToken cancellationToken = default)
        {
            Copies++;
            await staging.WriteAsync(Fixture.Bytes.AsMemory(0, 1), cancellationToken);
            Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            await staging.WriteAsync(Fixture.Bytes.AsMemory(1), cancellationToken);
            if (FailDigest) throw new InvalidDataException("Payload digest mismatch.");
        }
        public ValueTask<byte[]> ReadDocumentAsync(DistributionSource source, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CopyRepositoryAssetAsync(RepositoryAsset asset, Stream staging, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Supervisor
    {
        internal readonly List<IOwnedTask> Work = [];
        internal bool SkipBody;
        internal TaskCompletionSource? StartGate;
        internal readonly TaskCompletionSource Scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask<IOwnedTask> RunAsync(string name, Func<CancellationToken, ValueTask> work, CancellationToken token)
        {
            var completion = Task.Run(async () =>
            {
                if (StartGate is not null) await StartGate.Task.WaitAsync(token);
                if (!SkipBody) await work(token);
            });
            var task = new Handle(name, completion);
            Work.Add(task);
            Scheduled.TrySetResult();
            return ValueTask.FromResult<IOwnedTask>(task);
        }
        internal async Task WaitAsync() => await Task.WhenAll(Work.Select(task => task.Completion)).WaitAsync(Limit);
        private sealed class Handle(string name, Task completion) : IOwnedTask
        {
            public string Name => name;
            public Task Completion => completion;
            public ValueTask DisposeAsync() => new(completion);
        }
    }
}
