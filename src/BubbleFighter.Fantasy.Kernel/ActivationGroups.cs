using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Runtime;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Options of the row that wakes and puts to sleep on-demand activation groups.</summary>
public sealed class ActivationGroupOptions
{
    /// <summary>Gets or sets how many minutes a group stays loaded after its last connection closed.</summary>
    [Range(1, 1440)]
    public int IdleMinutes { get; set; } = 30;

    /// <summary>Gets or sets how many seconds the connection that wakes a group is held before it is closed.</summary>
    [Range(1, 300)]
    public int WakeTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Wakes an on-demand activation group when one of its always-on listeners accepts a connection, and unloads it
/// again once no connection has used it for the configured time. Appears as row <c>core.activation-groups</c>;
/// without on-demand rows it only counts connections.
/// </summary>
/// <remarks>
/// The connection that finds its group asleep is held inside the listener's accepted event, which the listener
/// awaits before it resolves the protocol, so the protocol exists when the listener asks for it. A handler that
/// a woken row registers for that event does not see the connection that woke it.
/// </remarks>
[Plugin("core.activation-groups", Category = "core", Names = ["en=On-demand build loading", "zh-Hans=按需加载版本", "zh-Hant=按需載入版本", "ko=필요 시 빌드 로드"], Description = "Loads on-demand row groups for their first connection and unloads idle ones.",
    Requires = [typeof(IPluginHost)])]
public sealed class ActivationGroupsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ActivationGroupOptions>();
        var host = context.Services.Get<IPluginHost>();

        // Asked through the event bus, not the service registry: a registry view is fixed when a row activates, and
        // the rows that answer come and go with their group.
        var groups = new ActivationGroups(host,
            group => context.Events.Bail<ActivationGroupIdleQuery, string>(new(group)),
            TimeSpan.FromMinutes(options.IdleMinutes), TimeSpan.FromSeconds(options.WakeTimeoutSeconds), context.Logger, context.Lifetime);
        context.Effect(groups);
        // Ahead of every other handler: they may expect the group's services.
        context.Effect(context.Events.OnAsync<ConnectionAcceptedEvent>(groups.AcceptedAsync, priority: int.MaxValue));
        context.Effect(context.Events.On<ConnectionClosedEvent>(groups.Closed));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Connection counts, wake and sleep of the activation groups of one host.</summary>
internal sealed class ActivationGroups(IPluginHost host, Func<string, string?> inUse, TimeSpan idle, TimeSpan wakeTimeout,
    ILogger logger, CancellationToken lifetime, Action? compact = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (string Listener, Group Group)> _connections = [];
    private readonly Action _compact = compact ?? Compact;
    private bool _disposed;

    private sealed class Group(string name)
    {
        internal string Name { get; } = name;
        internal int Connections;
        // One wake or sleep at a time; a later one starts from what the earlier one left.
        internal Task Work = Task.CompletedTask;
        internal CancellationTokenSource? Idle;
    }

    /// <summary>Counts the connection and, when its group sleeps, holds it until the group is loaded.</summary>
    internal async ValueTask AcceptedAsync(ConnectionAcceptedEvent accepted, CancellationToken ct)
    {
        var name = host.Instances.FirstOrDefault(row => row.RowId == accepted.ListenerId)?.ActivationGroup;
        // A group whose rows all start with the server needs neither counting nor waking.
        if (name is null || Members(name).Count == 0) return;
        Task wake;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_groups.TryGetValue(name, out var group)) _groups[name] = group = new(name);
            _connections[accepted.Connection.Id] = (accepted.ListenerId, group);
            group.Connections++;
            group.Idle?.Cancel();
            group.Idle = null;
            wake = group.Work = Chain(group.Work, () => WakeAsync(name));
        }
        try
        {
            await wake.WaitAsync(wakeTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(error, "Group '{Group}' was not loaded within {Seconds} s for connection {Id} from {Remote}; closing it.",
                name, (int)wakeTimeout.TotalSeconds, accepted.Connection.Id, accepted.Connection.RemoteEndPoint);
            await accepted.Connection.CloseAsync("server is still loading this client version").ConfigureAwait(false);
        }
    }

    /// <summary>Forgets the connection; the last one of a group starts its idle time.</summary>
    internal void Closed(ConnectionClosedEvent closed)
    {
        lock (_gate)
        {
            if (_disposed || !_connections.Remove(closed.Connection.Id, out var entry)) return;
            var group = entry.Group;
            if (--group.Connections > 0) return;
            ArmIdle(group);
        }
    }

