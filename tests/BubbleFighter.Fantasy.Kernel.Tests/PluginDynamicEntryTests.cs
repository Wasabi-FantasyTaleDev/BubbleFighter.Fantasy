using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises bounded entry admission, retirement, and reincarnation through the real plugin host.</summary>
public sealed class PluginDynamicEntryTests
{
    private const int Effects = 16;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Exposes a value whose owner must remain alive while a dependent activation drains.</summary>
    public interface IValue
    {
        /// <summary>Gets the provider's value while its owned resource is alive.</summary>
        int Number { get; }
    }

    /// <summary>Adds an enabled parent/provider/consumer subtree while preserving disabled and manually stopped entries.</summary>
    [Fact]
    public async Task AddedKnownSubtreeActivatesAndRemovesWithoutRestartingUnchangedEntries()
    {
        var control = Control();
        var manual = Row("manual", "idle");
        await using var harness = new Harness([control, manual]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        await harness.Host.UnloadAsync("manual", default).AsTask().WaitAsync(Limit);
        var parent = Row("parent", "group", parent: "control");
        var provider = Row("provider-entry", "provider", 31, "parent");
        var consumer = Row("consumer-entry", "consumer", parent: "parent");
        var inactive = Row("inactive", "idle", parent: "parent", disabled: true);

        var added = await harness.Host.ReconcileConfigurationAsync([control, manual, consumer, inactive, provider, parent]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, added.Outcome);
        Assert.Equal(31, harness.Entry("consumer-entry").Last.Seen!.Number);
        Assert.Equal(PluginState.Active, harness.Info("parent").State);
        Assert.Equal(PluginState.Unloaded, harness.Info("inactive").State);
        Assert.Equal(PluginState.Unloaded, harness.Info("manual").State);
        Assert.Equal(0, harness.Entry("inactive").Starts);
        Assert.Equal(1, harness.Entry("control").Starts);
        Assert.Equal(1, harness.Entry("manual").Starts);
        AssertBefore(harness.ActivationOrder.ToArray(), "parent", "provider-entry");
        AssertBefore(harness.ActivationOrder.ToArray(), "provider-entry", "consumer-entry");
        Assert.All(harness.Host.Instances, instance => Assert.False(instance.IsRetiring));
        var removed = await harness.Host.ReconcileConfigurationAsync([control, manual]).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, removed.Outcome);
        Assert.Equal(["control", "manual"], harness.Host.Instances.Select(instance => instance.RowId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(1, harness.Entry("control").Starts);
        Assert.Equal(0, harness.Entry("control").Stops);
        Assert.Equal(1, harness.Entry("manual").Starts);
        Assert.Equal(PluginState.Unloaded, harness.Info("manual").State);
        AssertBefore(harness.DisposalOrder.ToArray(), "consumer-entry:execution", "provider-entry:execution");
        AssertBefore(harness.DisposalOrder.ToArray(), "provider-entry:execution", "parent:execution");
        Assert.All(harness.Factory("provider").ReloadFlags, flag => Assert.False(flag));
        Assert.All(harness.Factory("consumer").ReloadFlags, flag => Assert.False(flag));
    }

    /// <summary>Drains a removed parent hierarchy in child-first order without relying on service dependency edges.</summary>
    [Fact]
    public async Task RemovingAParentAndItsChildrenDrainsTheWholeSubtreeInOrder()
    {
        var control = Control();
        await using var harness = new Harness([control]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        await harness.Host.ReconcileConfigurationAsync(
        [
            control, Row("leaf", "idle", parent: "child"),
            Row("child", "idle", parent: "parent"), Row("parent", "group")
        ]).AsTask().WaitAsync(Limit);

        var result = await harness.Host.ReconcileConfigurationAsync([control]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal("control", Assert.Single(harness.Host.Instances).RowId);
        AssertBefore(harness.DisposalOrder.ToArray(), "leaf:execution", "child:execution");
        AssertBefore(harness.DisposalOrder.ToArray(), "child:execution", "parent:execution");
        Assert.Equal(1, harness.Entry("control").Starts);
        Assert.Equal(0, harness.Entry("control").Stops);
    }

    /// <summary>Removes an inactive entry without acquiring an execution merely to retire it.</summary>
    [Fact]
    public async Task RemovingAnInactiveEntryDoesNotAcquireItsFactory()
    {
        var control = Control();
        await using var harness = new Harness([control, Row("inactive", "idle", disabled: true)]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var result = await harness.Host.ReconcileConfigurationAsync([control]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Empty(harness.Factory("idle").ReloadFlags);
        Assert.Equal(0, harness.Entry("inactive").Starts);
        Assert.Equal("control", Assert.Single(harness.Host.Instances).RowId);
    }

    /// <summary>Rejects a candidate exceeding either fixed bootstrap pool before tearing down or acquiring executions.</summary>
    [Theory]
    [InlineData("entry")]
    [InlineData("metadata")]
    public async Task CapacityAdmissionFailsBeforeTeardownOrFactoryAcquisition(string pool)
    {
        var control = pool == "entry" ? Control(entries: 0) : Control(metadata: 0);
        await using var harness = new Harness([control]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var acquired = harness.Acquisitions;

        var error = await Assert.ThrowsAsync<ConfigException>(() => harness.Host.ReconcileConfigurationAsync(
            [WithValue(control, 99), Row("new-entry", "idle")]).AsTask().WaitAsync(Limit));

        Assert.Contains(pool == "entry" ? "entry capacity" : "service metadata capacity", error.Message);
        Assert.Equal(acquired, harness.Acquisitions);
        Assert.Equal(0, harness.Entry("control").Stops);
        Assert.Empty(harness.Factory("idle").ReloadFlags);
        Assert.Equal(0, harness.Entry("control").Last.Context.Config.Bind<Options>().Value);
        var unchanged = await harness.Host.ReconcileConfigurationAsync([control]).AsTask().WaitAsync(Limit);
        Assert.Equal(0, unchanged.Revision);
    }

    /// <summary>Rejects unknown definitions, invalid parents, and forged startup grants atomically across the candidate.</summary>
    [Theory]
    [InlineData("unknown-plugin")]
    [InlineData("unknown-parent")]
    [InlineData("orphan")]
    [InlineData("entry-grant")]
    [InlineData("metadata-grant")]
    public async Task InvalidMembershipCandidatesDoNotChangeExistingEntries(string kind)
    {
        var initial = new[] { Control(), Row("parent", "group"), Row("child", "idle", parent: "parent") };
        await using var harness = new Harness(initial);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var acquired = harness.Acquisitions;
        var added = Row("invalid", "idle");
        added = kind switch
        {
            "unknown-plugin" => added with { Plugin = "not-in-the-catalog" },
            "unknown-parent" => added with { ParentId = "not-an-entry" },
            "entry-grant" => added with { Lifecycle = added.Lifecycle with { EntryReserve = 1 } },
            "metadata-grant" => added with { Lifecycle = added.Lifecycle with { ServiceMetadataReserve = 1 } },
            _ => added
        };
        PluginRow[] candidate = [WithValue(initial[0], 99), initial[1], initial[2], Row("valid-new", "idle"), added];
        if (kind == "orphan") candidate = candidate.Where(row => row.Id != "parent").ToArray();

        await Assert.ThrowsAsync<ConfigException>(() => harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));

        Assert.Equal(acquired, harness.Acquisitions);
        Assert.All(initial, row => { Assert.Equal(1, harness.Entry(row.Id).Starts); Assert.Equal(0, harness.Entry(row.Id).Stops); });
        Assert.All(harness.Host.Instances, instance => { Assert.Equal(PluginState.Active, instance.State); Assert.False(instance.IsRetiring); });
        Assert.Equal(0, harness.Entry("control").Last.Context.Config.Bind<Options>().Value);
        var unchanged = await harness.Host.ReconcileConfigurationAsync(initial).AsTask().WaitAsync(Limit);
        Assert.Equal(0, unchanged.Revision);
    }

    /// <summary>Charges incomplete retirement to fixed capacity and prevents identity reuse until physical finalization.</summary>
    [Theory]
    [InlineData("entry")]
    [InlineData("metadata")]
    public async Task RetiringEntriesRemainVisibleAndChargedUntilFreshIdentityReuseIsSafe(string pool)
    {
        var control = pool == "entry" ? Control(entries: 1) : Control(metadata: 1 + Effects);
        var provider = Row("provider-entry", "provider", 11, timeout: 100);
        await using var harness = new Harness([control]);
        var gate = harness.BlockCleanup("provider-entry");
        try
        {
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            await harness.Host.ReconcileConfigurationAsync([control, provider]).AsTask().WaitAsync(Limit);
            var previous = harness.Entry("provider-entry").Last;
            harness.Entry("control").Last.Context.Events.Emit(new Pulse());
            var previousPulses = Volatile.Read(ref previous.Pulses);
            var removing = harness.Host.ReconcileConfigurationAsync([control]).AsTask();
            await gate.Entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => removing.WaitAsync(Limit));
            Assert.True(harness.Info("provider-entry").IsRetiring);
            Assert.Equal(PluginState.CleanupIncomplete, harness.Info("provider-entry").State);
            Assert.Equal(0, harness.Entry("provider-entry").Closes);
            var capacity = await Assert.ThrowsAsync<ConfigException>(() => harness.Host.ReconcileConfigurationAsync(
                [control, Row("another-entry", "idle")]).AsTask().WaitAsync(Limit));
            Assert.Contains(pool == "entry" ? "entry capacity" : "service metadata capacity", capacity.Message);
            Assert.Empty(harness.Factory("idle").ReloadFlags);
            await Assert.ThrowsAsync<ConfigException>(() => harness.Host.ReconcileConfigurationAsync(
                [control, WithValue(provider, 22)]).AsTask().WaitAsync(Limit));
            await AssertLifecycleRejectedAsync(() => harness.Host.LoadAsync("provider-entry", default).AsTask().WaitAsync(Limit));
            await AssertLifecycleRejectedAsync(() => harness.Host.ReloadAsync("provider-entry", default).AsTask().WaitAsync(Limit));
            Assert.Single(harness.Factory("provider").ReloadFlags);
            await harness.Host.UnloadAsync("control", default).AsTask().WaitAsync(Limit);
            await harness.Host.LoadAsync("control", default).AsTask().WaitAsync(Limit);
            Assert.Equal(2, harness.Entry("control").Starts);
            Assert.Empty(harness.Entry("control").Last.Notifications);
            Assert.True(harness.Info("provider-entry").IsRetiring);
            gate.Release.TrySetResult();
            await harness.Host.UnloadAsync("provider-entry", default).AsTask().WaitAsync(Limit);
            Assert.DoesNotContain(harness.Host.Instances, instance => instance.RowId == "provider-entry");
            Assert.Equal(1, harness.Entry("provider-entry").Closes);

            var readmitted = await harness.Host.ReconcileConfigurationAsync([control, WithValue(provider, 22)]).AsTask().WaitAsync(Limit);

            Assert.Equal(PluginTransitionOutcome.Converged, readmitted.Outcome);
            Assert.False(harness.Info("provider-entry").IsRetiring);
            Assert.Equal(PluginState.Active, harness.Info("provider-entry").State);
            var current = harness.Entry("provider-entry").Last;
            Assert.NotSame(previous.Context, current.Context);
            Assert.True(previous.Context.Lifetime.IsCancellationRequested);
            Assert.False(current.Context.Lifetime.IsCancellationRequested);
            Assert.Equal(22, current.Produced!.Number);
            var currentPulses = Volatile.Read(ref current.Pulses);
            harness.Entry("control").Last.Context.Events.Emit(new Pulse());
            Assert.Equal(previousPulses, Volatile.Read(ref previous.Pulses));
            Assert.Equal(currentPulses + 1, Volatile.Read(ref current.Pulses));
            Assert.Equal(2, harness.Entry("provider-entry").Starts);
        }
        finally { gate.Release.TrySetResult(); }
    }

    /// <summary>Withdraws a removed provider from new observations while dependent cleanup retains its frozen reference.</summary>
    [Fact]
    public async Task RetiringPublicationIsHiddenWhileDependentFrozenReferencesStillDrain()
    {
        var control = Control();
        var consumer = Row("consumer-entry", "consumer", timeout: 100);
        var independent = Row("independent", "idle");
        await using var harness = new Harness([control, Row("provider-entry", "provider", 41), consumer, independent]);
        var gate = harness.BlockCleanup("consumer-entry", activation => Assert.Equal(41, activation.Seen!.Number));
        try
        {
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            var frozen = harness.Entry("consumer-entry").Last.Seen!;
            var candidate = new[] { control, consumer, independent };
            var removing = harness.Host.ReconcileConfigurationAsync(candidate).AsTask();
            await gate.Entered.Task.WaitAsync(Limit);
            Assert.True(harness.Info("provider-entry").IsRetiring);
            Assert.Equal(41, frozen.Number);
            await Assert.ThrowsAsync<InvalidOperationException>(() => removing.WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, harness.Info("consumer-entry").State);
            Assert.False(harness.Info("consumer-entry").IsRetiring);
            // Withdrawing the publication schedules dependency reactions independently of the failed revision.
            // Settle those reactions before issuing new lifecycle operations against their reserved rows.
            await harness.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
            await harness.Host.UnloadAsync("independent", default).AsTask().WaitAsync(Limit);
            await harness.Host.LoadAsync("independent", default).AsTask().WaitAsync(Limit);
            Assert.Equal(2, harness.Entry("independent").Starts);
            await harness.Host.UnloadAsync("control", default).AsTask().WaitAsync(Limit);
            await harness.Host.LoadAsync("control", default).AsTask().WaitAsync(Limit);
            Assert.Empty(harness.Entry("control").Last.Notifications);
            Assert.Equal(41, frozen.Number);
            gate.Release.TrySetResult();

            await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);

            Assert.DoesNotContain(harness.Host.Instances, instance => instance.RowId == "provider-entry");
            Assert.Equal(PluginState.Pending, harness.Info("consumer-entry").State);
            Assert.Equal(1, harness.Entry("consumer-entry").Starts);
            Assert.Equal(1, harness.Entry("provider-entry").Closes);
            AssertBefore(harness.DisposalOrder.ToArray(), "consumer-entry:resource", "provider-entry:execution");
        }
        finally { gate.Release.TrySetResult(); }
    }

    /// <summary>A configuration retry must wait for a conflicting automatic dependency activation to settle.</summary>
    [Fact]
    public async Task ConfigurationRetryWaitsForAutomaticDependencyActivation()
    {
        var control = Control();
        var provider = Row("conditional-entry", "conditional");
        var consumer = Row("consumer-entry", "consumer", disabled: true);
        await using var harness = new Harness([control, provider, consumer]);
        var gate = harness.BlockActivation("consumer-entry");
        try
        {
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            var enabled = harness.Host.RequestState("consumer-entry", true);
            Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, (await enabled.WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
            var owner = harness.Entry("conditional-entry").Last.Context;
            owner.Effect(owner.Services.Register<IValue>(new Value(53)));
            await gate.Entered.Task.WaitAsync(Limit);
            var settlement = harness.Host.WaitForBindingChangesAsync();
            Assert.False(settlement.IsCompleted);
            var candidate = new[] { control, provider, consumer with { Disabled = false } };
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));
            Assert.Contains("queued or running lifecycle transition", failure.Message);

            gate.Release.TrySetResult();
            await settlement.WaitAsync(Limit);
            var result = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
            Assert.Equal(PluginState.Active, harness.Info("consumer-entry").State);
            Assert.Equal(53, harness.Entry("consumer-entry").Last.Seen!.Number);
        }
        finally { gate.Release.TrySetResult(); }
    }

    /// <summary>Keeps a failed added provider's staged registration unpublished and recovers it with a corrected candidate.</summary>
    [Fact]
    public async Task FailedNewEntryPublishesNothingAndCanRecoverWithoutReaddingItsIdentity()
    {
        var control = Control();
        await using var harness = new Harness([control]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var provider = WithValue(Row("provider-entry", "provider"), 51, fail: true);
        var consumer = Row("consumer-entry", "consumer");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReconcileConfigurationAsync([control, provider, consumer]).AsTask().WaitAsync(Limit));

        Assert.Contains("configured dynamic activation failure", failed.ToString());
        Assert.Equal(PluginState.Failed, harness.Info("provider-entry").State);
        Assert.False(harness.Info("provider-entry").IsRetiring);
        Assert.Empty(harness.Entry("control").Last.Notifications);
        Assert.Equal(0, harness.Entry("consumer-entry").Starts);
        Assert.Equal(1, harness.Entry("provider-entry").Closes);
        var recovered = await harness.Host.ReconcileConfigurationAsync([control, WithValue(provider, 52), consumer]).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, recovered.Outcome);
        Assert.Equal(52, harness.Entry("consumer-entry").Last.Seen!.Number);
        Assert.Equal(52, Assert.Single(harness.Entry("control").Last.Notifications).Number);
        Assert.Equal(2, harness.Entry("provider-entry").Starts);
        Assert.Equal(1, harness.Entry("control").Starts);
        Assert.All(harness.Factory("provider").ReloadFlags, flag => Assert.False(flag));
    }

    /// <summary>Admits an enabled entry with missing dependencies as desired and resumes it when a provider is added.</summary>
    [Fact]
    public async Task NewlyDesiredConsumerWaitsUntilALaterCandidateAddsItsProvider()
    {
        var control = Control();
        var consumer = Row("consumer-entry", "consumer");
        await using var harness = new Harness([control]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var waiting = await harness.Host.ReconcileConfigurationAsync([control, consumer]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, waiting.Outcome);
        Assert.Equal(PluginState.Pending, harness.Info("consumer-entry").State);
        Assert.Equal(0, harness.Entry("consumer-entry").Starts);
        Assert.Empty(harness.Factory("consumer").ReloadFlags);
        var ready = await harness.Host.ReconcileConfigurationAsync([control, consumer, Row("provider-entry", "provider", 61)]).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, ready.Outcome);
        Assert.Equal(61, harness.Entry("consumer-entry").Last.Seen!.Number);
        Assert.Equal(1, harness.Entry("consumer-entry").Starts);
    }

    /// <summary>Retains bootstrap headroom after removing every entry and permits the original grant-bearing identity to return.</summary>
    [Fact]
    public async Task EmptyCandidateRetainsBootstrapCapacityForLaterReadmission()
    {
        var control = Control();
        await using var harness = new Harness([control]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var originalContext = harness.Entry("control").Last.Context;
        await harness.Host.ReconcileConfigurationAsync([control, Row("temporary", "idle")]).AsTask().WaitAsync(Limit);

        var emptied = await harness.Host.ReconcileConfigurationAsync([]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, emptied.Outcome);
        Assert.Empty(harness.Host.Instances);
        Assert.True(originalContext.Lifetime.IsCancellationRequested);
        var restored = await harness.Host.ReconcileConfigurationAsync(
            [control, Row("provider-entry", "provider", 71), Row("consumer-entry", "consumer"), Row("parent", "group")]).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, restored.Outcome);
        Assert.Equal(4, harness.Host.Instances.Count);
        Assert.NotSame(originalContext, harness.Entry("control").Last.Context);
        Assert.Equal(2, harness.Entry("control").Starts);
        Assert.Equal(71, harness.Entry("consumer-entry").Last.Seen!.Number);
        Assert.All(harness.Host.Instances, instance => { Assert.Equal(PluginState.Active, instance.State); Assert.False(instance.IsRetiring); });
    }

    /// <summary>Joins physical execution ownership when shutdown supersedes a pending addition or removal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownJoinsAcceptedMembershipWork(bool adding)
    {
        var control = Control();
        var entry = Row("entry", adding ? "provider" : "idle", 81);
        await using var harness = new Harness(adding ? [control] : [control, entry]);
        var gate = adding ? harness.BlockActivation("entry") : harness.BlockCleanup("entry");
        try
        {
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            var applying = harness.Host.ReconcileConfigurationAsync(adding ? [control, entry] : [control]).AsTask();
            await gate.Entered.Task.WaitAsync(Limit);
            var shutdown = harness.Host.ShutdownAsync(default).AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, harness.Entry("entry").Closes);
            gate.Release.TrySetResult();

            var result = await applying.WaitAsync(Limit);
            await shutdown.WaitAsync(Limit);

            Assert.Equal(PluginTransitionOutcome.Superseded, result.Outcome);
            Assert.Equal(1, harness.Entry("entry").Starts);
            Assert.Equal(1, harness.Entry("entry").Stops);
            Assert.Equal(1, harness.Entry("entry").Closes);
            Assert.True(harness.Entry("entry").Last.Context.Lifetime.IsCancellationRequested);
        }
        finally { gate.Release.TrySetResult(); }
    }

    /// <summary>Rejects shared-backend activation while a removed entry retains an execution after unknown cleanup failure.</summary>
    [Fact]
    public async Task FailedRetirementPreventsSharedBackendAcquisitionAfterItsLifecycleScopeSettles()
    {
        const string cleanupFailure = "opaque backend cleanup failure";
        var control = Control();
        var retiring = Row("retiring", "idle");
        var shared = Row("shared-idle", "idle", disabled: true);
        var independent = Row("independent", "group", disabled: true);
        var harness = new Harness([control, retiring, shared, independent],
            new Dictionary<string, string?> { ["idle"] = "shared" });
        harness.Entry("retiring").DisposeExecution = () => throw new InvalidOperationException(cleanupFailure);
        PluginContext? retainedContext = null;
        Exception? primaryFailure = null;
        try
        {
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            retainedContext = Assert.IsType<PluginContext>(harness.Entry("retiring").Last.Context);
            Assert.Single(harness.Factory("idle").ReloadFlags);
            var removal = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Host.ReconcileConfigurationAsync(
                [control, shared, independent]).AsTask().WaitAsync(Limit));
            Assert.Contains(cleanupFailure, removal.ToString());
            Assert.True(harness.Info("retiring").IsRetiring);
            Assert.Equal(PluginState.CleanupFailed, harness.Info("retiring").State);
            Assert.Equal(1, retainedContext.RetainedExecutionCount);
            Assert.Equal(1, harness.Entry("retiring").Closes);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Host.LoadAsync("shared-idle", default).AsTask().WaitAsync(Limit));

            Assert.Single(harness.Factory("idle").ReloadFlags);
            Assert.Equal(0, harness.Entry("shared-idle").Starts);
            await harness.Host.LoadAsync("independent", default).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, harness.Info("independent").State);
            Assert.Equal(1, harness.Entry("independent").Starts);
            Assert.Single(harness.Factory("group").ReloadFlags);
            Assert.True(harness.Info("retiring").IsRetiring);
            Assert.Equal(1, retainedContext.RetainedExecutionCount);
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            var shutdownFailure = await Record.ExceptionAsync(() => harness.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            if (primaryFailure is null)
            {
                var retainedFailure = Assert.IsType<AggregateException>(shutdownFailure);
                Assert.Contains(cleanupFailure, retainedFailure.ToString());
                Assert.Equal(1, Assert.IsType<PluginContext>(retainedContext).RetainedExecutionCount);
            }
            else if (shutdownFailure is not null)
                primaryFailure.Data["RetainedBackendShutdownFailure"] = shutdownFailure.ToString();
        }
    }

    private static async Task AssertLifecycleRejectedAsync(Func<Task> operation)
    {
        var error = await Record.ExceptionAsync(operation);
        Assert.True(error is ConfigException or InvalidOperationException, error?.ToString() ?? "The retiring lifecycle operation unexpectedly succeeded.");
    }

    private static PluginRow Control(int entries = 8, int metadata = 65536) => Row("control", "control") with
    { Lifecycle = new() { EffectCapacity = Effects, EntryReserve = entries, ServiceMetadataReserve = metadata } };

    private static PluginRow Row(string id, string plugin, int value = 0, string? parent = null, bool disabled = false, int timeout = 10000) => new()
    {
        Id = id, Plugin = plugin, Source = "dynamic-entry-test", ParentId = parent, Disabled = disabled,
        Config = new Dictionary<string, object?> { ["value"] = value },
        Lifecycle = new() { EffectCapacity = Effects, TeardownTimeoutMs = timeout }
    };

    private static PluginRow WithValue(PluginRow row, int value, bool fail = false) => row with
    { Config = new Dictionary<string, object?> { ["value"] = value, ["fail"] = fail } };

    private static void AssertBefore(string[] order, string first, string second)
    {
        var firstIndex = Array.IndexOf(order, first);
        var secondIndex = Array.IndexOf(order, second);
        Assert.True(firstIndex >= 0, $"Missing event '{first}'.");
        Assert.True(secondIndex >= 0, $"Missing event '{second}'.");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private sealed class Pulse : IEvent { }

    private sealed class Value(int number) : IValue
    {
        private int _closed;
        /// <summary>Reads the value only while the owning provider's physical lifetime remains open.</summary>
        public int Number
        {
            get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this); return number; }
        }
        internal void Close() => Volatile.Write(ref _closed, 1);
    }

    private sealed class Activation(IPluginContext context)
    {
        internal IPluginContext Context { get; } = context;
        internal ConcurrentQueue<IValue> Notifications { get; } = new();
        internal IValue? Seen;
        internal IValue? Produced;
        internal int Pulses;
    }

    private sealed class EntryProbe
    {
        internal ConcurrentQueue<Activation> Activations { get; } = new();
        internal int Starts => Activations.Count;
        internal Activation Last => Activations.Last();
        internal int Stops;
        internal int Closes;
        internal Func<Activation, CancellationToken, ValueTask>? BeforeActivate;
        internal Func<Activation, CancellationToken, ValueTask>? AfterActivate;
        internal Func<ValueTask>? DisposeExecution;
    }

    private sealed class FactoryProbe
    {
        internal ConcurrentQueue<bool> ReloadFlags { get; } = new();
        internal string? CoordinationKey { get; init; }
    }

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<Gate> _gates = [];
        private readonly ConcurrentDictionary<string, EntryProbe> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FactoryProbe> _factories = new(StringComparer.Ordinal);
        internal PluginHost Host { get; }
        internal ConcurrentQueue<string> ActivationOrder { get; } = new();
        internal ConcurrentQueue<string> DisposalOrder { get; } = new();
        internal int Acquisitions => _factories.Values.Sum(probe => probe.ReloadFlags.Count);
        internal EntryProbe Entry(string id) => _entries.GetOrAdd(id, _ => new());
        internal FactoryProbe Factory(string plugin) => _factories[plugin];
        internal PluginInstanceInfo Info(string id) => Host.Instances.Single(instance => instance.RowId == id);
        internal Harness(PluginRow[] initial, IReadOnlyDictionary<string, string?>? coordinationKeys = null)
        {
            var definitions = new List<PluginDescriptor>();
            foreach (var plugin in new[] { "control", "group", "idle", "provider", "consumer", "conditional" })
            {
                _factories.Add(plugin, new()
                { CoordinationKey = coordinationKeys is not null && coordinationKeys.TryGetValue(plugin, out var key) ? key : null });
                var declaration = new PluginAttribute(plugin)
                {
                    Provides = plugin == "provider" ? [typeof(IValue)] : [],
                    ConditionalProvides = plugin == "conditional" ? [typeof(IValue)] : [],
                    Requires = plugin == "consumer" ? [typeof(IValue)] : [],
                    Observes = plugin == "control" ? [typeof(IValue)] : []
                };
                definitions.Add(new(declaration, new ExecutionFactory(declaration, this), "dynamic-entry-test", "1.0.0", Versions));
            }
            Host = new PluginHost(initial, PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
        }
        internal Gate BlockCleanup(string id, Action<Activation>? verify = null)
        {
            var gate = new Gate();
            _gates.Add(gate);
            Entry(id).AfterActivate = async (activation, cancellationToken) =>
            {
                await activation.Context.AcquireEffectAsync(resourceCancellation => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
                {
                    verify?.Invoke(activation);
                    gate.Entered.TrySetResult();
                    await gate.Release.Task;
                    verify?.Invoke(activation);
                    DisposalOrder.Enqueue($"{id}:resource");
                })));
            };
            return gate;
        }
        internal Gate BlockActivation(string id)
        {
            var gate = new Gate();
            _gates.Add(gate);
            Entry(id).BeforeActivate = async (activation, cancellationToken) =>
            { gate.Entered.TrySetResult(); await gate.Release.Task; };
            return gate;
        }
        /// <summary>Releases every controlled resource before awaiting real host teardown.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var gate in _gates) gate.Release.TrySetResult();
            await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    private sealed class Options
    {
        /// <summary>Gets or sets the value used by provider fixtures.</summary>
        public int Value { get; set; }
        /// <summary>Gets or sets whether activation fails after attempting its staged publication.</summary>
        public bool Fail { get; set; }
    }

    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    {
        /// <summary>Completes only when the fixture's controlled cleanup has finished.</summary>
        public ValueTask DisposeAsync() => release();
    }

    private sealed class ExecutionFactory(PluginAttribute declaration, Harness harness) : IPluginExecutionFactory
    {
        /// <summary>Reports the optional backend identity used to coordinate shared module work.</summary>
        public string? CoordinationKey => harness.Factory(declaration.Id).CoordinationKey;
        /// <summary>Records every acquisition before an entry reaches its activation callback.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            harness.Factory(declaration.Id).ReloadFlags.Enqueue(reload);
            return ValueTask.FromResult<IPluginExecution>(new Execution(declaration, harness));
        }
    }

    private sealed class Execution(PluginAttribute declaration, Harness harness) : IPluginExecution
    {
        private EntryProbe? _entry;
        private string? _rowId;
        /// <summary>Returns the static declaration associated with this known catalog definition.</summary>
        public PluginAttribute Declaration => declaration;
        /// <summary>Reports the version declared by the fixture descriptor.</summary>
        public string Version => "1.0.0";
        /// <summary>Uses the abstraction version loaded by the test process.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Identifies this execution as a backend without a native plugin type.</summary>
        public Type? ImplementationType => null;
        /// <summary>Registers owned callbacks and services, then performs the configured activation behavior.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var options = context.Config.Bind<Options>();
            var entry = harness.Entry(context.Row.Id);
            var activation = new Activation(context);
            _entry = entry;
            _rowId = context.Row.Id;
            entry.Activations.Enqueue(activation);
            harness.ActivationOrder.Enqueue(context.Row.Id);
            context.Effect(() => { Interlocked.Increment(ref entry.Stops); harness.DisposalOrder.Enqueue($"{context.Row.Id}:effect"); });
            context.Effect(context.Events.On<Pulse>(pulse => Interlocked.Increment(ref activation.Pulses)));
            if (declaration.Observes.Contains(typeof(IValue)))
                context.Effect(context.Services.WhenAvailable<IValue>(value => activation.Notifications.Enqueue(value)));
            if (entry.BeforeActivate is { } before) await before(activation, cancellationToken);
            if (declaration.Provides.Contains(typeof(IValue)))
            {
                var value = new Value(options.Value);
                activation.Produced = value;
                context.Effect(value.Close);
                context.Effect(context.Services.Register<IValue>(value));
            }
            if (declaration.Requires.Contains(typeof(IValue))) activation.Seen = context.Services.Get<IValue>();
            if (options.Fail) throw new InvalidOperationException("configured dynamic activation failure");
            if (entry.AfterActivate is { } after) await after(activation, cancellationToken);
        }
        /// <summary>Records backend cleanup attempts and applies any injected release failure.</summary>
        public ValueTask DisposeAsync()
        {
            if (_entry is { } entry) Interlocked.Increment(ref entry.Closes);
            if (_rowId is { } id) harness.DisposalOrder.Enqueue($"{id}:execution");
            return _entry?.DisposeExecution?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
