using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Launcher;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed partial class DirectoryServer(WebApplication app, DirectoryOptions options) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal IReadOnlyCollection<string> Addresses => app.Urls.ToArray();

    internal static async ValueTask<DirectoryServer> StartAsync(DirectoryOptions options, ServerDirectory directory, CancellationToken ct)
    {
        var keys = options.Nodes.ToDictionary(node => node.Id, node => SHA256.HashData(Encoding.UTF8.GetBytes(node.HeartbeatKey)), StringComparer.Ordinal);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPEndPoint.Parse(options.Listen));
            server.Limits.MaxConcurrentConnections = options.MaxConnections;
            server.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
            {
                if (!context.Request.HasJsonContentType())
                { context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType; return; }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
            if (options.Migrations && context.Request.Path.Value?.Contains("/migrations", StringComparison.Ordinal) == true)
                timeout += TimeSpan.FromMilliseconds(Math.Max(25000, options.MigrationRequestTimeoutMs));
            else if (options.PlazaFederation && context.Request.Path.Value?.EndsWith("/plaza", StringComparison.Ordinal) == true)
                timeout += TimeSpan.FromSeconds(PlazaMaxWaitSeconds);
            deadline.CancelAfter(timeout);
            context.RequestAborted = deadline.Token;
            try { await next(context).ConfigureAwait(false); }
            catch (JsonException) { context.Response.StatusCode = 400; }
            catch (BadHttpRequestException error) { context.Response.StatusCode = error.StatusCode; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { if (!context.Response.HasStarted) context.Response.StatusCode = 408; }
        });
        bool Authorized(HttpContext context, string node)
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.Length > 263 || !header.StartsWith("Bearer ", StringComparison.Ordinal) || !keys.TryGetValue(node, out var expected)) return false;
            return CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])));
        }
        // The selector projection is public discovery data. Node leases, room summaries and ticket redemption
        // remain authenticated below; the Gateway must be able to read this list before it has a node lease.
        app.MapGet("/v1/servers/{build}", (string build) =>
        {
            if (string.IsNullOrWhiteSpace(build) || build.Length > 64 || build.Any(char.IsControl))
                return Results.BadRequest(new { error = "invalid_build" });
            return Results.Json(new { schema = 1, build, generated_at_utc = DateTimeOffset.UtcNow, selectors = directory.ListServers(build) }, JsonOptions);
        });
        app.MapPost("/v1/nodes/{node}/register", async (HttpContext context, string node) =>
        {
            if (!Authorized(context, node)) return Results.Unauthorized();
            var registration = await context.Request.ReadFromJsonAsync<NodeRegistration>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
            if (registration is null || registration.Instance is null) return Results.BadRequest();
            var grant = directory.Register(node, registration.Instance);
            return grant is null ? Results.Conflict(new { error = "node_lease_busy" }) : Results.Json(grant);
        });
        app.MapPut("/v1/nodes/{node}/heartbeat", async (HttpContext context, string node) =>
        {
            if (!Authorized(context, node)) return Results.Unauthorized();
            var report = await context.Request.ReadFromJsonAsync<NodeHeartbeat>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
            if (report is null) return Results.BadRequest();
            return directory.Observe(node, report) ? Results.NoContent() : Results.Conflict(new { error = "invalid_or_stale_report" });
        });
        app.MapDelete("/v1/nodes/{node}/lease", async (HttpContext context, string node) =>
        {
            if (!Authorized(context, node)) return Results.Unauthorized();
            var release = await context.Request.ReadFromJsonAsync<NodeRelease>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
            return release is not null && directory.Release(node, release.Token) ? Results.NoContent() : Results.Conflict();
        });
        app.MapGet("/v1/nodes/{node}/rooms/{build}", (HttpContext context, string node, string build) =>
            !Authorized(context, node) ? Results.Unauthorized() : Results.Json(new { schema = 1, rooms = directory.ListRooms(node, build) }));
        app.MapGet("/v1/nodes/{node}/presence/{group}", async (HttpContext context, string node, string group) =>
        {
            if (!Authorized(context, node)) return Results.Unauthorized();
            if (!directory.CanReadPresence(node, group)) return Results.NotFound();
            var includeRoster = context.Request.Query["roster"].ToString() == "true";
            return Results.Json(await directory.ReadAsync(group, includeRoster, context.RequestAborted).ConfigureAwait(false));
        });
        if (options.RoomPreparationPermits)
        {
            app.MapPost("/v1/nodes/{node}/room-preparations", async (HttpContext context, string node) =>
            {
                if (!Authorized(context, node)) return Results.Unauthorized();
                var request = await context.Request.ReadFromJsonAsync<RoomPreparationRequest>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
                if (request is null || !request.IsValid()) return Results.BadRequest();
                context.RequestAborted.ThrowIfCancellationRequested();
                var permit = directory.IssueRoomPreparation(node, request);
                return permit is null ? Results.Conflict(new { error = "preparation_unavailable" }) : Results.Json(permit, JsonOptions);
            });
            app.MapPost("/v1/nodes/{node}/room-preparations/claim", async (HttpContext context, string node) =>
            {
                if (!Authorized(context, node)) return Results.Unauthorized();
                var request = await context.Request.ReadFromJsonAsync<RoomPreparationRedemption>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
                if (request is null || !request.IsValid()) return Results.BadRequest();
                context.RequestAborted.ThrowIfCancellationRequested();
                var claim = directory.ClaimRoomPreparation(node, request);
                return claim is null ? Results.Conflict(new { error = "preparation_unavailable" }) : Results.Json(claim, JsonOptions);
            });
            app.MapDelete("/v1/nodes/{node}/room-preparations", async (HttpContext context, string node) =>
            {
                if (!Authorized(context, node)) return Results.Unauthorized();
                var request = await context.Request.ReadFromJsonAsync<RoomPreparationRedemption>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
                if (request is null || !request.IsValid()) return Results.BadRequest();
                context.RequestAborted.ThrowIfCancellationRequested();
                return directory.CancelRoomPreparation(node, request) ? Results.NoContent() : Results.Conflict(new { error = "preparation_unavailable" });
            });
        }
        if (options.Migrations) MapMigrationRoutes(app, directory, options, Authorized);
        if (options.PlazaFederation) MapPlazaRoutes(app, directory, Authorized);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            return new(app, options);
        }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.ShutdownSeconds));
        try { await app.StopAsync(deadline.Token).ConfigureAwait(false); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }
}

internal sealed record NodeRegistration(string Instance);
internal sealed record NodeRelease(string Token);

/// <summary>Publishes a bounded directory and authenticated heartbeat listener when explicitly mounted.</summary>
[Plugin("cluster.directory", Description = "Opt-in server selector and room discovery directory.", Provides = [typeof(IServerDirectory), typeof(IServerSelectionDirectory), typeof(ILauncherPresenceDirectory)])]
public sealed class ServerDirectoryPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<DirectoryOptions>();
        var directory = new ServerDirectory(options);
        await context.AcquireEffectAsync(async ct => await DirectoryServer.StartAsync(options, directory, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        context.Effect(context.Services.Register<IServerDirectory>(directory));
        context.Effect(context.Services.Register<IServerSelectionDirectory>(directory));
        context.Effect(context.Services.Register<ILauncherPresenceDirectory>(directory));
        context.Logger.LogInformation("Cluster directory listening on {Address} with {Nodes} configured nodes.", options.Listen, options.Nodes.Length);
    }
}