    private void ArmIdle(Group group)
    {
        // One delay per idle period, cancelled by the next connection: nothing ticks while a group is in use or asleep.
        group.Idle?.Cancel();
        var source = group.Idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _ = Detached(async () =>
        {
            var elapsed = true;
            try { await Task.Delay(idle, source.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { elapsed = false; }
            Task work;
            lock (_gate)
            {
                // Whoever cancelled it has already dropped the reference, so nothing cancels a disposed source.
                var current = ReferenceEquals(group.Idle, source);
                if (current) group.Idle = null;
                source.Dispose();
                if (!elapsed || _disposed || !current) return;
                work = group.Work = Chain(group.Work, () => SleepAsync(group));
            }
            try { await work.ConfigureAwait(false); }
            catch (Exception error) { logger.LogError(error, "Group '{Group}' could not be unloaded after its idle time.", group.Name); }
        });
    }

    // A row its configuration switches off stays off: asking for it with the group would switch it on.
    private IReadOnlyList<PluginInstanceInfo> Members(string name) =>
        host.Instances.Where(row => row.ActivationGroup == name && row.Activation == PluginActivation.OnDemand && !row.IsDisabled && !row.IsRetiring).ToArray();

    private async Task WakeAsync(string name)
    {
        var asleep = Members(name).Where(row => row.State != PluginState.Active).ToArray();
        if (asleep.Length == 0) return;
        var watch = Stopwatch.StartNew();
        logger.LogInformation("Group '{Group}' is loading {Count} row(s) for its first connection.", name, asleep.Length);
        // One request for the whole group: the host starts the rows in provider order, as it does at server start.
        await host.RequestStates(asleep.Select(row => row.RowId).ToArray(), enabled: true).WaitAsync(lifetime).ConfigureAwait(false);
        // A row that waits for something outside the group is started by the host when that arrives.
        while (Members(name).Where(row => row.State != PluginState.Active).ToArray() is { Length: > 0 } waiting)
        {
            if (waiting.FirstOrDefault(row => row.State is not (PluginState.Pending or PluginState.Unloaded)) is { } failed)
                throw new InvalidOperationException($"Row '{failed.RowId}' of group '{name}' is {failed.State}: {failed.Error ?? "no diagnostic"}.");
            if (watch.Elapsed > wakeTimeout)
                throw new TimeoutException($"Group '{name}' still waits for {string.Join(", ", waiting.Select(row => $"{row.RowId} ({row.Error ?? row.State.ToString()})"))}.");
            await Task.Delay(25, lifetime).ConfigureAwait(false);
        }
        logger.LogInformation("Group '{Group}' is loaded ({Milliseconds} ms).", name, watch.ElapsedMilliseconds);
        // Loading parses the group's data; what that left behind goes back before the group settles in.
        _compact();
    }

    private async Task SleepAsync(Group group)
    {
        lock (_gate)
            if (group.Connections > 0) return;
        var members = Members(group.Name).Where(row => row.State != PluginState.Unloaded).ToArray();
        if (members.Length == 0) return;
        // Zero connections is not proof that nothing is left: a row that still holds live state says so, and the group stays.
        if (inUse(group.Name) is { } held)
        {
            logger.LogWarning("Group '{Group}' has no connection but stays loaded: {Reason}.", group.Name, held);
            lock (_gate)
                if (!_disposed && group.Connections == 0) ArmIdle(group);
            return;
        }
        logger.LogInformation("Group '{Group}' had no connection for {Minutes} min; unloading {Count} row(s).",
            group.Name, (int)idle.TotalMinutes, members.Length);
        await host.RequestStates(members.Select(row => row.RowId).ToArray(), enabled: false).WaitAsync(lifetime).ConfigureAwait(false);
        var left = Members(group.Name).Where(row => row.State != PluginState.Unloaded).ToArray();
        // A connection that arrived meanwhile queued its wake behind this; rows it already brought back are not a fault.
        bool wanted;
        lock (_gate) wanted = group.Connections > 0;
        if (left.Length > 0 && !wanted)
            throw new InvalidOperationException($"Group '{group.Name}' did not unload cleanly: " +
                string.Join(", ", left.Select(row => $"{row.RowId} is {row.State} ({row.Error ?? "no diagnostic"})")) + ".");
        _compact();
        logger.LogInformation("Group '{Group}' is asleep.", group.Name);
    }

    /// <summary>Runs <paramref name="next"/> after <paramref name="previous"/> whatever its result, outside the caller's callback.</summary>
    private static Task Chain(Task previous, Func<Task> next) => Detached(async () =>
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception) { /* reported by whoever awaited it */ }
        await next().ConfigureAwait(false);
    });

    // The host refuses a wait for a transition from inside an owned callback, and the accepted event is one:
    // the work runs on its own task, without the caller's execution context.
    private static Task Detached(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);
        using (ExecutionContext.SuppressFlow()) return Task.Run(work);
    }

    private static void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var group in _groups.Values)
            {
                group.Idle?.Cancel();
                group.Idle = null;
            }
            _connections.Clear();
        }
    }
}
