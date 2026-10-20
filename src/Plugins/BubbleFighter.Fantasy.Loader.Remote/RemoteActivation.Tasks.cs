using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Bridges SDK background work to the activation's native owned-task supervisor.</summary>
internal sealed partial class RemoteActivation
{
    private readonly ConcurrentDictionary<string, RemoteTask> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskInvocation> _taskInvocations = new(StringComparer.Ordinal);
    private long _taskCounter;

    /// <summary>Starts one SDK callback under the native activation resource budget.</summary>
    public async Task<JsonElement> StartTaskAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var name = RequiredString(parameters, "name");
        var callback = RequiredString(parameters, "callback");
        if (Volatile.Read(ref _deactivating) != 0 || Context.Lifetime.IsCancellationRequested)
            throw new RpcException(RpcCodes.ActivationNotLive, "the activation is stopping");
        var id = "t" + Interlocked.Increment(ref _taskCounter);
        var task = new RemoteTask(id, name, callback);
        task.Owner = this;
        lock (_handleGate)
        {
            // Completed results remain addressable until disposal, after native work releases its slot.
            if (_tasks.Count >= _maxHandles)
                throw new RpcException(RpcCodes.Overloaded, "the activation task result capacity is exhausted");
            if (!_tasks.TryAdd(id, task))
                throw new RpcException(RpcCodes.InternalError, "task identity allocation collided");
        }
        try
        {
            var invocation = Guid.NewGuid().ToString("N");
            task.Invocation = invocation;
            var owned = await Context.RunTaskAsync(name, async token =>
            {
                var frame = new TaskInvocation(ExecutionContext.Capture(), token, task);
                _taskInvocations[invocation] = frame;
                try
                {
                    var response = await Source.InvokeTaskAsync(this, new
                    {
                        activation = Id, task = id, invocation, callback, name,
                    }, new { activation = Id, invocation }, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    task.WorkCompleted.TrySetResult(TaskOutcome.Success(response));
                }
                catch (OperationCanceledException error) when (token.IsCancellationRequested)
                {
                    task.WorkCompleted.TrySetResult(TaskOutcome.Canceled(error.Message));
                    throw;
                }
                catch (Exception error)
                {
                    task.WorkCompleted.TrySetResult(TaskOutcome.Fault(error.Message));
                    throw;
                }
                finally
                {
                    await frame.CloseAsync().ConfigureAwait(false);
                    _taskInvocations.TryRemove(invocation, out _);
                }
            }, cancellationToken).ConfigureAwait(false);
            task.Owned = owned;
            _ = ObserveTaskCompletionAsync(task, owned.Completion);
            return JsonSerializer.SerializeToElement(new { task = id });
        }
        catch
        {
            _tasks.TryRemove(id, out _);
            throw;
        }
    }

    private static async Task ObserveTaskCompletionAsync(RemoteTask task, Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
            task.WorkCompleted.TrySetResult(TaskOutcome.Canceled("the native task completed without a callback result"));
        }
        catch (OperationCanceledException error)
        {
            task.WorkCompleted.TrySetResult(TaskOutcome.Canceled(error.Message));
        }
        catch (Exception error)
        {
            task.WorkCompleted.TrySetResult(TaskOutcome.Fault(error.Message));
        }
    }

    /// <summary>Waits for the guest callback's actual work result without releasing its native owner.</summary>
    public async Task<JsonElement> WaitTaskAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var task = FindTask(parameters);
        var outcome = await task.WorkCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(outcome);
    }

    /// <summary>Cancels and joins one owned task, preserving ownership until native cleanup completes.</summary>
    public async Task<JsonElement> DisposeTaskAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var task = FindTask(parameters);
        if (Context.Lifetime.IsCancellationRequested || Volatile.Read(ref _deactivating) != 0)
        {
            // The activation scope owns the native task and will release it after the deactivation
            // hook. Returning here avoids a callback waiting for the parent scope that is already closing.
            _tasks.TryRemove(task.Id, out _);
            return JsonSerializer.SerializeToElement(new { deferred = true });
        }
        if (task.Owned is not { } owned)
            throw new RpcException(RpcCodes.InvalidParams, "the task has not finished admission");
        var invocationId = parameters.TryGetProperty("invocation", out var invocation) && invocation.ValueKind == JsonValueKind.String
            ? invocation.GetString() : null;
        if (invocationId is not null && _taskInvocations.TryGetValue(invocationId, out var frame) && frame.TryEnter())
        {
            try
            {
                ValueTask release = default;
                frame.Run(() => release = owned.DisposeAsync());
                await release.ConfigureAwait(false);
            }
            finally { frame.Exit(); }
        }
        else
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
        _tasks.TryRemove(task.Id, out _);
        return JsonSerializer.SerializeToElement(new { });
    }

    private RemoteTask FindTask(JsonElement parameters)
    {
        var id = RequiredString(parameters, "task");
        if (!_tasks.TryGetValue(id, out var task))
            throw new RpcException(RpcCodes.InvalidParams, $"task '{id}' is unknown, foreign or disposed");
        if (task.Owner != this)
            throw new RpcException(RpcCodes.InvalidParams, "the task belongs to another activation");
        return task;
    }

    private sealed class RemoteTask(string id, string name, string callback)
    {
        internal RemoteActivation Owner { get; set; } = null!;
        internal string Id { get; } = id;
        internal string Name { get; } = name;
        internal string Callback { get; } = callback;
        internal string? Invocation { get; set; }
        internal IOwnedTask? Owned { get; set; }
        internal TaskCompletionSource<TaskOutcome> WorkCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TaskInvocation(ExecutionContext? context, CancellationToken cancellation, RemoteTask task)
        : RemoteCallbackInvocation(context, cancellation)
    {
        internal RemoteTask Task { get; } = task;
    }

    private sealed record TaskOutcome(string State, string? Error, JsonElement? Result)
    {
        internal static TaskOutcome Success(JsonElement result) => new("completed", null, result);
        internal static TaskOutcome Canceled(string? error) => new("canceled", error, null);
        internal static TaskOutcome Fault(string error) => new("faulted", error, null);
    }
}
