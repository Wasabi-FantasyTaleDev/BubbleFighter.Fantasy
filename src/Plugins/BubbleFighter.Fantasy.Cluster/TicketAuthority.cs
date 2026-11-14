using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Companion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed class TicketAuthority(WebApplication app, TicketAuthorityOptions options) : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal IReadOnlyCollection<string> Addresses => app.Urls.ToArray();

    internal static async ValueTask<TicketAuthority> StartAsync(TicketAuthorityOptions options, ILauncherTicketService tickets, CancellationToken ct,
        ICompanionFederation? companion = null, ILogger? logger = null)
    {
        var peers = options.Peers.ToDictionary(peer => peer.NodeId, StringComparer.Ordinal);
        var keys = options.Peers.ToDictionary(peer => peer.NodeId, peer => SHA256.HashData(Encoding.UTF8.GetBytes(peer.Key)), StringComparer.Ordinal);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPEndPoint.Parse(options.Listen));
            server.Limits.MaxConcurrentConnections = options.MaxConnections;
            server.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            context.RequestAborted = deadline.Token;
            try { await next(context).ConfigureAwait(false); }
            catch (JsonException) { context.Response.StatusCode = 400; }
            catch (BadHttpRequestException error) { context.Response.StatusCode = error.StatusCode; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { if (!context.Response.HasStarted) context.Response.StatusCode = 408; }
        });
        app.MapPost("/v1/nodes/{node}/tickets/redeem", async (HttpContext context, string node) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.Length > 263 || !header.StartsWith("Bearer ", StringComparison.Ordinal) || !keys.TryGetValue(node, out var expected)
                || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
                return Results.Unauthorized();
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            var request = await context.Request.ReadFromJsonAsync<TicketRedemption>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
            if (request is null || !TicketValidation.Token(request.Ticket) || !TicketValidation.Id(request.Build)) return Results.BadRequest();
            if (!peers[node].Builds.Contains(request.Build, StringComparer.Ordinal)) return Results.StatusCode(403);
            var identity = tickets is ILauncherNodeTicketService nodeTickets
                ? await nodeTickets.RedeemForNodeAsync(request.Ticket, request.Build, node, context.RequestAborted).ConfigureAwait(false)
                : await tickets.RedeemIdentityAsync(request.Ticket, request.Build, context.RequestAborted).ConfigureAwait(false);
            if (identity?.LaunchId is { } launchId && peers[node].CompanionUrl.Length > 0)
            {
                if (companion is null) throw new InvalidOperationException("Remote companion routing requires the companion row.");
                try
                {
                    using var preparation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    preparation.CancelAfter(options.CompanionTimeoutMilliseconds);
                    if (!await companion.RedirectAsync(identity.Username, request.Build, launchId, node,
                        new Uri(peers[node].CompanionUrl), peers[node].Key, preparation.Token).ConfigureAwait(false))
                        throw new InvalidDataException("The authenticated launch cannot be redirected to this node.");
                }
                catch (Exception error) when (error is HttpRequestException or IOException
                    || error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
                {
                    logger?.LogWarning("Companion preparation for node {Node} failed ({FailureType}); game authentication continues without it.", node, error.GetType().Name);
                    identity = identity with { LaunchId = null };
                }
            }
            return identity is null ? Results.Unauthorized() : Results.Json(identity);
        });
        try { await app.StartAsync(ct).ConfigureAwait(false); return new(app, options); }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try { await app.StopAsync(deadline.Token).ConfigureAwait(false); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }
}

internal sealed record TicketRedemption(string Ticket, string Build);

/// <summary>Exposes an existing local ticket authority only to explicitly trusted worker nodes.</summary>
[Plugin("cluster.ticket-authority", Category = "cluster", Names = ["en=Cluster ticket authority", "zh-Hans=集群票据签发", "zh-Hant=叢集票證簽發", "ko=클러스터 티켓 발급"], Description = "Private one-use ticket redemption for trusted workers.",
    Requires = [typeof(ILauncherTicketService)], Optional = [typeof(ICompanionFederation)])]
public sealed class TicketAuthorityPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<TicketAuthorityOptions>();
        var tickets = context.Services.Get<ILauncherTicketService>();
        context.Services.TryGet<ICompanionFederation>(out var companion);
        if (options.Peers.Any(peer => peer.CompanionUrl.Length > 0) && companion is null)
            throw new ConfigException("Remote companion routing requires the companion row.");
        await context.AcquireEffectAsync(async ct => await TicketAuthority.StartAsync(options, tickets, ct, companion, context.Logger).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }
}
