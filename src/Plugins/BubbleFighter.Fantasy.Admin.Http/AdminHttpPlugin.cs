using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Distribution;
using BubbleFighter.Fantasy.Contracts.Installation;
using BubbleFighter.Fantasy.Contracts.Listeners;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Admin.Http;

/// <summary>
/// Administration HTTP API plugin. Hosts a Kestrel minimal API exposing health, an overview,
/// plugin row state, row reload, client profiles, opcode tables, live sessions, rooms, settings
/// and a bounded log buffer, and serves the web interface a content row publishes. Anything
/// that needs HTTP reachability should register endpoints through it rather than hosting a
/// second listener.
/// </summary>
[Plugin("admin.http", Description = "Kestrel administration API.",
    Requires = [typeof(IPluginHost)],
    Optional = [typeof(IClientProfileRegistry), typeof(IOpcodeTableRegistry), typeof(ISessionRegistry), typeof(IRoomStartService),
        typeof(IAdminWebContent), typeof(IBuildReleaseLabels), typeof(IMatchConnectionTracker),
        typeof(IInstallationService), typeof(IAdminAccessControl), typeof(IPermissionService), typeof(IPermissionRegistry), typeof(IDistributionSources), typeof(IDistributionPlans), typeof(IDistributionPackages), typeof(IDistributionJobs)],
    OptionalMany = [typeof(IListenerEndpoint)])]
