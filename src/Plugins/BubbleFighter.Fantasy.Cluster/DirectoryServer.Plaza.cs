using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed partial class DirectoryServer
{
    // The longest plaza long-poll a node may ask for; the request deadline is extended by it.
    internal const int PlazaMaxWaitSeconds = 25;

    private static void MapPlazaRoutes(WebApplication app, ServerDirectory directory, Func<HttpContext, string, bool> authorized)
    {
        app.MapPost("/v1/nodes/{node}/plaza", async (HttpContext context, string node) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            var post = await context.Request.ReadFromJsonAsync<PlazaWirePost>(PlazaTransport.Json, context.RequestAborted).ConfigureAwait(false);
            if (post is null) return Results.BadRequest();
            var status = directory.PublishPlaza(node, token, epoch, post);
            return status == 204 ? Results.NoContent() : Results.StatusCode(status);
        });
        app.MapGet("/v1/nodes/{node}/plaza", async (HttpContext context, string node) =>
        {
            if (!authorized(context, node)) return Results.Unauthorized();
            var (token, epoch) = Credentials(context);
            var hub = context.Request.Query["hub"].ToString();
            if (hub.Length > 128 || !long.TryParse(context.Request.Query["after"], NumberStyles.None, CultureInfo.InvariantCulture, out var after)
                || !int.TryParse(context.Request.Query["wait"], NumberStyles.None, CultureInfo.InvariantCulture, out var wait)
                || wait is < 0 or > PlazaMaxWaitSeconds) return Results.BadRequest();
            var (status, batch) = await directory.ReadPlazaAsync(node, token, epoch, hub, after, TimeSpan.FromSeconds(wait),
                context.RequestAborted).ConfigureAwait(false);
            return batch is null ? Results.StatusCode(status) : Results.Json(batch, PlazaTransport.Json);
        });
    }
}
