using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Wake and sleep of an on-demand group, against a host that only records what it is asked.</summary>
public sealed class ActivationGroupsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task FirstConnectionLoadsTheGroupBeforeTheListenerGoesOn()
    {
        var host = new FakeHost();
        using var scene = new Scene(host);
        host.HoldRequests = true;

        var accepted = scene.Groups.AcceptedAsync(new(new FakeConnection(1), "listener"), default).AsTask();
        await host.Requested.Task.WaitAsync(Limit);
        Assert.False(accepted.IsCompleted);
        host.Release();
        await accepted.WaitAsync(Limit);

        Assert.Equal(PluginState.Active, host.State("a"));
        Assert.Equal(PluginState.Active, host.State("b"));
        // The row that is switched off is neither asked for nor waited for.
        Assert.Equal(["a:True", "b:True"], host.Requests.Order());
        Assert.Equal(PluginState.Unloaded, host.State("off"));
        Assert.Equal(1, scene.Compactions);
    }

    [Fact]
    public async Task ConnectionToALoadedGroupAsksForNothing()
    {
        var host = new FakeHost();
        using var scene = new Scene(host);
        await scene.Groups.AcceptedAsync(new(new FakeConnection(1), "listener"), default).AsTask().WaitAsync(Limit);
        host.Requests.Clear();

        await scene.Groups.AcceptedAsync(new(new FakeConnection(2), "listener"), default).AsTask().WaitAsync(Limit);
        await scene.Groups.AcceptedAsync(new(new FakeConnection(3), "elsewhere"), default).AsTask().WaitAsync(Limit);

        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task GroupUnloadsOnlyAfterItsLastConnectionAndItsIdleTime()
    {
        var host = new FakeHost();
        using var scene = new Scene(host, idle: TimeSpan.FromMilliseconds(150));
        var first = new FakeConnection(1);
        var second = new FakeConnection(2);
        await scene.Groups.AcceptedAsync(new(first, "listener"), default).AsTask().WaitAsync(Limit);
        await scene.Groups.AcceptedAsync(new(second, "listener"), default).AsTask().WaitAsync(Limit);

        scene.Groups.Closed(new(first, "test"));
        await Task.Delay(400);
        Assert.Equal(PluginState.Active, host.State("a"));

        scene.Groups.Closed(new(second, "test"));
        await host.WaitForAsync(() => host.State("a") == PluginState.Unloaded && host.State("b") == PluginState.Unloaded, Limit);
        Assert.Equal(PluginState.Active, host.State("listener"));
    }

    [Fact]
    public async Task ConnectionDuringTheIdleTimeKeepsTheGroup()
    {
        var host = new FakeHost();
        using var scene = new Scene(host, idle: TimeSpan.FromMilliseconds(300));
        var first = new FakeConnection(1);
        await scene.Groups.AcceptedAsync(new(first, "listener"), default).AsTask().WaitAsync(Limit);
        scene.Groups.Closed(new(first, "test"));
        await scene.Groups.AcceptedAsync(new(new FakeConnection(2), "listener"), default).AsTask().WaitAsync(Limit);
        host.Requests.Clear();

        await Task.Delay(700);

        Assert.Empty(host.Requests);
        Assert.Equal(PluginState.Active, host.State("a"));
    }

    [Fact]
    public async Task GroupThatStillHoldsStateStaysLoadedAndIsAskedAgain()
    {
        var host = new FakeHost();
        var asked = 0;
        using var scene = new Scene(host, idle: TimeSpan.FromMilliseconds(100), inUse: _ => Interlocked.Increment(ref asked) < 3 ? "1 room(s)" : null);
        var first = new FakeConnection(1);
        await scene.Groups.AcceptedAsync(new(first, "listener"), default).AsTask().WaitAsync(Limit);
        scene.Groups.Closed(new(first, "test"));

        await host.WaitForAsync(() => host.State("a") == PluginState.Unloaded, Limit);

        Assert.Equal(3, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task ConnectionIsClosedWhenTheGroupDoesNotLoadInTime()
    {
        var host = new FakeHost { HoldRequests = true };
        using var scene = new Scene(host, wakeTimeout: TimeSpan.FromMilliseconds(200));
        var connection = new FakeConnection(1);

        await scene.Groups.AcceptedAsync(new(connection, "listener"), default).AsTask().WaitAsync(Limit);

        Assert.Equal("server is still loading this client version", connection.Reason);
        host.Release();
    }

    [Fact]
    public async Task ConnectionIsClosedWhenARowOfTheGroupFails()
    {
        var host = new FakeHost { Fail = "b" };
        using var scene = new Scene(host);
        var connection = new FakeConnection(1);

        await scene.Groups.AcceptedAsync(new(connection, "listener"), default).AsTask().WaitAsync(Limit);

        Assert.True(connection.IsClosed);
    }

    private sealed class Scene : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        internal readonly ActivationGroups Groups;
        internal int Compactions;

        internal Scene(FakeHost host, TimeSpan? idle = null, TimeSpan? wakeTimeout = null, Func<string, string?>? inUse = null) =>
            Groups = new(host, inUse ?? (_ => null), idle ?? TimeSpan.FromMinutes(30), wakeTimeout ?? TimeSpan.FromSeconds(10),
                NullLogger.Instance, _lifetime.Token, () => Interlocked.Increment(ref Compactions));

        public void Dispose()
        {
            Groups.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    /// <summary>An always-on listener, two on-demand rows of group "g" and one row of the group that its configuration switches off.</summary>
    private sealed class FakeHost : IPluginHost
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, PluginState> _states = new()
        {
            ["listener"] = PluginState.Active, ["a"] = PluginState.Unloaded, ["b"] = PluginState.Unloaded, ["off"] = PluginState.Unloaded,
            ["elsewhere"] = PluginState.Active,
        };
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<string> Requests = new();
        internal bool HoldRequests;
        internal string? Fail;

        internal void Release() => _release.TrySetResult();

        internal PluginState State(string rowId) { lock (_gate) return _states[rowId]; }

        internal async Task WaitForAsync(Func<bool> reached, TimeSpan limit)
        {
            var deadline = DateTime.UtcNow + limit;
            while (!reached())
            {
                Assert.True(DateTime.UtcNow < deadline, "The host did not reach the expected state.");
                await Task.Delay(20);
            }
        }

        public IReadOnlyList<PluginInstanceInfo> Instances
        {
            get
            {
                lock (_gate)
                    return _states.Select(pair => new PluginInstanceInfo(pair.Key, "test", "test", pair.Value,
                        pair.Value == PluginState.Failed ? "failed on purpose" : null)
                    {
                        Activation = pair.Key is "a" or "b" or "off" ? PluginActivation.OnDemand : PluginActivation.Always,
                        IsDisabled = pair.Key == "off",
                        ActivationGroup = pair.Key == "elsewhere" ? null : "g",
                    }).ToArray();
            }
        }

        public ValueTask<ReloadResult> ReloadAsync(string rowId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask UnloadAsync(string rowId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask LoadAsync(string rowId, CancellationToken ct) => throw new NotSupportedException();

        public IPluginTransition RequestState(string rowId, bool enabled, bool restart = false)
        {
            Requests.Enqueue($"{rowId}:{enabled}");
            Requested.TrySetResult();
            return new Transition(rowId, Apply(rowId, enabled));
        }

        public IPluginTransition RequestStates(IReadOnlyCollection<string> rowIds, bool enabled)
        {
            foreach (var rowId in rowIds) Requests.Enqueue($"{rowId}:{enabled}");
            Requested.TrySetResult();
            return new Transition(rowIds.First(), ApplyAll(rowIds, enabled));
        }

        private async Task<PluginTransitionResult> ApplyAll(IReadOnlyCollection<string> rowIds, bool enabled)
        {
            foreach (var rowId in rowIds) await Apply(rowId, enabled);
            return new(PluginTransitionOutcome.Converged, rowIds.ToArray());
        }

        private async Task<PluginTransitionResult> Apply(string rowId, bool enabled)
        {
            if (HoldRequests) await _release.Task;
            lock (_gate) _states[rowId] = !enabled ? PluginState.Unloaded : rowId == Fail ? PluginState.Failed : PluginState.Active;
            return new(PluginTransitionOutcome.Converged, [rowId]);
        }

        private sealed class Transition(string rowId, Task<PluginTransitionResult> result) : IPluginTransition
        {
            public string RowId => rowId;
            public long Revision => 1;
            public bool IsCompleted => result.IsCompleted;
            public ValueTask<PluginTransitionResult> WaitAsync(CancellationToken cancellationToken = default) => new(result.WaitAsync(cancellationToken));
        }
    }

    private sealed class FakeConnection(long id) : IConnection
    {
        private readonly Pipe _pipe = new();
        internal string? Reason;
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => _pipe.Reader;
        public PipeWriter Output => _pipe.Writer;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => Reason is not null;
        public ValueTask CloseAsync(string reason) { Reason ??= reason; return ValueTask.CompletedTask; }
    }
}