public sealed partial class AdminHttpPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<AdminHttpOptions>();
        if (!TryParseEndPoint(options.Listen, out var endPoint, out var error))
            throw new ConfigException(context.Row.Id, [$"listen: {error}"]);

        var ring = new LogRing(options.LogBuffer);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(context.Logger));
        builder.Logging.AddProvider(new RingLoggerProvider(ring));
        // Enums travel by name so the web interface never has to know their numeric values.
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = options.MaximumRequestBytes;
            kestrel.Listen(endPoint!);
        });

        var app = builder.Build();
        context.Effect(() =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { app.StopAsync(timeout.Token).GetAwaiter().GetResult(); }
            finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        var host = context.Services.Get<IPluginHost>();
        var processStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        context.Services.TryGet<IMatchConnectionTracker>(out var matchTracker);
        context.Services.TryGet<IBuildReleaseLabels>(out var releaseLabels);
        bool IsMatchBound(Abstractions.Protocol.ISession session) => matchTracker?.IsMatchBound(session) ?? false;

        context.Services.TryGet<IAdminAccessControl>(out var access);
        context.Services.TryGet<IInstallationService>(out var installation);
        if (access is not null)
        {
            if (!IPAddress.IsLoopback(endPoint!.Address) && !access.AllowsRemoteBootstrap)
                throw new ConfigException(context.Row.Id, ["A non-loopback installation listener requires an explicit bootstrap_token of at least 32 characters."]);
            ConfigureInstallation(app, access, installation, options, endPoint!);
        }
        else if (!IPAddress.IsLoopback(endPoint!.Address))
            // Without an installation there are no administrator accounts, so nothing could guard a reachable listener
            // against anyone reloading plugins, starting rooms or reading sessions and logs.
            throw new ConfigException(context.Row.Id,
                ["Administration without an installation has no accounts and listens on loopback only. "
                + "Start the server with 'start' (first-run setup) to sign in with an administrator account from other machines."]);
        if (access is null)
            app.MapGet("/api/auth/status", () => Results.Ok(new { mode = "none", authenticated = true, installation = false }));

        app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

        ConfigureDistribution(app, context);
        ConfigureClientPluginPolicy(app, context);

        app.MapGet("/api/plugins", () => Results.Ok(host.Instances));

        app.MapPost("/api/plugins/{rowId}/reload", async (string rowId, CancellationToken ct) =>
        {
            try
            {
                var result = await host.ReloadAsync(rowId, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapGet("/api/profiles", () =>
            context.Services.TryGet<IClientProfileRegistry>(out var profiles) && profiles is not null
                ? Results.Ok(profiles.All)
                : Results.Problem("IClientProfileRegistry is not available.", statusCode: StatusCodes.Status503ServiceUnavailable));

        // The optional profile query keeps the web interface from downloading every build's table
        // when it shows one.
        app.MapGet("/api/opcodes", (string? profile) =>
            context.Services.TryGet<IOpcodeTableRegistry>(out var tables) && tables is not null
                ? Results.Ok(tables.All.Where(t => profile is null || t.Profile.Id == profile).Select(t => new
                {
                    profile = t.Profile.Id,
                    opcodes = t.All.Concat(t.ForConnection(OpcodeConnection.Relay).All.Where(o => o.Connection == OpcodeConnection.Relay))
                        .Select(o => new { o.Value, direction = o.Direction.ToString(), connection = o.Connection.ToString(), o.Name, evidence = o.Evidence.ToString(), o.Source, o.Notes }),
                }))
                : Results.Problem("IOpcodeTableRegistry is not available.", statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/api/sessions", () =>
            context.Services.TryGet<ISessionRegistry>(out var sessions) && sessions is not null
                ? Results.Ok(sessions.All.Select(s => new
                {
                    s.Id,
                    profile = s.Profile.Id,
                    remote = s.Connection.RemoteEndPoint.ToString(),
                    matchBound = IsMatchBound(s),
                    player = s.TryFeature<IPlayerSession>(out var player) && player is not null
                        ? new { player.Identity.UserNum, player.Identity.Username, player.Identity.Nickname, player.Identity.Level, phase = player.Phase }
                        : null,
                }))
                : Results.Problem("ISessionRegistry is not available.", statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/api/overview", async (CancellationToken ct) =>
        {
            var profiles = context.Services.TryGet<IClientProfileRegistry>(out var registry) && registry is not null ? registry.All : [];
            var players = (context.Services.TryGet<ISessionRegistry>(out var sessions) && sessions is not null ? sessions.All : [])
                .Where(s => !IsMatchBound(s)).ToArray();
            int? rooms = context.Services.TryGet<IRoomStartService>(out var roomService) && roomService is not null
                ? (await roomService.ListAsync(ct).ConfigureAwait(false)).Count
                : null;
            var listeners = context.Services.GetAll<IListenerEndpoint>();
            var instances = host.Instances;
            return Results.Ok(new
            {
                startedUtc = processStartedUtc,
                players = players.Length,
                rooms,
                rows = new
                {
                    total = instances.Count,
                    active = instances.Count(i => i.State == PluginState.Active),
                    disabled = instances.Count(i => i.State == PluginState.Unloaded),
                    failed = instances.Count(i => i.State is PluginState.Failed or PluginState.CleanupFailed or PluginState.WorkFailed),
                },
                builds = profiles.Select(p =>
                {
                    var game = listeners.FirstOrDefault(l => l.Protocol == $"{p.Id}/game");
                    var relay = listeners.FirstOrDefault(l => l.Protocol == $"{p.Id}/relay");
                    return new
                    {
                        p.Id,
                        p.Region,
                        release = releaseLabels is not null && releaseLabels.TryGetRelease(p.Id, out var releaseName) ? releaseName : null,
                        p.Description,
                        p.ClientVersionMajor,
                        gamePort = game?.LocalEndPoint.Port,
                        relayPort = relay?.LocalEndPoint.Port,
                        listening = game is not null,
                        players = players.Count(s => s.Profile.Id == p.Id),
                    };
                }),
            });
        });

        // Cross-version play does not exist yet: the setting reads as null and cannot be changed,
        // so the interface can show the switch as pending instead of guessing a value.
        app.MapGet("/api/settings", () => Results.Ok(new { crossVersionPlay = (bool?)null }));
        app.MapPut("/api/settings", () => Results.Problem(
            "Cross-version play is not supported by this server build.", statusCode: StatusCodes.Status409Conflict, title: "Setting unsupported"));

        app.MapGet("/api/logs", (long? after, int? limit) =>
        {
            var (entries, next) = ring.Read(after ?? 0, Math.Clamp(limit ?? 500, 1, 1000));
            return Results.Ok(new { entries, next });
        });

        // The match start is server-driven, so the administration API exposes
        // the room start seam instead of inventing a client packet. The
        // endpoints 409 when no row publishes IRoomStartService.
        app.MapGet("/api/rooms", async (CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IRoomStartService>(out var rooms) || rooms is null)
                return Results.Problem("IRoomStartService is not available.", statusCode: StatusCodes.Status503ServiceUnavailable);
            var live = await rooms.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(live.Select(r => new { roomId = r.RoomId, seats = r.Seats, phase = r.Phase }));
        });

        app.MapPost("/api/rooms/{roomId}/start", async (int roomId, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IRoomStartService>(out var rooms) || rooms is null)
                return Results.Problem("IRoomStartService is not available.", statusCode: StatusCodes.Status503ServiceUnavailable);
            try
            {
                await rooms.StartAsync(roomId, ct).ConfigureAwait(false);
                return Results.Ok(new { roomId, started = true });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        if (context.Services.TryGet<IAdminWebContent>(out var web) && web is not null)
        {
            // Registered last so every /api route above wins; an unknown /api path is a plain 404.
            app.MapGet("/{**path}", (string? path, HttpContext http) => ServeContent(web, path, http.Response));
        }

        // This plugin cannot hook the host's logger factory, so beyond its own pipeline the ring
        // records the connection events the admin row can observe on the bus.
        context.Effect(context.Events.OnAsync<ConnectionAcceptedEvent>((e, _) =>
        {
            ring.Append(LogLevel.Information, "net", $"Connection {e.Connection.Id} accepted from {e.Connection.RemoteEndPoint} on row '{e.ListenerId}'.");
            return ValueTask.CompletedTask;
        }));
        context.Effect(context.Events.On<ConnectionClosedEvent>(e =>
            ring.Append(LogLevel.Information, "net", $"Connection {e.Connection.Id} from {e.Connection.RemoteEndPoint} closed: {e.Reason}.")));
        context.Effect(context.Events.On<ClientVersionMismatchEvent>(e =>
            ring.Append(LogLevel.Warning, "protocol", $"Session {e.Session.Id} reported client version {e.Reported}, expected {e.Expected}.")));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        ring.Append(LogLevel.Information, "admin.http", $"Admin API listening on {options.Listen}.");
        context.Logger.LogInformation("Row '{RowId}' admin API listening on {Listen}.", context.Row.Id, options.Listen);
        if (web is not null)
        {
            foreach (var address in app.Urls)
            {
                var uri = new UriBuilder(address);
                if (uri.Host is "0.0.0.0" or "[::]" or "::") uri.Host = "127.0.0.1";
                context.Logger.LogInformation("WebUI: {Address}", uri.Uri.AbsoluteUri);
            }
            if (options.OpenBrowser && app.Urls.FirstOrDefault() is { } first)
                OpenBrowser(context, first);
        }
    }

    // A failed browser launch leaves the server running; the address is already in the log.
    private static void OpenBrowser(IPluginContext context, string address)
    {
        var uri = new UriBuilder(address);
        if (uri.Host is "0.0.0.0" or "[::]" or "::") uri.Host = "127.0.0.1";
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            context.Logger.LogWarning("Could not open a browser for the WebUI: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Serves one file of the web interface. The interface is a single-page app on hash routes, so
    /// only real files are served and anything else is a 404. The entry page is revalidated on
    /// every load so a rebuilt interface shows up at once; hashed build assets never change and
    /// are cached for a year.
    /// </summary>
    private static IResult ServeContent(IAdminWebContent web, string? path, HttpResponse response)
    {
        var relative = string.IsNullOrEmpty(path) ? "index.html" : path;
        if (relative.StartsWith("api/", StringComparison.Ordinal) || !web.TryGetFile(relative, out var content, out var contentType))
            return Results.NotFound();
        response.Headers.CacheControl = relative.StartsWith("assets/", StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        return Results.Bytes(content, contentType);
    }

    private static bool TryParseEndPoint(string text, out IPEndPoint? endPoint, out string? error)
    {
        endPoint = null;
        error = null;
        var separator = text.LastIndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
        {
            error = "expected 'host:port'";
            return false;
        }
        var host = text[..separator].Trim('[', ']');
        if (!int.TryParse(text[(separator + 1)..], out var port) || port is < 0 or > 65535)
        {
            error = "invalid port";
            return false;
        }
        if (host is "*" or "+")
        {
            endPoint = new IPEndPoint(IPAddress.Any, port);
            return true;
        }
        if (IPAddress.TryParse(host, out var address))
        {
            endPoint = new IPEndPoint(address, port);
            return true;
        }
        var addresses = Dns.GetHostAddresses(host);
        if (addresses.Length == 0)
        {
            error = $"cannot resolve host '{host}'";
            return false;
        }
        endPoint = new IPEndPoint(addresses[0], port);
        return true;
    }

    private sealed class ForwardingLoggerProvider(Microsoft.Extensions.Logging.ILogger target) : ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ForwardingLogger(target, categoryName);

        public void Dispose() { }

        private sealed class ForwardingLogger(Microsoft.Extensions.Logging.ILogger target, string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    target.Log(logLevel, exception, "[{Category}] {Message}", category, formatter(state, exception));
            }
        }
    }
}

/// <summary>Configuration for the <see cref="AdminHttpPlugin"/> row.</summary>
public sealed class AdminHttpOptions
{
    /// <summary>Gets or sets the bind address as <c>host:port</c>; defaults to loopback only.</summary>
    [Required]
    public string Listen { get; set; } = "127.0.0.1:8080";

    /// <summary>Gets or sets how many recent log lines the <c>/api/logs</c> buffer keeps; older lines are dropped.</summary>
    [Range(16, 100_000)]
    public int LogBuffer { get; set; } = 2000;

    /// <summary>Gets or sets the maximum administration request body size, including installation forms.</summary>
    [Range(4096, 1048576)]
    public long MaximumRequestBytes { get; set; } = 65536;

    /// <summary>Gets or sets comma-separated HTTP host names accepted for installation; empty accepts the bind address and loopback aliases.</summary>
    [MaxLength(2048)]
    public string AllowedHosts { get; set; } = "";

    /// <summary>Gets or sets whether the WebUI address opens in the default browser once the listener is up.</summary>
    public bool OpenBrowser { get; set; }
}
