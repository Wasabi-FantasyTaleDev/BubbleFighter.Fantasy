using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies bounded module ledger accounting: one record per key, generation on successful reload, independent lease sources.</summary>
public sealed class PluginModulesTests
{
    private const string ModuleKey = @"native:C:\plugins\demo\Demo.dll";

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Runs one wave of concurrent reload attempts that all start together and reports how many won the reload slot.</summary>
    private static async Task<int> AttemptConcurrently(PluginModules ledger, string key, int attempts)
    {
        var start = Signal();
        var winners = 0;
        var tasks = new Task[attempts];
        for (var i = 0; i < attempts; i++)
            tasks[i] = Task.Run(async () =>
            {
                await start.Task;
                if (ledger.TryBeginReload(key)) Interlocked.Increment(ref winners);
            });
        start.SetResult();
        await Task.WhenAll(tasks);
        return winners;
    }

    /// <summary>A ledger refuses a non-positive capacity instead of silently tracking nothing.</summary>
    [Fact]
    public void NonPositiveCapacityIsRejected()
    {
        Assert.Throws<ConfigException>(() => new PluginModules(0));
        Assert.Throws<ConfigException>(() => new PluginModules(-1));
    }

    /// <summary>A key beyond the declared capacity is rejected while the existing record stays usable.</summary>
    [Fact]
    public void CapacityRejectsANewKeyAndKeepsExistingRecords()
    {
        var ledger = new PluginModules(1);
        ledger.AddLease("native:first", LeaseKind.Activation);

        var failure = Assert.Throws<ConfigException>(() => ledger.AddLease("native:second", LeaseKind.Activation));
        Assert.Contains("capacity", failure.Message);
        Assert.Throws<ConfigException>(() => ledger.TryBeginReload("native:second"));

        Assert.Equal(1, ledger.MaxSnapshotEntries);
        Assert.Equal("native:first", Assert.Single(ledger.Snapshot()).Key);
        ledger.AddLease("native:first", LeaseKind.Activation);
        Assert.Equal(2, Assert.Single(ledger.Snapshot()).Activation);
    }

    /// <summary>A batch that exceeds capacity neither opens prefix records nor increments an existing scheduled lease.</summary>
    [Fact]
    public void ScheduledBatchOverCapacityLeavesExistingCountsAndAvailableSlotsUnchanged()
    {
        var ledger = new PluginModules(2);
        ledger.AddLease("native:a", LeaseKind.Scheduled);
        ledger.AddLease("native:a", LeaseKind.Activation);
        var before = ledger.Snapshot().ToArray();

        var failure = Assert.Throws<ConfigException>(() =>
            ledger.AddScheduledLeases(InOrder("native:a", "native:b", "native:c")));

        Assert.Contains("capacity", failure.Message);
        Assert.Equal(before, ledger.Snapshot().ToArray());
        ledger.AddScheduledLeases(InOrder("native:b"));
        Assert.Equal(["native:a", "native:b"], ledger.Snapshot().Select(entry => entry.Key).ToArray());
        Assert.All(ledger.Snapshot(), entry => Assert.Equal(1, entry.Scheduled));
    }

    /// <summary>A blank coordination key at any batch position rejects the whole acquisition without partial ownership.</summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 1)]
    [InlineData("\t", 2)]
    public void BlankKeyInScheduledBatchLeavesAllRecordsUnchanged(string blank, int position)
    {
        var ledger = new PluginModules(3);
        ledger.AddLease("native:a", LeaseKind.Scheduled);
        var before = ledger.Snapshot().ToArray();
        var keys = new List<string> { "native:a", "native:b" };
        keys.Insert(position, blank);

        Assert.Throws<ConfigException>(() => ledger.AddScheduledLeases(InOrder(keys.ToArray())));

        Assert.Equal(before, ledger.Snapshot().ToArray());
    }

    /// <summary>An unknown or unleased trailing module prevents every decrement in a scheduled release batch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidScheduledBatchReleaseDoesNotDecrementAnyValidKey(bool trackedWithoutScheduledLease)
    {
        var ledger = new PluginModules(3);
        ledger.AddLease("native:a", LeaseKind.Scheduled);
        ledger.AddLease("native:b", LeaseKind.Scheduled);
        ledger.AddLease("native:b", LeaseKind.Scheduled);
        if (trackedWithoutScheduledLease) ledger.AddLease("native:invalid", LeaseKind.DefinitionCache);
        var before = ledger.Snapshot().ToArray();

        Assert.Throws<ConfigException>(() =>
            ledger.RemoveScheduledLeases(InOrder("native:a", "native:b", "native:invalid")));

        Assert.Equal(before, ledger.Snapshot().ToArray());
        ledger.RemoveScheduledLeases(InOrder("native:a", "native:b"));
        Assert.Equal(0, ledger.Snapshot().Single(entry => entry.Key == "native:a").Scheduled);
        Assert.Equal(1, ledger.Snapshot().Single(entry => entry.Key == "native:b").Scheduled);
    }

