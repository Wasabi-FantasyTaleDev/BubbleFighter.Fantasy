using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks decorator admission and actual inner completion through a real SDK process and hosted service chain.</summary>
[Collection("SDK compilation")]
public sealed class ServiceProviderLifetimeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Host admission rejects another activation's token and a completed callback's token, including raw protocol requests.</summary>
    [Fact]
    public async Task ForeignAndExpiredInnerTokensCannotReenterTheCapturedChain()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(key, "scope");
        await host.StartAsync();
        var service = SdkProviderConsumer.States[key].Fighters;
        Assert.Empty(await service.ListByUserNumAsync(10, default));
        Assert.Empty(await service.ListByUserNumAsync(11, default));
        Assert.Equal(4, SdkLifetimeBase.States[key].Calls);
        Assert.Equal(2, host.Loggers.Entries.Count(entry => entry.Contains("foreign-inner-rejected", StringComparison.Ordinal)));
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("expired-inner-rejected", StringComparison.Ordinal));
    }

    /// <summary>Returning an SDK result closes further inner admission while admitted native work still delays completion and unload.</summary>
    [Fact]
    public async Task ReturnedCallbackMustDrainItsAlreadyAdmittedInnerCallBeforeUnload()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(key, "detach");
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Loggers.OnEntry = entry =>
        {
            if (entry.Contains("detached-inner-admitted", StringComparison.Ordinal)) returned.TrySetResult();
        };
        await host.StartAsync();
        var state = SdkLifetimeBase.States[key];
        var call = SdkProviderConsumer.States[key].Fighters.ListByUserNumAsync(1, default).AsTask();
        Task? unload = null;
        try
        {
            await returned.Task.WaitAsync(Limit);
            Assert.True(state.Entered.Task.IsCompleted);
            Assert.False(call.IsCompleted);
            await host.Host.UnloadAsync("consumer", default);
            unload = host.Host.UnloadAsync("decorator", default).AsTask();
            await state.Cancelled.Task.WaitAsync(Limit);
            Assert.False(unload.IsCompleted);
            state.Release.TrySetResult();
            await unload.WaitAsync(Limit);
            Assert.Equal(PluginState.Unloaded, host.Instance("decorator").State);
            _ = await Record.ExceptionAsync(() => call.WaitAsync(Limit));
            Assert.True(call.IsCompleted);
            Assert.Equal(1, state.Completed);
        }
        finally
        {
            state.Release.TrySetResult();
            if (unload is not null) _ = await Record.ExceptionAsync(() => unload.WaitAsync(Limit));
            _ = await Record.ExceptionAsync(() => call.WaitAsync(Limit));
        }
    }

    /// <summary>A provider deadline reports failure without treating a noncooperative inner operation as completed.</summary>
    [Fact]
    public async Task ProviderTimeoutRetainsAlreadyAdmittedNativeWorkUntilActualCompletion()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(key, "detach");
        await host.StartAsync();
        var state = SdkLifetimeBase.States[key];
        var call = SdkProviderConsumer.States[key].Fighters.ListByUserNumAsync(1, default).AsTask();
        try
        {
            await state.Entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<RemoteTimeoutException>(() => call.WaitAsync(TimeSpan.FromSeconds(30)));
            await state.Cancelled.Task.WaitAsync(Limit);
            Assert.Equal(0, Volatile.Read(ref state.Completed));
            // The deadline has expired, but releasing the real operation is still necessary for teardown.
            state.Release.TrySetResult();
            await state.Finished.Task.WaitAsync(Limit);
            Assert.Equal(1, Volatile.Read(ref state.Completed));
            // A faulted row must still report its original failure after its resources have drained.
            Exception failure = Assert.IsType<AggregateException>(await host.ShutdownAsync());
            while (failure.InnerException is { } inner) failure = inner;
            Assert.IsType<RemoteTimeoutException>(failure);
            Assert.Equal(PluginState.Failed, host.Instance("decorator").State);
            Assert.DoesNotContain(host.Host.Instances, row => row.State is PluginState.CleanupIncomplete or PluginState.CleanupFailed);
            Assert.Single(host.Loggers.Entries, entry => entry.Contains("lifetime-decorator-deactivated", StringComparison.Ordinal));
        }
        finally
        {
            state.Release.TrySetResult();
            _ = await Record.ExceptionAsync(() => call.WaitAsync(Limit));
        }
    }

    private static RemoteHarness Create(string key, string behavior) => new([new Dictionary<string, object?>
    {
        ["command"] = Python.Command,
        ["args"] = new List<object?> { Path.Combine(Python.RepoRoot, "sdk/python/tests/provider_lifecycle.py") },
        ["source_id"] = "lifetime",
    }], initialRows:
    [
        ServiceProviderTests.Row("base", "test.sdk-lifetime-base", ("key", key)),
        ServiceProviderTests.Row("observer", "conformance.scope-observer"),
        ServiceProviderTests.Row("decorator", "conformance.lifetime-decorator", ("behavior", behavior)),
        ServiceProviderTests.Row("consumer", "test.sdk-provider-consumer", ("key", key)),
    ]);
}

/// <summary>Supplies a real decoratable native repository whose deliberately noncooperative call can be drained explicitly.</summary>
[Plugin("test.sdk-lifetime-base", Provides = [typeof(IFighterRepository), typeof(IEquipmentReplyPolicy)])]
public sealed class SdkLifetimeBase : IPlugin
{
    internal static ConcurrentDictionary<string, State> States { get; } = new();

    /// <summary>Publishes owned native services and removes the observation after all dependent activations stop.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var key = (string)context.Row.Config!["key"]!;
        var state = new State();
        States[key] = state;
        context.Effect(() => States.TryRemove(key, out _));
        context.Effect(context.Services.RegisterDecoratable<IFighterRepository>(state));
        context.Effect(context.Services.Register<IEquipmentReplyPolicy>(new Policy()));
        return ValueTask.CompletedTask;
    }

    internal sealed class State : IFighterRepository
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal int Completed;

        public async ValueTask<IReadOnlyList<FighterRecord>> ListByUserNumAsync(long userNum, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (userNum == 1)
            {
                using var registration = ct.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult();
                await Release.Task;
            }
            Interlocked.Increment(ref Completed);
            Finished.TrySetResult();
            return [];
        }

        public async ValueTask UpsertAsync(FighterRecord record, CancellationToken ct) => await Entered.Task.WaitAsync(ct);
    }

    private sealed class Policy : IEquipmentReplyPolicy { public bool ReplyAfterInventory => false; }
}
