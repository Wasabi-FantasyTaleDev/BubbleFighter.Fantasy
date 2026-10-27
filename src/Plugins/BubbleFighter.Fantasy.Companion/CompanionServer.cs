using System.Collections.Frozen;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// The companion listener. A launcher upgrades <c>GET /companion/v1</c> with the <c>fantasytale.companion.v1</c>
/// subprotocol and its one-time ticket as <c>Authorization: Bearer</c>, sends <c>companion.hello</c>, and receives
/// <c>companion.welcome</c> with the connection's epoch, the binding state and the negotiated capabilities.
/// </summary>
internal sealed class CompanionServer : IAsyncDisposable
{
    internal const string Path = "/companion/v1";
    private static readonly FrozenSet<string> Locales = FrozenSet.Create(StringComparer.Ordinal, "zh-CN", "zh-TW", "en", "ko", "id", "vi");
    private readonly WebApplication _app;
    private readonly CompanionOptions _options;
    private readonly CompanionLaunches _launches;
    private readonly CompanionHub _hub;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _json;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _connections;

    private CompanionServer(WebApplication app, CompanionOptions options, CompanionLaunches launches, CompanionHub hub, ILogger logger)
    {
        _app = app; _options = options; _launches = launches; _hub = hub; _logger = logger;
        _json = CompanionWire.Options(options.JsonDepth);
        _connections = new SemaphoreSlim(options.MaxConnections);
    }

    /// <summary>Gets the bound addresses, including the port chosen when listen uses zero.</summary>
    internal IReadOnlyCollection<string> Addresses => _app.Urls.ToArray();

    internal static async Task<CompanionServer> StartAsync(CompanionOptions options, CompanionLaunches launches, CompanionHub hub, ILogger logger, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPEndPoint.Parse(options.Listen));
            k.Limits.MaxConcurrentConnections = options.MaxConnections;
            k.Limits.MaxConcurrentUpgradedConnections = options.MaxConnections;
            k.Limits.MaxRequestBodySize = 0;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.HandshakeSeconds);
        });
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(options.ShutdownSeconds));
        var app = builder.Build();
        var server = new CompanionServer(app, options, launches, hub, logger);
        // The launcher runs its own heartbeat, so the transport's ping is off.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = Timeout.InfiniteTimeSpan });
        app.Map(Path, server.AcceptAsync);
        await app.StartAsync(ct).ConfigureAwait(false);
        return server;
    }

    private async Task AcceptAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        // Check the subprotocol before the ticket, so a mismatched launcher keeps its ticket for a supported endpoint.
        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(CompanionWire.Subprotocol))
        { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; return; }
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || _launches.Redeem(header["Bearer ".Length..]) is not { } launch)
        { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        if (!_connections.Wait(0)) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        try
        {
            using var transport = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { SubProtocol = CompanionWire.Subprotocol }).ConfigureAwait(false);
            var socket = new CompanionSocket(transport, _options, _json, _hub, _launches, _logger);
            byte[] welcome;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, context.RequestAborted))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(_options.HandshakeSeconds));
                var refusal = "companion.invalid-hello";
                try
                {
                    var message = await CompanionWire.ReceiveAsync(transport, _options, _json, handshake.Token).ConfigureAwait(false);
                    var hello = message is { Kind: "hello", Type: "companion.hello", Version: 1, Epoch: null, OperationId: null }
                        ? message.Payload.Deserialize<Hello>(_json) : null;
                    if (hello is null || hello.Protocol != 1 || !CompanionWire.ValidName(hello.Game) || !Locales.Contains(hello.Locale)
                        || hello.Capabilities.Count > 1024 || hello.Capabilities.Any(c => !CompanionWire.ValidName(c.Key) || c.Value is < 1 or > 65535))
                        throw new InvalidDataException(refusal);
                    if (hello.Build != launch.Build || hello.LaunchId != launch.Id) throw new InvalidDataException(refusal = "companion.identity-mismatch");
                    var capabilities = _hub.Negotiate(hello.Capabilities);
                    socket.Negotiate(capabilities.ToFrozenDictionary(StringComparer.Ordinal), hello.Locale);
                    if (_launches.Attach(launch, socket) is not { } bound) throw new InvalidDataException(refusal = "companion.launch-ended");
                    welcome = CompanionWire.Encode(new("welcome", CompanionWire.NewId(), "companion.welcome", 1, CompanionWire.Payload(new
                    {
                        Protocol = 1, launch.Build, LaunchId = launch.Id, State = bound ? "connected" : "pending_game", Capabilities = capabilities,
                    }, _json), Epoch: socket.Epoch), _json, _options.MessageBytes);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or OperationCanceledException or WebSocketException or IOException)
                {
                    var reason = error is InvalidDataException { Message: var code } && code.StartsWith("companion.", StringComparison.Ordinal) ? code
                        : error is OperationCanceledException ? "companion.handshake-timeout" : refusal;
                    _logger.LogInformation("Companion handshake for launch {LaunchId} refused: {Reason}.", launch.Id, reason);
                    _launches.Detach(socket);
                    await RefuseAsync(transport, reason).ConfigureAwait(false);
                    return;
                }
            }
            _logger.LogDebug("Companion connection opened for launch {LaunchId}.", launch.Id);
            await socket.RunAsync(welcome, _stopping.Token).ConfigureAwait(false);
        }
        finally { _connections.Release(); }
    }

    private static async Task RefuseAsync(WebSocket transport, string reason)
    {
        if (transport.State != WebSocketState.Open) { transport.Abort(); return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await transport.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { transport.Abort(); }
    }

    /// <summary>Ends every launch and connection, then stops the listener.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var socket in _launches.CloseAll()) socket.Close("companion.server-stopping");
        await _stopping.CancelAsync().ConfigureAwait(false);
        try { await _app.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }
    }

    /// <summary>The launcher's <c>companion.hello</c> payload.</summary>
    private sealed record Hello(int Protocol, string Game, string Build, string LaunchId, string Locale, Dictionary<string, int> Capabilities);
}