    /// <summary>Each successful batch owns one scheduled lease per unique key and releases independently to zero.</summary>
    [Fact]
    public void ScheduledBatchesIncrementOncePerKeyAndReleaseToZero()
    {
        var ledger = new PluginModules(3);
        ledger.AddLease("native:a", LeaseKind.Activation);
        ledger.AddLease("native:b", LeaseKind.DefinitionCache);
        var keys = InOrder("native:b", "native:a", "native:c", "native:a");

        ledger.AddScheduledLeases(keys);

        Assert.Equal(3, ledger.Snapshot().Count);
        Assert.All(ledger.Snapshot(), entry => Assert.Equal(1, entry.Scheduled));
        Assert.True(ledger.HasScheduledLeases());
        ledger.AddScheduledLeases(keys);
        Assert.All(ledger.Snapshot(), entry => Assert.Equal(2, entry.Scheduled));
        ledger.RemoveScheduledLeases(keys);
        Assert.All(ledger.Snapshot(), entry => Assert.Equal(1, entry.Scheduled));
        ledger.RemoveScheduledLeases(keys);

        Assert.All(ledger.Snapshot(), entry => Assert.Equal(0, entry.Scheduled));
        Assert.False(ledger.HasScheduledLeases());
        Assert.Equal(1, ledger.Snapshot().Single(entry => entry.Key == "native:a").Activation);
        Assert.Equal(1, ledger.Snapshot().Single(entry => entry.Key == "native:b").DefinitionCache);
        Assert.All(ledger.Snapshot(), entry => Assert.Equal(PluginModules.InitialGeneration, entry.Generation));
    }

    /// <summary>Empty scheduled batches create no records and leave existing ownership unchanged.</summary>
    [Fact]
    public void EmptyScheduledBatchesDoNotCreateRecordsOrChangeExistingCounts()
    {
        var ledger = new PluginModules(1);
        var empty = InOrder();

        ledger.AddScheduledLeases(empty);
        ledger.RemoveScheduledLeases(empty);
        Assert.Empty(ledger.Snapshot());
        ledger.AddLease(ModuleKey, LeaseKind.Scheduled);
        var before = ledger.Snapshot().ToArray();
        ledger.AddScheduledLeases(empty);
        ledger.RemoveScheduledLeases(empty);

        Assert.Equal(before, ledger.Snapshot().ToArray());
    }

    // Explicit ordering ensures invalid trailing entries exercise atomicity after valid prefixes.
    private static IReadOnlySet<string> InOrder(params string[] keys) => new SortedSet<string>(keys,
        Comparer<string>.Create((left, right) => Array.IndexOf(keys, left).CompareTo(Array.IndexOf(keys, right))));

