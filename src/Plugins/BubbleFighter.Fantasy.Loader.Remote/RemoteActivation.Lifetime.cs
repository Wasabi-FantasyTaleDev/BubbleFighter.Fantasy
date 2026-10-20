using System.Text.Json;

namespace BubbleFighter.Fantasy.Loader.Remote;

internal sealed partial class RemoteActivation
{
    private readonly object _lifetimeGate = new();
    private Task<JsonElement>? _lifetimeObserver;

    /// <summary>Arms cancellation after the SDK has installed this activation's signal, before its user hook runs.</summary>
    public Task<JsonElement> ObserveLifetimeAsync()
    {
        lock (_lifetimeGate)
            return _lifetimeObserver ??= StartLifetimeObserverAsync();
    }

    private async Task<JsonElement> StartLifetimeObserverAsync()
    {
        // The native owner accounts for the observer and its actual write completion. Cancellation
        // callbacks never block on guest I/O, and repeated ready requests cannot reserve more slots.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = await Context.RunTaskAsync($"sdk-lifetime:{Context.Row.Id}", async token =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            if (!Source.IsFaulted)
                await Source.CancelLifetimeAsync(Id).ConfigureAwait(false);
        }).ConfigureAwait(false);
        // Native task admission may succeed just before teardown prevents its body from starting.
        // Do not tell the SDK it is armed in that case; failed readiness triggers local rollback.
        if (await Task.WhenAny(started.Task, observer.Completion).ConfigureAwait(false) != started.Task)
            await observer.Completion.ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { });
    }
}
