using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Installation;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Admin.Http;

public sealed partial class AdminHttpPlugin
{
    // Entering the WebUI (webui.access) only shows the server; each action that changes it, or reveals room passwords,
    // has its own node, and webui.admin still allows them all.
    private const string RoomsInspectPermission = "webui.rooms.inspect";
    private const string ClientsManagePermission = "webui.clients.manage";
    private const string PluginsManagePermission = "webui.plugins.manage";
    private const string MaintenancePermission = "webui.server.maintenance";
    private const string RestartPermission = "webui.server.restart";

    private static void ConfigureOperationalEndpoints(WebApplication app, IPluginContext context, IPluginHost host)
    {
        if (context.Services.TryGet<IPermissionRegistry>(out var registry) && registry is not null)
            foreach (var (node, description) in new[]
            {
                (RoomsInspectPermission, "See room details, including room passwords."),
                (ClientsManagePermission, "Add client builds and import their data."),
                (PluginsManagePermission, "Load, unload, disable and reload server plugin rows."),
                (MaintenancePermission, "Turn maintenance mode on and off."),
                (RestartPermission, "Restart the server process."),
            })
                context.Effect(registry.Register(new(node, description)));
        app.MapGet("/api/rooms/{clientBuild}/{roomId:int}", async (string clientBuild, int roomId, HttpContext http, CancellationToken ct) =>
        {
            var denial = RequireAdmin(context, http, out _, RoomsInspectPermission);
            if (denial is not null) return denial;
            var rooms = context.Services.GetAll<IRoomAdminDirectory>();
            if (rooms.Count == 0)
                return Results.Problem("Room administration data is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
            var directory = rooms.FirstOrDefault(candidate => string.Equals(candidate.ClientBuild, clientBuild, StringComparison.OrdinalIgnoreCase));
            if (directory is null) return Results.NotFound();
            var room = await directory.GetAsync(roomId, ct).ConfigureAwait(false);
            return room is null ? Results.NotFound() : Results.Ok(room);
        });

        app.MapPost("/api/clients", async (AddClientRequest request, HttpContext http, CancellationToken ct) =>
        {
            var denial = RequireAdmin(context, http, out _, ClientsManagePermission);
            if (denial is not null) return denial;
            if (string.IsNullOrWhiteSpace(request.Profile) || request.Profile.Length > 64)
                return Results.BadRequest(new { error = "A packaged client profile id is required." });
            if (!context.Services.TryGet<IInstallationService>(out var installation) || installation is null)
                return Results.Problem("Client configuration is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
            // The packaged builds a setup may select; the running registry only knows the builds already serving players.
            if (!installation.Status.Profiles.Any(profile => profile.SetupEnabled && string.Equals(profile.Id, request.Profile, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "The packaged client profile was not found." });
            try
            {
                var configuration = installation.GetConfiguration();
                var settings = configuration.Settings;
                var clients = settings.Clients?.ToList() ??
                    (settings.Profile == "management" ? [] : [new InstallationClient(settings.Profile, settings.ClientDataPath, settings.DeferClientDataImport)]);
                if (clients.Any(client => string.Equals(client.Profile, request.Profile, StringComparison.OrdinalIgnoreCase)))
                    return Results.Conflict(new { error = "The client profile is already configured." });
                clients.Add(new InstallationClient(request.Profile, string.Empty, DeferClientDataImport: true));
                var updated = settings with
                {
                    Clients = clients,
                    Profile = "management",
                    ClientDataPath = string.Empty,
                    DeferClientDataImport = false,
                };
                await installation.UpdateAsync(new InstallationUpdate(updated, configuration.HasDatabasePassword), ct).ConfigureAwait(false);
                return Results.Accepted(value: new { profile = request.Profile, configured = true, dataDeferred = true });
            }
            catch (ArgumentException error)
            {
                return Results.Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException error)
            {
                return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/api/plugins/{rowId}/unload", async (string rowId, HttpContext http, CancellationToken ct) =>
        {
            var denial = RequireAdmin(context, http, out _, PluginsManagePermission);
            if (denial is not null) return denial;
            if (rowId.Equals(context.Row.Id, StringComparison.Ordinal))
                return Results.Conflict(new { error = "The administration API row cannot unload itself." });
            return await RunTransitionAsync(context, async () =>
            {
                await host.UnloadAsync(rowId, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { rowId, state = "Unloaded" });
            }).ConfigureAwait(false);
        });

        app.MapPost("/api/plugins/{rowId}/load", async (string rowId, HttpContext http, CancellationToken ct) =>
        {
            var denial = RequireAdmin(context, http, out _, PluginsManagePermission);
            if (denial is not null) return denial;
            return await RunTransitionAsync(context, async () =>
            {
                await host.LoadAsync(rowId, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { rowId, state = "Active" });
            }).ConfigureAwait(false);
        });

        app.MapPost("/api/plugins/{rowId}/disable", async (string rowId, HttpContext http, CancellationToken ct) =>
        {
            var denial = RequireAdmin(context, http, out _, PluginsManagePermission);
            if (denial is not null) return denial;
            return await RunTransitionAsync(context, async () =>
            {
                var result = await host.RequestState(rowId, enabled: false).WaitAsync(CancellationToken.None).ConfigureAwait(false);
                return result.Outcome == PluginTransitionOutcome.Converged
                    ? Results.Ok(new { rowId, state = "Unloaded", changedRowIds = result.ChangedRowIds })
                    : Results.Conflict(new { rowId, outcome = result.Outcome.ToString(), changedRowIds = result.ChangedRowIds });
            }).ConfigureAwait(false);
        });

        app.MapGet("/api/maintenance", (HttpContext http) =>
        {
            var denial = RequireAdmin(context, http, out _);
            if (denial is not null) return denial;
            return context.Services.TryGet<IHostReadiness>(out var maintenance) && maintenance is not null
                ? Results.Ok(new { enabled = maintenance.IsMaintenance })
                : Results.Problem("Maintenance control is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.MapPut("/api/maintenance", (MaintenanceRequest request, HttpContext http) =>
        {
            var denial = RequireAdmin(context, http, out _, MaintenancePermission);
            if (denial is not null) return denial;
            if (!context.Services.TryGet<IHostReadiness>(out var maintenance) || maintenance is null)
                return Results.Problem("Maintenance control is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
            maintenance.SetMaintenance(request.Enabled);
            context.Logger.LogWarning("Administration changed maintenance mode to {Enabled}.", request.Enabled);
            return Results.Ok(new { enabled = maintenance.IsMaintenance });
        });

        app.MapPost("/api/restart", (HttpContext http) =>
        {
            var denial = RequireAdmin(context, http, out _, RestartPermission);
            if (denial is not null) return denial;
            if (!context.Services.TryGet<IServerRestartService>(out var restart) || restart is null)
                return Results.Problem("Process restart is unavailable for this launch mode.", statusCode: StatusCodes.Status503ServiceUnavailable);
            http.Response.OnCompleted(() =>
            {
                try { restart.RequestRestart(); }
                catch (Exception error) { context.Logger.LogError(error, "Could not accept the requested server restart."); }
                return Task.CompletedTask;
            });
            return Results.Accepted("/api/health", new { restarting = true });
        });

        app.MapGet("/api/commands", (HttpContext http) =>
        {
            var denial = RequireAdmin(context, http, out var principal);
            if (denial is not null) return denial;
            return context.Services.TryGet<ICommandRegistry>(out var commands) && commands is not null
                ? Results.Ok(commands.List(principal!))
                : Results.Problem("The command registry is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.MapPost("/api/commands/execute", async (CommandTextRequest request, HttpContext http, CancellationToken ct) =>
        {
            var denial = RequireAdmin(context, http, out var principal);
            if (denial is not null) return denial;
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 1024)
                return Results.BadRequest(new { error = "Command text must contain 1 to 1024 characters." });
            if (!context.Services.TryGet<ICommandExecutor>(out var executor) || executor is null)
                return Results.Problem("The command executor is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
            var caller = new CommandCaller(principal!, principal!, "en");
            var result = await executor.ExecuteTextAsync(caller, request.Text, requirePrefix: false, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });
    }

    // A transition that reaches this row (a reload of something it depends on) stops this listener, and the listener
    // waits for the requests it is serving. The transition therefore runs apart from the request, and the request
    // answers as soon as this row starts closing; the page reconnects once the listener is back.
    internal static async Task<IResult> RunTransitionAsync(IPluginContext context, Func<Task<IResult>> transition)
    {
        var running = Task.Run(async () =>
        {
            try { return await transition().ConfigureAwait(false); }
            catch (Exception error)
            {
                return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (context.Lifetime.Register(() => closing.TrySetResult()))
            if (await Task.WhenAny(running, closing.Task).ConfigureAwait(false) == running)
                return await running.ConfigureAwait(false);
        return Results.Accepted(value: new { pending = true });
    }

    private static IResult? RequireAdmin(IPluginContext context, HttpContext http, out CommandPrincipal? principal, string? permission = null)
    {
        principal = null;
        if (!context.Services.TryGet<IAdminAccessControl>(out var access) || access is null)
            return Results.Problem("Administration identity is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        var account = access.GetAccountId(http.Request.Cookies[SessionCookie]);
        if (account is null) return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!context.Services.TryGet<IPermissionService>(out var permissions) || permissions is null)
            return Results.Problem("Administration permissions are unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        principal = new CommandPrincipal(PrincipalKind.WebUi, account);
        return permissions.IsAllowed(principal, "webui.access") && (permission is null || permissions.IsAllowed(principal, permission))
            ? null
            : Results.Json(new { error = "access_denied" }, statusCode: StatusCodes.Status403Forbidden);
    }

    private sealed record MaintenanceRequest(bool Enabled);
    private sealed record CommandTextRequest(string Text);
    private sealed record AddClientRequest(string Profile);
}
