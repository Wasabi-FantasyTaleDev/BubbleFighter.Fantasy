using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Signals;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>
/// Relays clan notices between the nodes of one account domain through the directory: a notice for members who are
/// not connected here is published, and the other nodes' notices are told to the members connected here.
/// </summary>
[Plugin("cluster.clan-federation", Category = "cluster", Names = ["en=Clan relay", "zh-Hans=公会中继", "zh-Hant=公會中繼", "ko=클랜 중계"], Description = "Clan notices relayed to members connected to other nodes of one account domain.",
    Requires = [typeof(INodeLeaseSource), typeof(IClanRelayEndpoint)])]
public sealed class ClanFederationPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ClanFederationOptions>();
        var lease = context.Services.Get<INodeLeaseSource>();
        if (lease.NodeId != options.NodeId || new Uri(lease.DirectoryUrl) != new Uri(options.DirectoryUrl))
            throw new ConfigException("The clan relay and heartbeat row must name the same node and directory.");
        var endpoint = context.Services.Get<IClanRelayEndpoint>();
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 2 })
            { BaseAddress = new(options.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan };
        var client = new ClanFederationClient(options, lease, http, endpoint, context.Logger);
        context.Effect(client);
        context.Effect(endpoint.Bind(client));
        await context.RunTaskAsync("clan-federation-send", client.SendLoopAsync, cancellationToken).ConfigureAwait(false);
        await context.RunTaskAsync("clan-federation-receive", client.ReceiveLoopAsync, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Configures the node's clan relay link to the directory.</summary>
public sealed class ClanFederationOptions : IValidatableOptions
{
    /// <summary>Gets or sets the identity also used by this node's heartbeat row.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets the directory HTTP origin; redirects are never followed.</summary>
    public string DirectoryUrl { get; set; } = "";
    /// <summary>Gets or sets the node's private directory key, resolved from deployment secrets.</summary>
    public string AccessKey { get; set; } = "";
    /// <summary>Gets or sets how often queued notices are sent, in milliseconds.</summary>
    [Range(20, 2000)] public int FlushIntervalMs { get; set; } = 100;
    /// <summary>Gets or sets the queued notices; the oldest is dropped when the queue is full.</summary>
    [Range(16, 65536)] public int MaxQueuedNotices { get; set; } = 4096;
    /// <summary>Gets or sets the directory long-poll duration, in seconds.</summary>
    [Range(1, DirectoryServer.ClanMaxWaitSeconds)] public int PollWaitSeconds { get; set; } = 20;
    /// <summary>Gets or sets the full HTTP deadline, including response buffering, in milliseconds.</summary>
    [Range(1000, 90000)] public int HttpTimeoutMs { get; set; } = 30000;
    /// <summary>Gets or sets the delay before retrying a failed request, in milliseconds.</summary>
    [Range(100, 30000)] public int RetryDelayMs { get; set; } = 1000;
    /// <summary>Gets or sets the maximum buffered response size.</summary>
    [Range(1024, 16777216)] public int MaxBodyBytes { get; set; } = 4194304;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        // The relay names no build: the clan is one directory for every build.
        var common = new RoomDirectoryClientOptions { NodeId = NodeId, DirectoryUrl = DirectoryUrl, AccessKey = AccessKey, Builds = ["clan"] };
        foreach (var result in common.Validate()) yield return result;
        if (HttpTimeoutMs <= PollWaitSeconds * 1000)
            yield return new("http_timeout_ms must exceed the clan long-poll wait.", [nameof(HttpTimeoutMs)]);
    }
}

internal sealed class ClanFederationClient(ClanFederationOptions options, INodeLeaseSource leases, HttpClient http,
    IClanRelayEndpoint endpoint, ILogger logger) : IClanRelayLink, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<ClanWireNotice> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    // Present while the queue holds notices; the send loop sleeps on it.
    private readonly PresenceSignal _pending = new();
    private string _hub = "";
    private long _cursor;
    private bool _queueFullReported;
    private int _disposed;

    /// <inheritdoc />
    public void Publish(ClanRelayNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var wire = ClanTransport.ToWire(notice);
        if (!ClanTransport.Valid(wire))
        {
            logger.LogWarning("A clan notice ({Kind}) exceeds the relay bounds and stays on this node.", notice.Kind);
            return;
        }
        lock (_gate)
        {
            if (_queue.Count >= options.MaxQueuedNotices)
            {
                // Clan notices are transient: the oldest goes, and its members read the directory on their next open.
                _queue.Dequeue();
                if (!_queueFullReported) logger.LogWarning("The clan relay queue is full; the oldest notices are dropped.");
                _queueFullReported = true;
            }
            _queue.Enqueue(wire);
            _pending.Refresh(() => _queue.Count != 0);
        }
    }

    internal async ValueTask SendLoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            while (true)
            {
                await _pending.WhenPresent.WaitAsync(lifetime.Token).ConfigureAwait(false);
                await Task.Delay(options.FlushIntervalMs, lifetime.Token).ConfigureAwait(false);
                if (leases.Current is null)
                {
                    await Task.Delay(options.RetryDelayMs, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                ClanWireNotice[] batch;
                lock (_gate)
                {
                    batch = [.. _queue];
                    _queue.Clear();
                    _queueFullReported = false;
                    _pending.Refresh(() => _queue.Count != 0);
                }
                if (batch.Length == 0) continue;
                try
                {
                    await SendAsync(HttpMethod.Post, "", new ClanWirePost(batch), lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
                    or OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    // Not resent: a late notice is worse than none, and the members read the directory on their next open.
                    logger.LogWarning("{Count} clan notices could not reach the directory ({FailureType}) and were dropped.",
                        batch.Length, error.GetType().Name);
                    await Task.Delay(options.RetryDelayMs, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
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
                    var batch = bytes is null ? null : JsonSerializer.Deserialize<ClanWireBatch>(bytes, ClanTransport.Json);
                    if (batch is null || batch.Hub.Length is 0 or > 128 || batch.Cursor < 0 || batch.Notices is null
                        || !batch.Notices.All(ClanTransport.Valid))
                        throw new InvalidDataException("The directory returned an invalid clan batch.");
                    _hub = batch.Hub;
                    _cursor = batch.Cursor;
                    if (batch.Notices.Length > 0)
                        await endpoint.DeliverAsync([.. batch.Notices.Select(ClanTransport.FromWire)], lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException
                    or OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    logger.LogWarning("Relayed clan notices could not be read ({FailureType}); retrying.", error.GetType().Name);
                    await Task.Delay(options.RetryDelayMs, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async ValueTask<byte[]?> SendAsync(HttpMethod method, string query, object? body, CancellationToken ct)
    {
        var lease = leases.Current ?? throw new HttpRequestException("The node has no current heartbeat lease.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.HttpTimeoutMs);
        using var request = new HttpRequestMessage(method, "/v1/nodes/" + Uri.EscapeDataString(options.NodeId) + "/clan" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessKey);
        request.Headers.Add(MigrationTransport.LeaseHeader, lease.Token);
        request.Headers.Add(MigrationTransport.EpochHeader, lease.Epoch.ToString(CultureInfo.InvariantCulture));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, ClanTransport.Json));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new HttpRequestException("The directory does not relay this node's clan notices (lease or account domain not verified).");
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
