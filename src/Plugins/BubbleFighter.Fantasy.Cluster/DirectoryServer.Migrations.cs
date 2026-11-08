using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Cluster;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed partial class DirectoryServer
{
    private static void MapMigrationRoutes(WebApplication app, ServerDirectory directory, DirectoryOptions options,
        Func<HttpContext, string, bool> authorized)
    {
        app.MapGet("/v1/nodes/{node}/migrations/targets/{destination}/{build}", (HttpContext context, string node, string destination, string build) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            if (!directory.HasMigrationLease(node, token, epoch)) return Results.Conflict();
            var target = directory.ResolveMigrationTarget(node, token, epoch, destination, build);
            return target is null ? Results.NoContent() : Results.Json(target, MigrationTransport.Json);
        });
        app.MapPost("/v1/nodes/{node}/migrations", async (HttpContext context, string node) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            if (!directory.HasMigrationLease(node, token, epoch)) return Results.Conflict();
            var request = await context.Request.ReadFromJsonAsync<MigrationRequest>(MigrationTransport.Json, context.RequestAborted).ConfigureAwait(false);
            if (request is null || !MigrationTransport.Valid(request)) return Results.BadRequest();
            var (status, pending) = directory.EnqueueMigration(node, token, epoch, request);
            if (pending is null) return Results.StatusCode(status);
            try
            {
                var offer = await pending.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(options.MigrationRequestTimeoutMs),
                    context.RequestAborted).ConfigureAwait(false);
                return Results.Json(offer, MigrationTransport.Json);
            }
            catch (TimeoutException)
            {
                directory.AbandonMigration(pending);
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
            catch (MigrationUnavailableException) { return Results.Conflict(); }
            catch (OperationCanceledException)
            {
                directory.AbandonMigration(pending);
                throw;
            }
        });
        app.MapGet("/v1/nodes/{node}/migrations/inbox", async (HttpContext context, string node) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            if (!int.TryParse(context.Request.Query["wait"], NumberStyles.None, CultureInfo.InvariantCulture, out var wait)
                || wait is < 1 or > 25) return Results.BadRequest();
            var (status, envelope) = await directory.ReadMigrationAsync(node, token, epoch, TimeSpan.FromSeconds(wait),
                context.RequestAborted).ConfigureAwait(false);
            return envelope is null ? Results.StatusCode(status) : Results.Json(envelope, MigrationTransport.Json);
        });
        app.MapPost("/v1/nodes/{node}/migrations/{id}/offer", async (HttpContext context, string node, string id) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            if (!directory.HasMigrationLease(node, token, epoch)) return Results.Conflict();
            if (!AccountHandoff.IsMigrationId(id)) return Results.BadRequest();
            var offer = await context.Request.ReadFromJsonAsync<MigrationOffer>(MigrationTransport.Json, context.RequestAborted).ConfigureAwait(false);
            return offer is null ? Results.BadRequest() : Results.StatusCode(directory.OfferMigration(node, token, epoch, id, offer));
        });
        app.MapDelete("/v1/nodes/{node}/migrations/{id}", (HttpContext context, string node, string id) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            if (!AccountHandoff.IsMigrationId(id)) return Results.BadRequest();
            var (token, epoch) = Credentials(context);
            return Results.StatusCode(directory.CancelMigration(node, token, epoch, id));
        });
    }

    private static (string Token, long Epoch) Credentials(HttpContext context)
    {
        var token = context.Request.Headers[MigrationTransport.LeaseHeader].ToString();
        return token.Length <= 128 && long.TryParse(context.Request.Headers[MigrationTransport.EpochHeader],
            NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ? (token, epoch) : ("", 0);
    }
}
