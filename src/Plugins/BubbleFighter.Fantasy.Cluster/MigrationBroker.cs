using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Publishes migration RPC through the existing node lease and the authenticated private directory.</summary>
[Plugin("cluster.migration-broker", Description = "Bounded leased-node migration requests and destination inbox.",
    Requires = [typeof(INodeLeaseSource)], Provides = [typeof(IMigrationBroker), typeof(IMigrationTargetDirectory)])]
public sealed class MigrationBrokerPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MigrationBrokerOptions>();
        var lease = context.Services.Get<INodeLeaseSource>();
        if (lease.NodeId != options.NodeId || new Uri(lease.DirectoryUrl) != new Uri(options.DirectoryUrl))
            throw new ConfigException("The migration broker and heartbeat row must name the same node and directory.");
        var http = new HttpClient(new SocketsHttpHandler
        { AllowAutoRedirect = false, MaxConnectionsPerServer = options.MaxConcurrentRequests + 1 })
        { BaseAddress = new(options.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan };
        var broker = new MigrationBroker(options, lease, http, context.Logger);
        context.Effect(broker);
        await context.RunTaskAsync("migration-inbox", broker.RunAsync, cancellationToken).ConfigureAwait(false);
        context.Effect(context.Services.Register<IMigrationBroker>(broker));
        context.Effect(context.Services.Register<IMigrationTargetDirectory>(broker));
    }
}

