using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Installation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Admin.Http;

public sealed partial class AdminHttpPlugin
{
    private static void ConfigureClientPluginPolicy(WebApplication app, IPluginContext context)
    {
        if (context.Services.TryGet<IInstallationService>(out var configuredInstallation) && configuredInstallation is not null &&
            context.Services.TryGet<IPermissionRegistry>(out var registry) && registry is not null)
            context.Effect(registry.Register(new("webui.settings.client_plugins", "Read and change the client plugin recommendations and launch policy.")));
        app.MapGet("/api/setup/client-plugins", (HttpContext http) =>
        {
            var rejection = Authorize(http, out var installation);
            return rejection ?? Results.Ok(new InstallationClientPluginSettings(installation!.GetConfiguration().Settings.ClientPlugins));
        });
        app.MapPut("/api/setup/client-plugins", async (InstallationClientPluginSettings settings, HttpContext http, CancellationToken ct) =>
        {
            var rejection = Authorize(http, out var installation);
            if (rejection is not null) return rejection;
            if (http.Request.Headers["X-BFF-Request"] != "1") return Error("plugin_policy_request_guard_required", 403);
            try
            {
                await installation!.UpdateClientPluginsAsync(settings, ct).ConfigureAwait(false);
                return Results.Accepted();
            }
            catch (NotSupportedException) { return Error("plugin_policy_unavailable", 503); }
        });

        IResult? Authorize(HttpContext http, out IInstallationService? installation)
        {
            installation = null;
            http.Response.Headers.CacheControl = "no-store";
            if (!context.Services.TryGet<IAdminAccessControl>(out var access) || access is null)
                return Error("plugin_policy_identity_unavailable", 503);
            var account = access.GetAccountId(http.Request.Cookies[SessionCookie]);
            if (account is null) return Error("unauthorized", 401);
            if (!context.Services.TryGet<IPermissionService>(out var permissions) || permissions is null)
                return Error("plugin_policy_permissions_unavailable", 503);
            var principal = new CommandPrincipal(PrincipalKind.WebUi, account);
            if (!permissions.IsAllowed(principal, "webui.access") || !permissions.IsAllowed(principal, "webui.settings.client_plugins"))
                return Error("plugin_policy_access_denied", 403);
            if (!context.Services.TryGet<IInstallationService>(out installation) || installation is null)
                return Error("plugin_policy_unavailable", 503);
            return null;
        }

        static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
    }
}