    /// <summary>Repeated leases and reloads of one key keep a single record inside a one-record ledger.</summary>
    [Fact]
    public void RepeatedUseOfOneKeyKeepsOneRecord()
    {
        var ledger = new PluginModules(1);
        ledger.AddLease(ModuleKey, LeaseKind.Scheduled);
        ledger.AddLease(ModuleKey, LeaseKind.Scheduled);
        Assert.True(ledger.TryBeginReload(ModuleKey));
        Assert.True(ledger.CompleteReload(ModuleKey, success: true));
        ledger.MarkRetainedFailure(ModuleKey, retained: true);

        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModuleKey, entry.Key);
        Assert.Equal(PluginModules.InitialGeneration + 1, entry.Generation);
        Assert.Equal(2, entry.Scheduled);
        Assert.True(entry.RetainedFailureMarked);
    }

    /// <summary>Completing a reload that was never begun is rejected and leaves the reload slot free.</summary>
    [Fact]
    public void CompleteWithoutBeginIsRejected()
    {
        var ledger = new PluginModules(1);
        ledger.AddLease(ModuleKey, LeaseKind.DefinitionCache);

        Assert.Throws<ConfigException>(() => ledger.CompleteReload(ModuleKey, success: true));
        Assert.Equal(PluginModules.InitialGeneration, Assert.Single(ledger.Snapshot()).Generation);
        Assert.True(ledger.TryBeginReload(ModuleKey));
        Assert.True(ledger.CompleteReload(ModuleKey, success: true));
        Assert.Equal(PluginModules.InitialGeneration + 1, Assert.Single(ledger.Snapshot()).Generation);
    }

    /// <summary>One module admits one reload owner at a time and admits a new owner only after completion.</summary>
    [Fact]
    public void SecondBeginIsRejectedUntilTheReloadCompletes()
    {
        var ledger = new PluginModules(1);

        Assert.True(ledger.TryBeginReload(ModuleKey));
        Assert.False(ledger.TryBeginReload(ModuleKey));
        Assert.Equal(PluginModules.InitialGeneration, Assert.Single(ledger.Snapshot()).Generation);
        Assert.True(ledger.CompleteReload(ModuleKey, success: true));
        Assert.True(ledger.TryBeginReload(ModuleKey));
    }

    /// <summary>A failed reload reports no advance and releases its slot without changing the generation.</summary>
    [Fact]
    public void FailedReloadDoesNotAdvanceTheGeneration()
    {
        var ledger = new PluginModules(1);

        Assert.True(ledger.TryBeginReload(ModuleKey));
        Assert.False(ledger.CompleteReload(ModuleKey, success: false));
        Assert.Equal(PluginModules.InitialGeneration, Assert.Single(ledger.Snapshot()).Generation);
        Assert.Equal(ModuleKey, Assert.Single(ledger.Snapshot()).Key);
        Assert.True(ledger.TryBeginReload(ModuleKey));
        Assert.True(ledger.CompleteReload(ModuleKey, success: true));
        Assert.Equal(PluginModules.InitialGeneration + 1, Assert.Single(ledger.Snapshot()).Generation);
    }

    /// <summary>A hundred bounded reload rounds advance the generation by exactly one each and add no record.</summary>
    [Fact]
    public void BoundedReloadRoundsAdvanceTheGenerationMonotonically()
    {
        const int rounds = 100;
        var ledger = new PluginModules(1);

        for (var round = 0; round < rounds; round++)
        {
            Assert.True(ledger.TryBeginReload(ModuleKey));
            Assert.True(ledger.CompleteReload(ModuleKey, success: true));
            Assert.Equal(PluginModules.InitialGeneration + round + 1, Assert.Single(ledger.Snapshot()).Generation);
        }

        Assert.Equal(PluginModules.InitialGeneration + rounds, Assert.Single(ledger.Snapshot()).Generation);
    }

    /// <summary>The four lease sources count separately, and removing a reference that was never taken fails loud without mutating state.</summary>
    [Fact]
    public void LeaseSourcesCountIndependentlyAndRejectRemovalBelowZero()
    {
        var ledger = new PluginModules(1);
        ledger.AddLease(ModuleKey, LeaseKind.Scheduled);
        ledger.AddLease(ModuleKey, LeaseKind.Activation);
        ledger.AddLease(ModuleKey, LeaseKind.Activation);
        ledger.AddLease(ModuleKey, LeaseKind.DefinitionCache);
        ledger.AddLease(ModuleKey, LeaseKind.RetainedFailure);

        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(1, entry.Scheduled);
        Assert.Equal(2, entry.Activation);
        Assert.Equal(1, entry.DefinitionCache);
        Assert.Equal(1, entry.RetainedFailure);

        ledger.RemoveLease(ModuleKey, LeaseKind.Activation);
        ledger.RemoveLease(ModuleKey, LeaseKind.Activation);
        Assert.Throws<ConfigException>(() => ledger.RemoveLease(ModuleKey, LeaseKind.Activation));
        ledger.RemoveLease(ModuleKey, LeaseKind.Scheduled);
        Assert.Throws<ConfigException>(() => ledger.RemoveLease(ModuleKey, LeaseKind.Scheduled));
        ledger.RemoveLease(ModuleKey, LeaseKind.RetainedFailure);

        entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(0, entry.Scheduled);
        Assert.Equal(0, entry.Activation);
        Assert.Equal(1, entry.DefinitionCache);
        Assert.Equal(0, entry.RetainedFailure);
    }

    /// <summary>A lease source outside the four supported kinds is rejected instead of being counted elsewhere.</summary>
    [Fact]
    public void UnsupportedLeaseSourceIsRejected()
    {
        var ledger = new PluginModules(1);

        Assert.Throws<ConfigException>(() => ledger.AddLease(ModuleKey, (LeaseKind)99));
        Assert.Throws<ConfigException>(() => ledger.RemoveLease(ModuleKey, (LeaseKind)99));
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>Every member that needs an existing record rejects a key the ledger never opened.</summary>
    [Fact]
    public void UnknownKeysAreRejected()
    {
        var ledger = new PluginModules(2);

        Assert.Throws<ConfigException>(() => ledger.CompleteReload(ModuleKey, success: true));
        Assert.Throws<ConfigException>(() => ledger.RemoveLease(ModuleKey, LeaseKind.Scheduled));
        Assert.Throws<ConfigException>(() => ledger.MarkRetainedFailure(ModuleKey, retained: true));
        Assert.Throws<ConfigException>(() => ledger.ShouldRefuseAcquisition(ModuleKey));
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>Keys preserve the definition's nonempty identity without introducing a ledger-only length restriction.</summary>
    [Fact]
    public void BlankKeysAreRejectedAndLongCapturedKeysArePreserved()
    {
        var ledger = new PluginModules(2);
        Assert.Throws<ConfigException>(() => ledger.AddLease(null!, LeaseKind.Scheduled));
        Assert.Throws<ConfigException>(() => ledger.AddLease("   ", LeaseKind.Scheduled));

        var longest = new string('k', 1024);
        ledger.AddLease(longest, LeaseKind.Scheduled);
        Assert.Equal(longest, Assert.Single(ledger.Snapshot()).Key);
        Assert.True(ledger.TryBeginReload(longest));
        Assert.True(ledger.CompleteReload(longest, success: true));
        Assert.Single(ledger.Snapshot());
    }

    /// <summary>Acquisition is refused only while the module carries a retained failure, and the mark can be cleared.</summary>
    [Fact]
    public void RetainedFailureMarkDecidesAcquisitionRefusal()
    {
        var ledger = new PluginModules(1);
        ledger.AddLease(ModuleKey, LeaseKind.RetainedFailure);

        Assert.False(ledger.ShouldRefuseAcquisition(ModuleKey));
        Assert.False(Assert.Single(ledger.Snapshot()).RetainedFailureMarked);

        ledger.MarkRetainedFailure(ModuleKey, retained: true);
        Assert.True(ledger.ShouldRefuseAcquisition(ModuleKey));
        Assert.True(Assert.Single(ledger.Snapshot()).RetainedFailureMarked);

        ledger.MarkRetainedFailure(ModuleKey, retained: false);
        Assert.False(ledger.ShouldRefuseAcquisition(ModuleKey));
        Assert.False(Assert.Single(ledger.Snapshot()).RetainedFailureMarked);
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).RetainedFailure);
    }

    /// <summary>The snapshot reports every counter ordered by key and is a copy rather than a live view.</summary>
    [Fact]
    public void SnapshotReportsEveryCounterOrderedByKey()
    {
        var ledger = new PluginModules(2);
        ledger.AddLease("native:b", LeaseKind.DefinitionCache);
        ledger.AddLease("native:b", LeaseKind.DefinitionCache);
        ledger.AddLease("native:b", LeaseKind.RetainedFailure);
        ledger.AddLease("native:a", LeaseKind.Activation);
        ledger.AddLease("native:a", LeaseKind.Scheduled);
        ledger.RemoveLease("native:a", LeaseKind.Scheduled);
        Assert.True(ledger.TryBeginReload("native:a"));
        Assert.True(ledger.CompleteReload("native:a", success: true));
        ledger.MarkRetainedFailure("native:b", retained: true);

        var entries = ledger.Snapshot();
        Assert.Equal(["native:a", "native:b"], entries.Select(entry => entry.Key).ToArray());
        Assert.Equal(2, entries[0].Generation);
        Assert.Equal(0, entries[0].Scheduled);
        Assert.Equal(1, entries[0].Activation);
        Assert.Equal(0, entries[0].DefinitionCache);
        Assert.Equal(0, entries[0].RetainedFailure);
        Assert.False(entries[0].RetainedFailureMarked);
        Assert.Equal(PluginModules.InitialGeneration, entries[1].Generation);
        Assert.Equal(2, entries[1].DefinitionCache);
        Assert.Equal(1, entries[1].RetainedFailure);
        Assert.True(entries[1].RetainedFailureMarked);

        ledger.AddLease("native:a", LeaseKind.Activation);
        Assert.Equal(1, entries[0].Activation);
        var refreshed = ledger.Snapshot();
        Assert.Equal(2, refreshed.Count);
        Assert.True(refreshed.Count <= ledger.MaxSnapshotEntries);
        Assert.Equal(2, refreshed[0].Activation);
        Assert.Equal("native:b", refreshed[1].Key);
        Assert.Equal(refreshed[1], ledger.Snapshot()[1]);
    }

    /// <summary>A hundred concurrent reload attempts admit exactly one owner, and none while a reload is in progress.</summary>
    [Fact]
    public async Task ConcurrentBeginReloadAttemptsAdmitExactlyOneOwner()
    {
        const int attempts = 100;
        var ledger = new PluginModules(2);

        Assert.Equal(1, await AttemptConcurrently(ledger, ModuleKey, attempts));
        Assert.Equal(0, await AttemptConcurrently(ledger, ModuleKey, attempts));
        Assert.True(ledger.CompleteReload(ModuleKey, success: true));
        Assert.Equal(1, await AttemptConcurrently(ledger, ModuleKey, attempts));
        Assert.True(ledger.CompleteReload(ModuleKey, success: true));
        Assert.Equal(PluginModules.InitialGeneration + 2, Assert.Single(ledger.Snapshot()).Generation);
    }
}
