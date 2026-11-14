using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Signals;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>
/// Shares this node's plaza with the other nodes of its account domain through the directory: local members are
/// published as they enter, move and leave, and the other nodes' members are shown in each build's plaza here.
/// </summary>
[Plugin("cluster.plaza-federation", Category = "cluster", Names = ["en=Plaza federation", "zh-Hans=广场联合", "zh-Hant=廣場聯合", "ko=광장 연합"], Description = "Plaza members shared across nodes of one account domain.",
    Requires = [typeof(INodeLeaseSource)], OptionalMany = [typeof(IPlazaFederationEndpoint)])]
public sealed class PlazaFederationPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<PlazaFederationOptions>();
        var lease = context.Services.Get<INodeLeaseSource>();
        if (lease.NodeId != options.NodeId || new Uri(lease.DirectoryUrl) != new Uri(options.DirectoryUrl))
            throw new ConfigException("The plaza federation and heartbeat row must name the same node and directory.");
        var endpoints = context.Services.GetAll<IPlazaFederationEndpoint>().Where(endpoint => options.Builds.Contains(endpoint.ClientBuild)).ToArray();
        var missing = options.Builds.Where(build => endpoints.Count(endpoint => endpoint.ClientBuild == build) != 1).ToArray();
        if (missing.Length > 0)
            throw new ConfigException("Plaza federation needs exactly one plaza per configured build; check " + string.Join(", ", missing) + ".");
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 2 })
            { BaseAddress = new(options.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan };
        var client = new PlazaFederationClient(options, lease, http, endpoints, context.Logger);
        context.Effect(client);
        foreach (var endpoint in endpoints) context.Effect(endpoint.Bind(client.LinkFor(endpoint.ClientBuild)));
        await context.RunTaskAsync("plaza-federation-send", client.SendLoopAsync, cancellationToken).ConfigureAwait(false);
        await context.RunTaskAsync("plaza-federation-receive", client.ReceiveLoopAsync, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Configures the node's plaza link to the directory.</summary>
public sealed class PlazaFederationOptions : IValidatableOptions
{
    /// <summary>Gets or sets the identity also used by this node's heartbeat row.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets the directory HTTP origin; redirects are never followed.</summary>
    public string DirectoryUrl { get; set; } = "";
    /// <summary>Gets or sets the node's private directory key, resolved from deployment secrets.</summary>
    public string AccessKey { get; set; } = "";
    /// <summary>Gets or sets the client builds whose plaza is shared; each needs its rooms row on this node.</summary>
    public string[] Builds { get; set; } = [];
    /// <summary>Gets or sets how often queued local changes are sent, in milliseconds.</summary>
    [Range(20, 2000)] public int FlushIntervalMs { get; set; } = 100;
    /// <summary>Gets or sets the queued local changes; positions are coalesced per player and dropped first when full.</summary>
    [Range(16, 65536)] public int MaxQueuedEvents { get; set; } = 4096;
    /// <summary>Gets or sets the directory long-poll duration, in seconds.</summary>
    [Range(1, DirectoryServer.PlazaMaxWaitSeconds)] public int PollWaitSeconds { get; set; } = 20;
    /// <summary>Gets or sets the full HTTP deadline, including response buffering, in milliseconds.</summary>
    [Range(1000, 90000)] public int HttpTimeoutMs { get; set; } = 30000;
    /// <summary>Gets or sets the delay before retrying a failed request, in milliseconds.</summary>
    [Range(100, 30000)] public int RetryDelayMs { get; set; } = 1000;
    /// <summary>Gets or sets the maximum buffered response size.</summary>
    [Range(1024, 4194304)] public int MaxBodyBytes { get; set; } = 1048576;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        var common = new RoomDirectoryClientOptions { NodeId = NodeId, DirectoryUrl = DirectoryUrl, AccessKey = AccessKey, Builds = Builds };
        foreach (var result in common.Validate()) yield return result;
        if (HttpTimeoutMs <= PollWaitSeconds * 1000)
            yield return new("http_timeout_ms must exceed the plaza long-poll wait.", [nameof(HttpTimeoutMs)]);
    }
}

internal sealed class PlazaFederationClient(PlazaFederationOptions options, INodeLeaseSource leases, HttpClient http,
    IReadOnlyList<IPlazaFederationEndpoint> endpoints, ILogger logger) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<PlazaWireEvent> _queue = [];
    // The local members as last published, replayed whenever the directory may have lost them.
    private readonly Dictionary<(string Build, int User), (PlazaWireEvent Entered, PlazaWireEvent? Moved)> _local = [];
    private readonly CancellationTokenSource _stop = new();
    // Present while the queue holds changes or a resend is due; the send loop sleeps on it.
    private readonly PresenceSignal _pending = new();
    private string? _sentToken;
    private string _hub = "";
    private long _cursor;
    private bool _republish;
    private bool _queueFullReported;
    private int _disposed;

    internal IPlazaFederationLink LinkFor(string build) => new Link(this, build);

    private sealed class Link(PlazaFederationClient client, string build) : IPlazaFederationLink
    {
        public void Publish(PlazaFederationEvent change) => client.Enqueue(PlazaTransport.ToWire(build, change));
    }

    private void Enqueue(PlazaWireEvent change)
    {
        if (!PlazaTransport.Valid(change))
        {
            logger.LogWarning("Plaza change for player {User} on {Build} is not shareable and stays local.", change.User, change.Build);
            return;
        }
        lock (_gate)
        {
            var key = (change.Build, change.User);
            switch (change.Kind)
            {
                case PlazaFederationEventKind.Entered: _local[key] = (change, _local.GetValueOrDefault(key).Moved); break;
                case PlazaFederationEventKind.Moved when _local.TryGetValue(key, out var known): _local[key] = (known.Entered, change); break;
                case PlazaFederationEventKind.Left: _local.Remove(key); break;
            }
            // Only the newest position of a player matters to the others.
            if (change.Kind == PlazaFederationEventKind.Moved)
                _queue.RemoveAll(queued => queued.Kind == PlazaFederationEventKind.Moved && queued.Build == change.Build && queued.User == change.User);
            if (_queue.Count >= options.MaxQueuedEvents)
            {
                var position = _queue.FindIndex(queued => queued.Kind == PlazaFederationEventKind.Moved);
                if (position >= 0) _queue.RemoveAt(position);
                else
                {
                    // Entries and leaves are never dropped silently: the next send replays the whole local plaza.
                    _queue.Clear();
                    _republish = true;
                    if (!_queueFullReported) logger.LogWarning("The plaza federation queue is full; the local plaza will be resent.");
                    _queueFullReported = true;
                    RefreshPending();
                    return;
                }
            }
            _queue.Add(change);
            RefreshPending();
        }
    }

    // Called with _gate held after every change to the queue or the resend flag.
    private void RefreshPending() => _pending.Refresh(() => _queue.Count != 0 || _republish);

    // Complete while the send loop has something to do: a lease whose token has not been sent to, queued changes or
    // a pending resend. Pending otherwise, until a change is queued or the lease becomes a different one. Both
    // halves are levels read after the state they describe, so nothing that happens before the wait is missed; a
    // lease source that cannot report changes returns a completed task and the loop keeps its period.
    private Task WhenWork()
    {
        var lease = leases.Current;
        if (lease is null) return leases.WhenChangedFrom(null);
        lock (_gate)
            if (_sentToken != lease.Token || _pending.IsPresent) return Task.CompletedTask;
        var changed = leases.WhenChangedFrom(lease);
        return changed.IsCompleted ? changed : Task.WhenAny(_pending.WhenPresent, changed);
    }

    internal async ValueTask SendLoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var period = TimeSpan.FromMilliseconds(options.FlushIntervalMs);
        PeriodicTimer? timer = null;
        try
        {
            while (true)
            {
                var work = WhenWork();
                if (!work.IsCompleted)
                {
                    // A periodic timer fires whether or not anyone waits on it, so it is released, not just ignored.
                    timer?.Dispose();
                    timer = null;
                    await work.WaitAsync(lifetime.Token).ConfigureAwait(false);
                }
                timer ??= new PeriodicTimer(period);
                if (!await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)) return;
                if (leases.Current is not { } lease) continue;
                PlazaWireEvent[] batch;
                lock (_gate)
                {
                    // A new lease or a restarted directory dropped this node's members; send them all again first.
                    if (_republish || _sentToken != lease.Token)
                    {
                        _queue.Clear();
                        foreach (var (entered, moved) in _local.Values)
                        {
                            _queue.Add(entered);
                            if (moved is not null) _queue.Add(moved);
                        }
                        _republish = false;
                        _queueFullReported = false;
                    }
                    if (_queue.Count == 0 && _sentToken == lease.Token)
                    {
                        RefreshPending();
                        continue;
                    }
                    batch = [.. _queue];
                    _queue.Clear();
                    RefreshPending();
                }
                try
                {
                    await SendAsync(HttpMethod.Post, "", new PlazaWirePost(batch), lifetime.Token).ConfigureAwait(false);
                    lock (_gate) _sentToken = lease.Token;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
                    or OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _republish = true;
                        RefreshPending();
                    }
                    logger.LogWarning("Plaza changes could not reach the directory ({FailureType}); they will be resent.", error.GetType().Name);
                    await Task.Delay(options.RetryDelayMs, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            timer?.Dispose();
        }
    }

    internal async ValueTask ReceiveLoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    if (leases.Current is null)
                    {
                        await Task.Delay(options.RetryDelayMs, lifetime.Token).ConfigureAwait(false);
                        continue;
                    }
                    var path = "?hub=" + Uri.EscapeDataString(_hub) + "&after=" + _cursor.ToString(CultureInfo.InvariantCulture)
                        + "&wait=" + options.PollWaitSeconds.ToString(CultureInfo.InvariantCulture);
                    var bytes = await SendAsync(HttpMethod.Get, path, null, lifetime.Token).ConfigureAwait(false);
                    var batch = bytes is null ? null : JsonSerializer.Deserialize<PlazaWireBatch>(bytes, PlazaTransport.Json);
                    if (batch is null || batch.Hub.Length is 0 or > 128 || batch.Cursor < 0 || batch.Events is null
                        || !batch.Events.All(PlazaTransport.Valid))
                        throw new InvalidDataException("The directory returned an invalid plaza batch.");
                    if (batch.Hub != _hub && _hub.Length > 0)
                        lock (_gate)
                        {
                            _republish = true;
                            RefreshPending();
                        }
                    _hub = batch.Hub;
                    _cursor = batch.Cursor;
                    await ApplyAsync(batch, lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException
                    or OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    logger.LogWarning("Remote plaza changes could not be read ({FailureType}); retrying.", error.GetType().Name);
                    // Whatever is shown may be stale after a gap: the next read starts from a snapshot.
                    _hub = "";
                    await Task.Delay(options.RetryDelayMs, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async ValueTask ApplyAsync(PlazaWireBatch batch, CancellationToken ct)
    {
        foreach (var endpoint in endpoints)
        {
            var changes = batch.Events.Where(change => change.Build == endpoint.ClientBuild).Select(PlazaTransport.FromWire).ToArray();
            if (!batch.Snapshot && changes.Length == 0) continue;
            try { await endpoint.ApplyAsync(new(batch.Snapshot, changes), ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(error, "Remote plaza changes for {Build} could not be applied.", endpoint.ClientBuild);
            }
        }
    }

    private async ValueTask<byte[]?> SendAsync(HttpMethod method, string query, object? body, CancellationToken ct)
    {
        var lease = leases.Current ?? throw new HttpRequestException("The node has no current heartbeat lease.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.HttpTimeoutMs);
        using var request = new HttpRequestMessage(method, "/v1/nodes/" + Uri.EscapeDataString(options.NodeId) + "/plaza" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessKey);
        request.Headers.Add(MigrationTransport.LeaseHeader, lease.Token);
        request.Headers.Add(MigrationTransport.EpochHeader, lease.Epoch.ToString(CultureInfo.InvariantCulture));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, PlazaTransport.Json));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new HttpRequestException("The directory does not share this node's plaza (lease or account domain not verified).");
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidDataException("The directory did not return JSON.");
        await response.Content.LoadIntoBufferAsync(options.MaxBodyBytes, deadline.Token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        http.Dispose();
    }
}