internal sealed class MigrationBroker(MigrationBrokerOptions options, INodeLeaseSource leases, HttpClient http, ILogger logger)
    : IMigrationBroker, IMigrationTargetDirectory, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _handlerGate = new();
    private TaskCompletionSource<Handlers> _handler = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _disposed;
    private string Prefix => "/v1/nodes/" + Uri.EscapeDataString(options.NodeId) + "/migrations";

    public async ValueTask<MigrationTarget?> ResolveAsync(string nodeId, string clientBuild, CancellationToken cancellationToken)
    {
        if (!RoomDirectoryClientOptions.BackendId(nodeId) || !RoomDirectoryClientOptions.Identifier(clientBuild))
            throw new ArgumentException("Migration target lookup requires valid node and build identities.");
        Enter();
        try
        {
            var bytes = await SendAsync(HttpMethod.Get, Prefix + "/targets/" + Uri.EscapeDataString(nodeId)
                + "/" + Uri.EscapeDataString(clientBuild), null, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return null;
            var target = JsonSerializer.Deserialize<MigrationTarget>(bytes, MigrationTransport.Json);
            return target is not null && target.NodeId == nodeId && target.Epoch > 0 ? target
                : throw new InvalidDataException("The migration directory returned an invalid target.");
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    public async ValueTask<MigrationOffer> RequestAsync(MigrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MigrationTransport.Valid(request) || request.SourceNode != options.NodeId)
            throw new ArgumentException("The migration must identify this source and a complete destination intent.", nameof(request));
        Enter();
        try
        {
            var bytes = await SendAsync(HttpMethod.Post, Prefix, request, cancellationToken).ConfigureAwait(false);
            var offer = bytes is null ? null : JsonSerializer.Deserialize<MigrationOffer>(bytes, MigrationTransport.Json);
            return offer is not null && MigrationTransport.Valid(offer) ? offer
                : throw new InvalidDataException("The migration directory returned an invalid offer.");
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    public async ValueTask CancelAsync(string destinationNode, string migrationId, CancellationToken cancellationToken)
    {
        if (!RoomDirectoryClientOptions.BackendId(destinationNode) || !AccountHandoff.IsMigrationId(migrationId))
            throw new ArgumentException("Migration cancellation requires valid destination and migration identities.");
        Enter();
        try { await SendAsync(HttpMethod.Delete, Prefix + "/" + migrationId, new { }, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _active); }
    }

    public IDisposable Serve(Func<MigrationRequest, CancellationToken, ValueTask<MigrationOffer>> accept,
        Func<string, CancellationToken, ValueTask> cancel)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(cancel);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_handlerGate)
        {
            if (_handler.Task.IsCompleted)
                throw new InvalidOperationException("A migration broker can register only one destination receiver.");
            var handlers = new Handlers(this, accept, cancel);
            _handler.SetResult(handlers);
            return handlers;
        }
    }

    internal async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Task<Handlers> pending;
                lock (_handlerGate) pending = _handler.Task;
                var handlers = await pending.WaitAsync(lifetime.Token).ConfigureAwait(false);
                await ReceiveAsync(handlers, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async ValueTask ReceiveAsync(Handlers handlers, CancellationToken lifetime)
    {
        try
        {
            using var serving = CancellationTokenSource.CreateLinkedTokenSource(lifetime, handlers.Stopped.Token);
            var ct = serving.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var bytes = await SendAsync(HttpMethod.Get, Prefix + "/inbox?wait="
                        + options.PollWaitSeconds.ToString(CultureInfo.InvariantCulture), null, ct).ConfigureAwait(false);
                    if (bytes is null) continue;
                    var envelope = JsonSerializer.Deserialize<MigrationEnvelope>(bytes, MigrationTransport.Json);
                    if (envelope is null || !MigrationTransport.Valid(envelope.Request) || envelope.Request.DestinationNode != options.NodeId)
                        throw new InvalidDataException("The migration inbox returned an invalid destination request.");
                    using var callback = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    callback.CancelAfter(TimeSpan.FromMilliseconds(options.MigrationRequestTimeoutMs));
                    if (envelope.Cancel)
                    {
                        await handlers.Cancel(envelope.Request.MigrationId, callback.Token).ConfigureAwait(false);
                        continue;
                    }
                    MigrationOffer offer;
                    try
                    {
                        offer = await handlers.Accept(envelope.Request, callback.Token).ConfigureAwait(false);
                        callback.Token.ThrowIfCancellationRequested();
                        if (!MigrationTransport.Valid(offer)) throw new InvalidDataException("The destination returned an invalid offer.");
                    }
                    catch (Exception error)
                    {
                        logger.LogWarning("Migration {MigrationId} was refused by its destination callback ({FailureType}).",
                            envelope.Request.MigrationId, error.GetType().Name);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.MigrationRequestTimeoutMs));
                        await handlers.Cancel(envelope.Request.MigrationId, cleanup.Token).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        offer = new(null, 0, null, TimeSpan.Zero);
                    }
                    try
                    {
                        await SendAsync(HttpMethod.Post, Prefix + "/" + envelope.Request.MigrationId + "/offer", offer, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.MigrationRequestTimeoutMs));
                        await handlers.Cancel(envelope.Request.MigrationId, cleanup.Token).ConfigureAwait(false);
                        throw;
                    }
                }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
                {
                    ct.ThrowIfCancellationRequested();
                    logger.LogWarning("Migration inbox for {NodeId} failed ({FailureType}); retrying after the configured delay.",
                        options.NodeId, error.GetType().Name);
                    await Task.Delay(options.RetryDelayMs, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || handlers.Stopped.IsCancellationRequested) { }
    }

    private void Unregister(Handlers handlers)
    {
        lock (_handlerGate)
        {
            if (!_handler.Task.IsCompletedSuccessfully || !ReferenceEquals(_handler.Task.Result, handlers)) return;
            _handler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        handlers.Stopped.Cancel();
    }

    private async ValueTask<byte[]?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var lease = leases.Current ?? throw new InvalidOperationException("The migration node has no current heartbeat lease.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(options.HttpTimeoutMs));
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessKey);
        request.Headers.Add(MigrationTransport.LeaseHeader, lease.Token);
        request.Headers.Add(MigrationTransport.EpochHeader, lease.Epoch.ToString(CultureInfo.InvariantCulture));
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, MigrationTransport.Json);
            if (bytes.Length > options.MaxBodyBytes) throw new InvalidDataException("The migration exceeds max_body_bytes.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidDataException("The migration directory did not return JSON.");
        await response.Content.LoadIntoBufferAsync(options.MaxBodyBytes, deadline.Token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
    }

    private void Enter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Increment(ref _active) <= options.MaxConcurrentRequests) return;
        Interlocked.Decrement(ref _active);
        throw new InvalidOperationException("The configured migration request capacity is exhausted.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        http.Dispose();
    }

    private sealed class Handlers(MigrationBroker owner, Func<MigrationRequest, CancellationToken, ValueTask<MigrationOffer>> accept,
        Func<string, CancellationToken, ValueTask> cancel) : IDisposable
    {
        internal Func<MigrationRequest, CancellationToken, ValueTask<MigrationOffer>> Accept => accept;
        internal Func<string, CancellationToken, ValueTask> Cancel => cancel;
        internal CancellationTokenSource Stopped { get; } = new();
        public void Dispose() => owner.Unregister(this);
    }
}
