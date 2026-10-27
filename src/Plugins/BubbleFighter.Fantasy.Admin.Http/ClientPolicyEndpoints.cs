using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Installation;
using BubbleFighter.Fantasy.Contracts.Launcher;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Admin.Http;

public sealed partial class AdminHttpPlugin
{
    private const string ClientPolicyPermission = "webui.settings.client_policy";

    // Every write names the revision it edited (If-Match), so two open editors cannot silently overwrite each other.
    private static void ConfigureClientPolicy(WebApplication app, IPluginContext context)
    {
        if (context.Services.TryGet<IInstallationService>(out var configuredInstallation) && configuredInstallation is not null &&
            context.Services.TryGet<IPermissionRegistry>(out var registry) && registry is not null)
            context.Effect(registry.Register(new(ClientPolicyPermission, "Read and change the client policy the Launcher enforces.")));

        app.MapGet("/api/setup/client-policy/rule-types", (HttpContext http) =>
            Authorize(http, out _) ?? Results.Ok(ClientPolicyRuleTypes.All.Select(type => new
            {
                name = type.Name,
                field = type.Field switch { ClientPolicyRuleField.Version => "version", ClientPolicyRuleField.Sources => "sources", _ => "none" },
                conflicts = type.Conflicts,
            })));
        app.MapGet("/api/setup/client-policy", (HttpContext http) =>
        {
            var rejection = Authorize(http, out var installation);
            if (rejection is not null) return rejection;
            var saved = installation!.GetConfiguration().Settings.ClientPolicy;
            context.Services.TryGet<IClientPolicyView>(out var view);
            return Results.Ok(new
            {
                mode = saved is null ? "profile" : "custom",
                revision = InstallationClientPolicySettings.Revision(saved),
                policy = saved,
                active = view?.Current,
            });
        });
        app.MapPut("/api/setup/client-policy", (InstallationClientPolicySettings settings, HttpContext http, CancellationToken ct) =>
            Write(http, ct, (_, _) => settings.Policy));
        app.MapPut("/api/setup/client-policy/sources", (ClientPolicySource[] sources, HttpContext http, CancellationToken ct) =>
            Write(http, ct, (saved, active) => (saved ?? active ?? ClientPolicy.Empty) with { Sources = sources }));
        app.MapPut("/api/setup/client-policy/plugins/{id}", (string id, ClientPluginRules plugin, HttpContext http, CancellationToken ct) =>
            Write(http, ct, (saved, active) =>
            {
                if (plugin?.Id != id) throw new ClientPolicyException("client_policy_invalid", id);
                var basis = saved ?? active ?? ClientPolicy.Empty;
                var plugins = basis.Plugins.ToList();
                var index = plugins.FindIndex(entry => entry.Id == id);
                if (index < 0) plugins.Add(plugin); else plugins[index] = plugin;
                return basis with { Plugins = plugins };
            }));
        app.MapDelete("/api/setup/client-policy/plugins/{id}", (string id, HttpContext http, CancellationToken ct) =>
            Write(http, ct, (saved, active) =>
            {
                var basis = saved ?? active ?? ClientPolicy.Empty;
                return basis with { Plugins = basis.Plugins.Where(entry => entry.Id != id).ToArray() };
            }));

        // A partial edit in profile mode starts from the policy launchers receive now and saves the result as custom.
        async Task<IResult> Write(HttpContext http, CancellationToken ct, Func<ClientPolicy?, ClientPolicy?, ClientPolicy?> change)
        {
            var rejection = Authorize(http, out var installation);
            if (rejection is not null) return rejection;
            if (http.Request.Headers["X-BFF-Request"] != "1") return Error("client_policy_request_guard_required", 403);
            var expected = http.Request.Headers.IfMatch.ToString().Trim('"');
            if (expected.Length == 0) return Error("client_policy_revision_required", 428);
            var saved = installation!.GetConfiguration().Settings.ClientPolicy;
            context.Services.TryGet<IClientPolicyView>(out var view);
            try
            {
                await installation.UpdateClientPolicyAsync(new(change(saved, view?.Current)), expected, ct).ConfigureAwait(false);
                return Results.Accepted();
            }
            catch (ClientPolicyRevisionException conflict)
            {
                return Results.Json(new { error = "client_policy_revision_conflict", revision = conflict.Current }, statusCode: 409);
            }
            catch (ClientPolicyException invalid)
            {
                return Results.Json(new { error = invalid.Code, plugin = invalid.Plugin, rule = invalid.Rule, other = invalid.Other }, statusCode: 400);
            }
            catch (InvalidOperationException busy) { return Error(busy.Message, 409); }
            catch (NotSupportedException) { return Error("client_policy_unavailable", 503); }
        }

        IResult? Authorize(HttpContext http, out IInstallationService? installation)
        {
            installation = null;
            http.Response.Headers.CacheControl = "no-store";
            if (!context.Services.TryGet<IAdminAccessControl>(out var access) || access is null)
                return Error("client_policy_identity_unavailable", 503);
            var account = access.GetAccountId(http.Request.Cookies[SessionCookie]);
            if (account is null) return Error("unauthorized", 401);
            if (!context.Services.TryGet<IPermissionService>(out var permissions) || permissions is null)
                return Error("client_policy_permissions_unavailable", 503);
            var principal = new CommandPrincipal(PrincipalKind.WebUi, account);
            if (!permissions.IsAllowed(principal, "webui.access") || !permissions.IsAllowed(principal, ClientPolicyPermission))
                return Error("client_policy_access_denied", 403);
            if (!context.Services.TryGet<IInstallationService>(out installation) || installation is null)
                return Error("client_policy_unavailable", 503);
            return null;
        }

        static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
    }
}
