using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises source revision cutover and retained cleanup through actual hosted producers and consumers.</summary>
public sealed class PluginDefinitionSourceReplacementTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>();

    /// <summary>Identifies the value published by successive definitions of the same provider.</summary>
    public interface IValue
    {
        /// <summary>Gets the code revision represented by the active provider.</summary>
        int Number { get; }
    }
    private sealed record Value(int Number) : IValue;

    /// <summary>Candidate acquisition keeps the old source usable until complete validation and cutover.</summary>
    [Fact]
    public async Task CandidateAcquisitionLeavesOldSourcePublishedAndUsable()
    {
        var scene = new Scene();
        var entered = Signal(); var release = Signal();
        Task<IPluginDefinitionRegistration>? replacement = null;
        var next = Provider(2); var batch = new Batch(Definition(next));
        try
        {
            await scene.StartAsync();
            replacement = scene.Registration!.ReplaceAsync(1, [], async _ =>
            { entered.TrySetResult(); await release.Task; return batch; }).AsTask();
            await entered.Task.WaitAsync(Limit);
            Assert.Equal(PluginDefinitionSourceState.Published, scene.Registration.Info.State);
            Assert.Equal("1.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.OldBatch.Disposals);
            await scene.AdmitGameAsync();
            Assert.Equal(1, scene.Old.Starts);
            Assert.Equal(0, next.Acquisitions);
            release.TrySetResult();
            var accepted = await replacement.WaitAsync(Limit);
            Assert.NotEqual(scene.Registration.Info.Revision, accepted.Info.Revision);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            await next.Started.Task.WaitAsync(Limit);
        }
        finally
        {
            release.TrySetResult();
            if (replacement is not null) await ObserveAsync(replacement);
            await scene.ShutdownAsync();
        }
    }

    /// <summary>Cutover preserves old cleanup ownership, drains dependents first and never lets an old handle revoke the new revision.</summary>
    [Fact]
    public async Task SameIdentityCutoverWaitsForOldExecutionBeforeNewSameKeyActivation()
    {
        var scene = new Scene(key: "shared-module");
        var release = Signal(); var closing = Signal();
        scene.Old.CloseGate = release; scene.Old.CloseEntered = closing;
        var next = Provider(2, "shared-module"); var batch = new Batch(Definition(next));
        Task? oldDisposal = null;
        try
        {
            await scene.StartAsync();
            await scene.AdmitGameAsync(dependent: true);
            var original = scene.Registration!;
            var accepted = await original.ReplaceAsync(1, ["shared-module"], _ => ValueTask.FromResult<IPluginDefinitionBatch>(batch)).AsTask().WaitAsync(Limit);
            await closing.Task.WaitAsync(Limit);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, next.Acquisitions);
            Assert.Equal(1, scene.Dependent.EffectStops);
            Assert.Equal(0, scene.OldBatch.Disposals);
            Assert.Contains(scene.Host.DefinitionSources, source => source.Revision == original.Info.Revision);
            Assert.Contains(scene.Host.DefinitionSources, source => source.Revision == accepted.Info.Revision);
            oldDisposal = original.DisposeAsync().AsTask();
            Assert.False(oldDisposal.IsCompleted);
            release.TrySetResult();
            await oldDisposal.WaitAsync(Limit);
            await scene.Dependent.SawRevisionTwo.Task.WaitAsync(Limit);
            Assert.Equal([1, 2], scene.Dependent.ObservedValues.ToArray());
            Assert.Equal(1, scene.OldBatch.Disposals);
            Assert.Equal(1, scene.Old.ExecutionDisposals);
            Assert.Equal(PluginDefinitionSourceState.Published, accepted.Info.State);
            await original.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, batch.Disposals);
        }
        finally
        {
            release.TrySetResult();
            if (oldDisposal is not null) await ObserveAsync(oldDisposal);
            await scene.ShutdownAsync();
        }
    }

    /// <summary>Invalid candidate metadata or acquisition failure leaves the old published revision and its executions intact.</summary>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unreserved-key")]
    [InlineData("producer-throws")]
    [InlineData("null-batch")]
    public async Task RejectedCandidateDoesNotWithdrawOldSource(string failure)
    {
        var scene = new Scene();
        var next = Provider(2, failure == "unreserved-key" ? "not-reserved" : null);
        var batch = failure == "duplicate"
            ? new Batch(Definition(next), Definition(Provider(3)))
            : new Batch(Definition(next));
        try
        {
            await scene.StartAsync();
            await scene.AdmitGameAsync();
            var error = await Record.ExceptionAsync(() => scene.Registration!.ReplaceAsync(batch.Definitions.Count, [], _ =>
                failure switch
                {
                    "producer-throws" => throw new InvalidOperationException("candidate producer failed"),
                    "null-batch" => ValueTask.FromResult<IPluginDefinitionBatch>(null!),
                    _ => ValueTask.FromResult<IPluginDefinitionBatch>(batch),
                }).AsTask().WaitAsync(Limit));
            Assert.NotNull(error);
            Assert.IsNotType<TimeoutException>(error);
            Assert.Equal(PluginDefinitionSourceState.Published, scene.Registration!.Info.State);
            Assert.Equal("1.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.OldBatch.Disposals);
            Assert.Equal(0, scene.Old.ExecutionDisposals);
            Assert.Equal(0, next.Acquisitions);
            Assert.Equal(failure is "producer-throws" or "null-batch" ? 0 : 1, batch.Disposals);
            await scene.Host.ReloadAsync("game", default).AsTask().WaitAsync(Limit);
            Assert.Equal(2, scene.Old.Starts);
            Assert.Equal("1.0.0", scene.Host.Instances.Single(row => row.RowId == "game").Version);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>Replacement reserves the old and candidate peak before a producer can allocate any resources.</summary>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 1)]
    public async Task SourceAndDefinitionPeakBudgetsRejectBeforeProducer(int sources, int definitions)
    {
        var scene = new Scene(sourceReserve: sources, definitionReserve: definitions);
        var calls = 0;
        try
        {
            await scene.StartAsync();
            var failure = await Record.ExceptionAsync(() => scene.Registration!.ReplaceAsync(1, [], _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(Provider(2))));
            }).AsTask().WaitAsync(Limit));
            Assert.IsType<ConfigException>(failure);
            Assert.Equal(0, calls);
            Assert.Equal(PluginDefinitionSourceState.Published, scene.Registration!.Info.State);
            Assert.Equal("1.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.OldBatch.Disposals);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>Competing replacements against one old handle fail before the second producer rather than queue behind the first.</summary>
    [Fact]
    public async Task ConcurrentReplacementRequiresTheStillCurrentOldHandle()
    {
        var scene = new Scene(sourceReserve: 3, definitionReserve: 4);
        var entered = Signal(); var release = Signal();
        Task<IPluginDefinitionRegistration>? first = null; var secondCalls = 0;
        try
        {
            await scene.StartAsync();
            first = scene.Registration!.ReplaceAsync(1, [], async _ =>
            { entered.TrySetResult(); await release.Task; return new Batch(Definition(Provider(2))); }).AsTask();
            await entered.Task.WaitAsync(Limit);
            var secondFailure = await Record.ExceptionAsync(() => scene.Registration.ReplaceAsync(1, [], _ =>
            {
                Interlocked.Increment(ref secondCalls);
                return ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(Provider(3))));
            }).AsTask().WaitAsync(Limit));
            Assert.NotNull(secondFailure);
            Assert.IsNotType<TimeoutException>(secondFailure);
            Assert.Equal(0, secondCalls);
            Assert.Equal(PluginDefinitionSourceState.Published, scene.Registration.Info.State);
            release.TrySetResult();
            var accepted = await first.WaitAsync(Limit);
            Assert.Equal(PluginDefinitionSourceState.Published, accepted.Info.State);
            var staleFailure = await Record.ExceptionAsync(() => scene.Registration.ReplaceAsync(1, [], _ =>
            { Interlocked.Increment(ref secondCalls); return ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(Provider(4)))); }).AsTask().WaitAsync(Limit));
            Assert.NotNull(staleFailure);
            Assert.IsNotType<TimeoutException>(staleFailure);
            Assert.Equal(0, secondCalls);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
        }
        finally
        {
            release.TrySetResult();
            if (first is not null) await ObserveAsync(first);
            await scene.ShutdownAsync();
        }
    }

    /// <summary>Caller cancellation stops observation while owner shutdown still waits for and cleans a late candidate batch.</summary>
    [Fact]
    public async Task CancelledObserverDoesNotAbandonCandidateOwnedByClosingSource()
    {
        var scene = new Scene();
        using var caller = new CancellationTokenSource();
        var entered = Signal(); var release = Signal(); var ownerCancelled = Signal();
        var next = Provider(2); var batch = new Batch(Definition(next));
        CancellationToken producerToken = default;
        Task<IPluginDefinitionRegistration>? replacement = null; Task? shutdown = null;
        try
        {
            await scene.StartAsync();
            replacement = scene.Registration!.ReplaceAsync(1, [], async token =>
            {
                producerToken = token;
                using var registration = token.Register(() => ownerCancelled.TrySetResult());
                entered.TrySetResult();
                await release.Task;
                return batch;
            }, caller.Token).AsTask();
            await entered.Task.WaitAsync(Limit);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement.WaitAsync(Limit));
            Assert.False(producerToken.IsCancellationRequested);
            Assert.Equal(PluginDefinitionSourceState.Published, scene.Registration.Info.State);
            shutdown = scene.Host.ShutdownAsync(default).AsTask();
            await ownerCancelled.Task.WaitAsync(Limit);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, batch.Disposals);
            release.TrySetResult();
            await shutdown.WaitAsync(Limit);
            Assert.Equal(1, batch.Disposals);
            Assert.Equal(1, scene.OldBatch.Disposals);
            Assert.Equal(0, next.Acquisitions);
        }
        finally
        {
            release.TrySetResult();
            if (replacement is not null) await ObserveAsync(replacement);
            if (shutdown is not null) await ObserveAsync(shutdown);
            await scene.ShutdownAsync();
        }
    }

    /// <summary>An old execution returned after cutover is cleaned without activation and cannot overwrite the accepted new factory.</summary>
    [Fact]
    public async Task LateOldAcquisitionCannotActivateAfterReplacementCutover()
    {
        var scene = new Scene();
        var release = Signal(); scene.Old.AcquireGate = release;
        IPluginTransition? first = null;
        var next = Provider(2);
        try
        {
            await scene.StartAsync();
            await scene.AdmitGameAsync(disabled: true);
            first = scene.Host.RequestState("game", true);
            await scene.Old.AcquireEntered.Task.WaitAsync(Limit);
            var accepted = await scene.Registration!.ReplaceAsync(1, [],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(next)))).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginDefinitionSourceState.Published, accepted.Info.State);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.Old.Starts);
            release.TrySetResult();
            await ObserveAsync(first.WaitAsync().AsTask());
            await scene.Host.RequestState("game", true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(0, scene.Old.Starts);
            Assert.Equal(1, scene.Old.ExecutionDisposals);
            Assert.Equal(1, scene.Old.Acquisitions);
            Assert.Equal("2.0.0", scene.Host.Instances.Single(row => row.RowId == "game").Version);
            Assert.True(next.Starts > 0);
        }
        finally
        {
            release.TrySetResult();
            if (first is not null) await ObserveAsync(first.WaitAsync().AsTask());
            await scene.ShutdownAsync();
        }
    }

    /// <summary>A new revision's activation failure cannot authorize the old committed factory as a fallback.</summary>
    [Fact]
    public async Task FailedNewActivationRetriesNewRevisionInsteadOfCachedOldFactory()
    {
        var scene = new Scene();
        var next = Provider(2); next.ActivationError = "new revision activation failed";
        try
        {
            await scene.StartAsync(); await scene.AdmitGameAsync();
            await scene.Registration!.ReplaceAsync(1, [],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(next)))).AsTask().WaitAsync(Limit);
            await next.Started.Task.WaitAsync(Limit);
            var failure = await Record.ExceptionAsync(() => scene.Host.RequestState("game", true).WaitAsync().AsTask().WaitAsync(Limit));
            Assert.NotNull(failure);
            Assert.Contains("new revision activation failed", failure.ToString());
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(1, scene.Old.Starts);
            Assert.Equal(1, scene.Old.Acquisitions);
            next.ActivationError = null;
            await scene.Host.LoadAsync("game", default).AsTask().WaitAsync(Limit);
            Assert.Equal("2.0.0", scene.Host.Instances.Single(row => row.RowId == "game").Version);
            Assert.Equal(1, scene.Old.Acquisitions);
            Assert.True(next.Starts >= 2);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>Failed old batch cleanup retains peak capacity without removing the successfully published replacement.</summary>
    [Fact]
    public async Task FailedOldBatchCleanupRetainsReservationWhileNewDefinitionRemainsVisible()
    {
        var scene = new Scene(sourceReserve: 2, definitionReserve: 2);
        scene.OldBatch.CloseError = "old batch cleanup failed";
        var next = Provider(2); var nextBatch = new Batch(Definition(next)); var calls = 0;
        try
        {
            await scene.StartAsync();
            var original = scene.Registration!;
            var accepted = await original.ReplaceAsync(1, [], _ => ValueTask.FromResult<IPluginDefinitionBatch>(nextBatch)).AsTask().WaitAsync(Limit);
            var oldFailure = await Record.ExceptionAsync(() => original.DisposeAsync().AsTask().WaitAsync(Limit));
            Assert.NotNull(oldFailure);
            Assert.Contains("old batch cleanup failed", oldFailure.ToString());
            Assert.Equal(PluginDefinitionSourceState.CleanupFailed, original.Info.State);
            Assert.Equal(PluginDefinitionSourceState.Published, accepted.Info.State);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(2, scene.Host.DefinitionSources.Count);
            var budgetFailure = await Record.ExceptionAsync(() => accepted.ReplaceAsync(1, [], _ =>
            { Interlocked.Increment(ref calls); return ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(Provider(3)))); }).AsTask().WaitAsync(Limit));
            Assert.IsType<ConfigException>(budgetFailure);
            Assert.Equal(0, calls);
            Assert.Equal(0, nextBatch.Disposals);
        }
        finally
        {
            var failure = await Record.ExceptionAsync(() => scene.ShutdownAsync());
            Assert.NotNull(failure);
            Assert.IsNotType<TimeoutException>(failure);
            Assert.Contains("old batch cleanup failed", failure.ToString());
        }
    }

    /// <summary>Owner shutdown waits for a same-key candidate producer instead of treating its owned key as an unload conflict.</summary>
    [Fact]
    public async Task ShutdownWaitsForActualSameKeyCandidateProducerAndCleansBothBatches()
    {
        var scene = new Scene(key: "shared-worker");
        var entered = Signal(); var release = Signal(); var ownerCancelled = Signal();
        var next = Provider(2, "shared-worker"); var batch = new Batch(Definition(next));
        Task<IPluginDefinitionRegistration>? replacement = null; Task? shutdown = null;
        try
        {
            await scene.StartAsync();
            replacement = scene.Registration!.ReplaceAsync(1, ["shared-worker"], async token =>
            {
                using var registration = token.Register(() => ownerCancelled.TrySetResult());
                entered.TrySetResult();
                await release.Task;
                return batch;
            }).AsTask();
            await entered.Task.WaitAsync(Limit);
            shutdown = scene.Host.ShutdownAsync(default).AsTask();
            await ownerCancelled.Task.WaitAsync(Limit);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, batch.Disposals);
            release.TrySetResult();
            await shutdown.WaitAsync(Limit);
            await ObserveAsync(replacement);
            Assert.Equal(1, batch.Disposals);
            Assert.Equal(1, scene.OldBatch.Disposals);
            Assert.Equal(0, next.Acquisitions);
        }
        finally
        {
            release.TrySetResult();
            if (replacement is not null) await ObserveAsync(replacement);
            if (shutdown is not null) await ObserveAsync(shutdown);
            await scene.ShutdownAsync();
        }
    }

    /// <summary>Live acquisition of the old key rejects replacement before its producer and releases the exclusion for a later retry.</summary>
    [Fact]
    public async Task ActiveOldAcquisitionRejectsSameKeyProducerBeforeCallbackThenAllowsRetry()
    {
        var scene = new Scene(key: "shared-worker");
        var release = Signal(); scene.Old.AcquireGate = release;
        var next = Provider(2, "shared-worker"); var batch = new Batch(Definition(next));
        IPluginTransition? first = null; var calls = 0;
        try
        {
            await scene.StartAsync(); await scene.AdmitGameAsync(disabled: true);
            first = scene.Host.RequestState("game", true);
            await scene.Old.AcquireEntered.Task.WaitAsync(Limit);
            var rejected = await Record.ExceptionAsync(() => scene.Registration!.ReplaceAsync(1, ["shared-worker"], _ =>
            { Interlocked.Increment(ref calls); return ValueTask.FromResult<IPluginDefinitionBatch>(batch); }).AsTask().WaitAsync(Limit));
            Assert.NotNull(rejected); Assert.IsNotType<TimeoutException>(rejected);
            Assert.Equal(0, calls);
            Assert.Equal(PluginDefinitionSourceState.Published, scene.Registration!.Info.State);
            Assert.Equal("1.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.OldBatch.Disposals);
            Assert.Equal(0, scene.Old.ExecutionDisposals);
            release.TrySetResult();
            await first.WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(1, scene.Old.Starts);
            await scene.Registration.ReplaceAsync(1, ["shared-worker"], _ =>
            { Interlocked.Increment(ref calls); return ValueTask.FromResult<IPluginDefinitionBatch>(batch); }).AsTask().WaitAsync(Limit);
            await next.Started.Task.WaitAsync(Limit);
            Assert.Equal(1, calls);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
        }
        finally
        {
            release.TrySetResult();
            if (first is not null) await ObserveAsync(first.WaitAsync().AsTask());
            await scene.ShutdownAsync();
        }
    }

    /// <summary>A different producer key may cut over while old acquisition is pending, but that late old executor still cannot activate.</summary>
    [Fact]
    public async Task IndependentReplacementKeyCanCutOverWithoutAuthorizingLateOldExecution()
    {
        var scene = new Scene(key: "old-worker");
        var release = Signal(); scene.Old.AcquireGate = release;
        var next = Provider(2, "new-worker"); IPluginTransition? first = null;
        try
        {
            await scene.StartAsync(); await scene.AdmitGameAsync(disabled: true);
            first = scene.Host.RequestState("game", true);
            await scene.Old.AcquireEntered.Task.WaitAsync(Limit);
            var accepted = await scene.Registration!.ReplaceAsync(1, ["new-worker"],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(next)))).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginDefinitionSourceState.Published, accepted.Info.State);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.Old.Starts);
            release.TrySetResult();
            await ObserveAsync(first.WaitAsync().AsTask());
            await scene.Host.RequestState("game", true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(0, scene.Old.Starts);
            Assert.Equal(1, scene.Old.ExecutionDisposals);
            Assert.True(next.Starts > 0);
        }
        finally
        {
            release.TrySetResult();
            if (first is not null) await ObserveAsync(first.WaitAsync().AsTask());
            await scene.ShutdownAsync();
        }
    }

    /// <summary>Replacing metadata cannot let the new same-key factory run while the old source batch disposer still owns its backend.</summary>
    [Fact]
    public async Task SameKeyFactoryWaitsForActualOldBatchDisposal()
    {
        var scene = new Scene(key: "shared-worker");
        var closing = Signal(); var release = Signal();
        scene.OldBatch.CloseEntered = closing; scene.OldBatch.CloseGate = release;
        var next = Provider(2, "shared-worker");
        try
        {
            await scene.StartAsync(); await scene.AdmitGameAsync();
            var original = scene.Registration!;
            await original.ReplaceAsync(1, ["shared-worker"],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(next)))).AsTask().WaitAsync(Limit);
            await closing.Task.WaitAsync(Limit);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(1, scene.Old.ExecutionDisposals);
            Assert.Equal(0, next.Acquisitions);
            var oldDisposal = original.DisposeAsync().AsTask();
            Assert.False(oldDisposal.IsCompleted);
            release.TrySetResult();
            await oldDisposal.WaitAsync(Limit);
            await next.Started.Task.WaitAsync(Limit);
            Assert.Equal(1, scene.OldBatch.Disposals);
            Assert.True(next.Acquisitions > 0);
        }
        finally { release.TrySetResult(); await scene.ShutdownAsync(); }
    }

    /// <summary>A failed old source backend release keeps its same key unavailable even though replacement metadata is published.</summary>
    [Fact]
    public async Task FailedOldBatchCleanupDoesNotReleaseSameKeyForReplacementFactory()
    {
        var scene = new Scene(key: "shared-worker");
        scene.OldBatch.CloseError = "old same-key batch cleanup failed";
        var next = Provider(2, "shared-worker");
        try
        {
            await scene.StartAsync(); await scene.AdmitGameAsync();
            var original = scene.Registration!;
            await original.ReplaceAsync(1, ["shared-worker"],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(next)))).AsTask().WaitAsync(Limit);
            var oldFailure = await Record.ExceptionAsync(() => original.DisposeAsync().AsTask().WaitAsync(Limit));
            Assert.NotNull(oldFailure);
            Assert.Contains("old same-key batch cleanup failed", oldFailure.ToString());
            Assert.Equal(PluginDefinitionSourceState.CleanupFailed, original.Info.State);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, next.Acquisitions);
            var rejected = await Record.ExceptionAsync(() => scene.Host.LoadAsync("game", default).AsTask().WaitAsync(Limit));
            Assert.NotNull(rejected); Assert.IsNotType<TimeoutException>(rejected);
            Assert.Equal(0, next.Acquisitions);
        }
        finally
        {
            var failure = await Record.ExceptionAsync(() => scene.ShutdownAsync());
            Assert.NotNull(failure); Assert.IsNotType<TimeoutException>(failure);
            Assert.Contains("old same-key batch cleanup failed", failure.ToString());
        }
    }

    /// <summary>A candidate producer cannot dispose its own predecessor while holding the replacement operation it must finish.</summary>
    [Fact]
    public async Task CandidateProducerCannotDisposePredecessorOrPoisonReusablePeakBudget()
    {
        var scene = new Scene(sourceReserve: 2, definitionReserve: 2, key: "shared-worker");
        try
        {
            await scene.StartAsync();
            var original = scene.Registration!;
            var failure = await Record.ExceptionAsync(() => original.ReplaceAsync(1, ["shared-worker"], async _ =>
            {
                await original.DisposeAsync();
                return new Batch(Definition(Provider(2, "shared-worker")));
            }).AsTask().WaitAsync(Limit));
            Assert.NotNull(failure); Assert.IsNotType<TimeoutException>(failure);
            Assert.Equal(PluginDefinitionSourceState.Published, original.Info.State);
            Assert.Equal("1.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.OldBatch.Disposals);
            Assert.Equal(PluginState.Active, scene.Host.Instances.Single(row => row.RowId == "owner").State);
            var accepted = await original.ReplaceAsync(1, ["shared-worker"],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(Provider(2, "shared-worker"))))).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginDefinitionSourceState.Published, accepted.Info.State);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>A candidate callback cannot synchronously wait for host shutdown and the rejected attempt does not withdraw its owner.</summary>
    [Fact]
    public async Task CandidateProducerCannotAwaitHostShutdownAndValidReplacementCanStillProceed()
    {
        var scene = new Scene(sourceReserve: 2, definitionReserve: 2, key: "shared-worker");
        try
        {
            await scene.StartAsync();
            var original = scene.Registration!;
            var failure = await Record.ExceptionAsync(() => original.ReplaceAsync(1, ["shared-worker"], async _ =>
            {
                await scene.Host.ShutdownAsync(default);
                return new Batch(Definition(Provider(2, "shared-worker")));
            }).AsTask().WaitAsync(Limit));
            Assert.NotNull(failure); Assert.IsNotType<TimeoutException>(failure);
            Assert.Equal(PluginDefinitionSourceState.Published, original.Info.State);
            Assert.Equal("1.0.0", scene.ProviderInfo().Version);
            Assert.Equal(0, scene.OldBatch.Disposals);
            Assert.Equal(PluginState.Active, scene.Host.Instances.Single(row => row.RowId == "owner").State);
            var accepted = await original.ReplaceAsync(1, ["shared-worker"],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(Provider(2, "shared-worker"))))).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginDefinitionSourceState.Published, accepted.Info.State);
            Assert.Equal("2.0.0", scene.ProviderInfo().Version);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>External shutdown cancels an owner-bound same-key producer without requiring an unrelated gate to release it.</summary>
    [Fact]
    public async Task ExternalShutdownCancelsCooperativeSameKeyProducerAndFinishesCleanup()
    {
        var scene = new Scene(key: "shared-worker");
        var entered = Signal(); var cancelled = Signal();
        Task<IPluginDefinitionRegistration>? replacement = null; Task? shutdown = null;
        var returned = 0;
        try
        {
            await scene.StartAsync();
            replacement = scene.Registration!.ReplaceAsync(1, ["shared-worker"], async token =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { cancelled.TrySetResult(); throw; }
                Interlocked.Increment(ref returned);
                return new Batch(Definition(Provider(2, "shared-worker")));
            }).AsTask();
            await entered.Task.WaitAsync(Limit);
            shutdown = scene.Host.ShutdownAsync(default).AsTask();
            await cancelled.Task.WaitAsync(Limit);
            await shutdown.WaitAsync(Limit);
            var failure = await Record.ExceptionAsync(() => replacement.WaitAsync(Limit));
            Assert.NotNull(failure); Assert.IsNotType<TimeoutException>(failure);
            Assert.Equal(0, Volatile.Read(ref returned));
            Assert.Equal(1, scene.OldBatch.Disposals);
            Assert.DoesNotContain(scene.Host.Definitions, definition => definition.Id == "dynamic.provider" && definition.Version == "2.0.0");
        }
        finally
        {
            if (shutdown is null) shutdown = scene.Host.ShutdownAsync(default).AsTask();
            await ObserveAsync(shutdown);
            if (replacement is not null) await ObserveAsync(replacement);
            await scene.ShutdownAsync();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ObserveAsync(Task task)
    {
        var error = await Record.ExceptionAsync(() => task.WaitAsync(Limit));
        Assert.IsNotType<TimeoutException>(error);
    }
    private static Factory Provider(int revision, string? key = null) =>
        new(new PluginAttribute("dynamic.provider") { Provides = [typeof(IValue)] }, $"{revision}.0.0", revision, key);
    private static PluginDefinition Definition(Factory factory) =>
        new(factory.Declaration, factory, "replacement-test", factory.Version, Versions);
    private static PluginRow Row(string id, string plugin) => new()
    { Id = id, Plugin = plugin, Source = "replacement-test", Lifecycle = new() { EffectCapacity = 4 } };

    private sealed class Scene
    {
        internal Factory Old { get; }
        internal Factory Dependent { get; } = new(new PluginAttribute("dependent") { Requires = [typeof(IValue)] }, "1.0.0", 0);
        internal Batch OldBatch { get; }
        internal IPluginDefinitionRegistration? Registration;
        internal PluginHost Host { get; }
        private readonly PluginRow _owner;

        internal Scene(int sourceReserve = 2, int definitionReserve = 4, string? key = null)
        {
            Old = Provider(1, key); OldBatch = new(Definition(Old));
            var owner = new Factory(new PluginAttribute("source.owner") { Requires = [typeof(IPluginHost)] }, "1.0.0", 0);
            owner.Activate = async context =>
            {
                Registration = await context.Services.Get<IPluginHost>().RegisterDefinitionSourceAsync(
                    context, "source", 1, key is null ? [] : [key], _ => ValueTask.FromResult<IPluginDefinitionBatch>(OldBatch));
            };
            _owner = Row("owner", owner.Declaration.Id) with
            { Lifecycle = new() { EffectCapacity = 16, EntryReserve = 2, ServiceMetadataReserve = 16,
                DefinitionSourceReserve = sourceReserve, DefinitionReserve = definitionReserve } };
            Host = new([_owner], PluginCatalog.FromDefinitions([new PluginDescriptor(Definition(owner)), new PluginDescriptor(Definition(Dependent))]), NullLoggerFactory.Instance);
        }
        internal Task StartAsync() => Host.StartAsync(default).AsTask().WaitAsync(Limit);
        internal Task AdmitGameAsync(bool dependent = false, bool disabled = false) => Host.ReconcileConfigurationAsync(
            dependent ? [_owner, Row("game", Old.Declaration.Id) with { Disabled = disabled }, Row("dependent", Dependent.Declaration.Id)]
                : [_owner, Row("game", Old.Declaration.Id) with { Disabled = disabled }]).AsTask().WaitAsync(Limit);
        internal PluginDefinitionInfo ProviderInfo() => Host.Definitions.Single(definition => definition.Id == Old.Declaration.Id);
        internal Task ShutdownAsync() => Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
    }

    private sealed class Factory(PluginAttribute declaration, string version, int number, string? key = null) : IPluginExecutionFactory
    {
        internal PluginAttribute Declaration => declaration;
        internal string Version => version;
        internal int Acquisitions; internal int Starts; internal int EffectStops; internal int ExecutionDisposals;
        internal TaskCompletionSource AcquireEntered { get; } = Signal();
        internal TaskCompletionSource Started { get; } = Signal();
        internal TaskCompletionSource SawRevisionTwo { get; } = Signal();
        internal TaskCompletionSource? AcquireGate;
        internal TaskCompletionSource? CloseGate;
        internal TaskCompletionSource? CloseEntered;
        internal string? ActivationError;
        internal Func<IPluginContext, ValueTask>? Activate;
        internal ConcurrentQueue<int> ObservedValues { get; } = new();
        /// <summary>Provides the backend key reserved by the source before acquisition.</summary>
        public string? CoordinationKey => key;
        /// <summary>Can return late despite cancellation so source revision fencing is exercised against a real owned result.</summary>
        public async ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Acquisitions); AcquireEntered.TrySetResult();
            if (AcquireGate is { } gate) await gate.Task;
            return new Execution(this, number);
        }
    }

    private sealed class Execution(Factory owner, int number) : IPluginExecution
    {
        /// <summary>Returns the resolved declaration used for host service admission.</summary>
        public PluginAttribute Declaration => owner.Declaration;
        /// <summary>Identifies the actual fixture code revision without using a native type.</summary>
        public string Version => owner.Version;
        /// <summary>Uses no additional shared contract versions in this neutral fixture.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Leaves implementation types absent to exercise the common backend path.</summary>
        public Type? ImplementationType => null;
        /// <summary>Publishes or consumes the real host service and records reverse-order effect cleanup.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.Starts); owner.Started.TrySetResult();
            if (owner.ActivationError is { } failure) throw new InvalidOperationException(failure);
            if (owner.Declaration.Provides.Contains(typeof(IValue))) context.Services.Register<IValue>(new Value(number));
            if (owner.Declaration.Requires.Contains(typeof(IValue)))
            {
                var observed = context.Services.Get<IValue>().Number; owner.ObservedValues.Enqueue(observed);
                if (observed == 2) owner.SawRevisionTwo.TrySetResult();
            }
            context.Effect(() => Interlocked.Increment(ref owner.EffectStops));
            if (owner.Activate is { } activate) await activate(context);
        }
        /// <summary>Can hold actual old backend cleanup after its dependents and effects have drained.</summary>
        public async ValueTask DisposeAsync()
        {
            owner.CloseEntered?.TrySetResult();
            if (owner.CloseGate is { } gate) await gate.Task;
            Interlocked.Increment(ref owner.ExecutionDisposals);
        }
    }

    private sealed class Batch(params PluginDefinition[] definitions) : IPluginDefinitionBatch
    {
        internal int Disposals; internal string? CloseError;
        internal TaskCompletionSource? CloseEntered;
        internal TaskCompletionSource? CloseGate;
        /// <summary>Exposes the complete candidate definitions for atomic host validation.</summary>
        public IReadOnlyList<PluginDefinition> Definitions { get; } = Array.AsReadOnly(definitions);
        /// <summary>Records true cleanup attempts and can retain failed batch ownership for budget assertions.</summary>
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            CloseEntered?.TrySetResult();
            if (CloseGate is { } gate) await gate.Task;
            if (CloseError is { } failure) throw new InvalidOperationException(failure);
        }
    }
}
