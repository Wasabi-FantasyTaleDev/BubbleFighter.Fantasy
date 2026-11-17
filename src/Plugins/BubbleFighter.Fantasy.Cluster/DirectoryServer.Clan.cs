using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed partial class DirectoryServer
{
    // The longest clan long-poll a node may ask for; the request deadline is extended by it.
    internal const int ClanMaxWaitSeconds = 25;

    private static void MapClanRoutes(WebApplication app, ServerDirectory directory, Func<HttpContext, string, bool> authorized)
    {
        app.MapPost("/v1/nodes/{node}/clan", async (HttpContext context, string node) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            var post = await context.Request.ReadFromJsonAsync<ClanWirePost>(ClanTransport.Json, context.RequestAborted).ConfigureAwait(false);
            if (post is null) return Results.BadRequest();
            var status = directory.PublishClan(node, token, epoch, post);
            return status == 204 ? Results.NoContent() : Results.StatusCode(status);
        });
        app.MapGet("/v1/nodes/{node}/clan", async (HttpContext context, string node) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            var hub = context.Request.Query["hub"].ToString();
            if (hub.Length > 128 || !long.TryParse(context.Request.Query["after"], NumberStyles.None, CultureInfo.InvariantCulture, out var after)
                || !int.TryParse(context.Request.Query["wait"], NumberStyles.None, CultureInfo.InvariantCulture, out var wait)
                || wait is < 0 or > ClanMaxWaitSeconds) return Results.BadRequest();
            var (status, batch) = await directory.ReadClanAsync(node, token, epoch, hub, after, TimeSpan.FromSeconds(wait),
                context.RequestAborted).ConfigureAwait(false);
            return batch is null ? Results.StatusCode(status) : Results.Json(batch, ClanTransport.Json);
        });
    }
}
