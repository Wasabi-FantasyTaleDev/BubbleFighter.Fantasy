using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Messenger;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>Owns the public version-one HTTP API without exposing administration routes.</summary>
public sealed class LauncherServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SemaphoreSlim _workers;
    private readonly LauncherOptions _options;
    private LauncherServer(WebApplication app, SemaphoreSlim workers, LauncherOptions options) { _app = app; _workers = workers; _options = options; }

    /// <summary>Gets the actual bound addresses, including the port chosen when listen uses zero.</summary>
    public IReadOnlyCollection<string> Addresses => _app.Urls.ToArray();

    /// <summary>Validates settings, binds the listener, and serves accounts and client presets from interfaces.</summary>
    public static Task<LauncherServer> StartAsync(LauncherOptions options, ILauncherAccountRepository accounts,
        LauncherSessions sessions, IPlayerWalletRepository? wallets = null, IPlayerProgressRepository? progress = null, CancellationToken ct = default) =>
        StartAsync(options, accounts, sessions, players: null, matchConnections: null, wallets: wallets, progress: progress, ct: ct);

    /// <summary>Binds the launcher API with presence limited to each authenticated player's owning login connection.</summary>
    /// <remarks>Direct callers may supply a match classifier for older session providers without login ownership.
    /// The plugin row does not acquire this service: its provider can depend on the tickets published here.</remarks>
    public static async Task<LauncherServer> StartAsync(LauncherOptions options, ILauncherAccountRepository accounts,
        LauncherSessions sessions, ISessionRegistry? players, IMatchConnectionTracker? matchConnections = null,
        IPlayerWalletRepository? wallets = null, IPlayerProgressRepository? progress = null, CancellationToken ct = default,
        ILogger? logger = null, IMessengerTicketIssuer? messenger = null, IHostReadiness? readiness = null,
        ICompanionTicketIssuer? companion = null)
    {
        Validator.ValidateObject(options, new ValidationContext(options), true);
        var errors = options.Validate().ToArray(); if (errors.Length > 0) throw new ValidationException(string.Join("; ", errors.Select(e => e.ErrorMessage)));
        var policy = new LauncherClientPolicy(options);
        var presence = new LauncherPresence(options, players, matchConnections);
        if (options.PlayerLimit is not null && players is null)
            throw new ValidationException("player_limit requires ISessionRegistry to enforce launcher admission.");
        await sessions.InitializeAsync(ct).ConfigureAwait(false);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPEndPoint.Parse(options.Listen)); k.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            k.Limits.MaxConcurrentConnections = options.MaxConnections;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
        });
        var app = builder.Build(); var workers = new SemaphoreSlim(options.MaxConcurrentRequests);
        var budget = new LauncherRequestBudget(options.RequestsPerMinute, options.MaxRateLimitAddresses);
        var dummyHash = HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), options.PasswordIterations);
        var gameLogins = new LauncherGameLogins(options);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!budget.TryTake(context.Connection.RemoteIpAddress) || !await workers.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
            { await Error(429, "rate_limited").ExecuteAsync(context).ConfigureAwait(false); return; }
            try { await next(context).ConfigureAwait(false); }
            catch (LauncherUpdateRequiredException)
            { context.Response.StatusCode = 426; await context.Response.WriteAsJsonAsync(new { error = "launcher_update_required", policy.MinimumLauncherVersion }, context.RequestAborted).ConfigureAwait(false); }
            catch (LauncherApiFailure e) when (!context.Response.HasStarted)
            {
                var result = e.Code == "server_maintenance"
                    ? Results.Json(new { error = e.Code, options.MaintenanceStart, options.MaintenanceEnd, options.MaintenanceDescription }, statusCode: e.Status)
                    : Error(e.Status, e.Code);
                await result.ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (Exception e) when (e is JsonException or BadHttpRequestException or ArgumentException or InvalidDataException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "invalid_request" }, context.RequestAborted).ConfigureAwait(false); }
            catch (InvalidOperationException e) when (e.Message == "Authentication capacity reached.")
            { await Error(503, "authentication_capacity").ExecuteAsync(context).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) when (!context.Response.HasStarted)
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }, context.RequestAborted).ConfigureAwait(false); }
            finally { workers.Release(); }
        });

        app.MapGet("/v1/catalog", (HttpContext context) => Results.Json(new
        {
            schema = 1, game = "bubblefighter", name = options.Name, registrationEnabled = options.RegistrationEnabled,
            secureUrl = options.SecureUrl.Length == 0 ? null : options.SecureUrl,
            registrationFields = Array.Empty<object>(),
            options.HostPolicy, region = options.Region.Length == 0 ? null : options.Region, options.SupportsInstances, options.SupportsPlugins, options.LevelIcon,
            profileFields = new[] { "level", "lucci", "cash" },
            presentation = presence.Presentation(),
            policy.MinimumLauncherVersion, clientPolicy = policy.ClientPolicy,
            instances = options.Instances.Select(instance => CatalogInstance(instance, context.Request.Host.Host)),
        }));
        app.MapGet("/v1/players", () =>
        {
            // Disabling disclosure must not even consult the session registry.
            if (!options.RosterEnabled) return Results.Json(new { schema = 1, enabled = false, players = Array.Empty<object>(), truncated = false });
            var online = presence.Snapshot();
            return Results.Json(new { schema = 1, enabled = true,
                players = online.Take(options.MaxRosterPlayers).Select(p => new { name = p.Nickname, level = p.Level }),
                truncated = online.Length > options.MaxRosterPlayers });
        });
        app.MapGet("/v1/instances/{id}/instance.toml", (string id) =>
        {
            var preset = options.Instances.SingleOrDefault(i => i.Id == id); if (preset is null) return Results.NotFound();
            // Portable instance metadata contains no server address or credentials.
            var text = "schema = 1\n[instance]\nid = " + JsonSerializer.Serialize(preset.Id) + "\nname = " + JsonSerializer.Serialize(preset.Name.Single(n => n.Language == "en").Text) +
                "\ngame = \"bubblefighter\"\nbuild = " + JsonSerializer.Serialize(preset.Build) + "\n";
            return Results.Text(text, "application/toml");
        });
        app.MapPost("/v1/auth/register", async (HttpContext context) =>
        {
            if (!options.RegistrationEnabled) return Error(403, "registration_disabled");
            RequireReady(readiness);
            var body = await Body(context).ConfigureAwait(false);
            policy.Check(context, body);
            var username = Username(body); var password = Password(body);
            if (password.Length < options.MinimumPasswordLength) return Results.BadRequest(new { error = "password_too_short" });
            if (body.TryGetProperty("fields", out var fields) && (fields.ValueKind != JsonValueKind.Object || fields.EnumerateObject().Any())) return Results.BadRequest(new { error = "unsupported_registration_fields" });
            var created = await accounts.RegisterAsync(username, HashPassword(password, options.PasswordIterations), context.RequestAborted).ConfigureAwait(false);
            return created ? Results.Json(new { created = true }, statusCode: 201) : Results.Conflict(new { error = "account_unavailable" });
        });
        app.MapPost("/v1/auth/login", async (HttpContext context) =>
        {
            var body = await Body(context).ConfigureAwait(false);
            policy.Check(context, body);
            var username = Username(body);
            var password = Password(body);
            RequireReady(readiness);
            presence.CheckAdmission(username);
            var account = await accounts.FindAsync(username, context.RequestAborted).ConfigureAwait(false);
            if (account is null && options.CreateAccountOnLogin)
            {
                if (password.Length < options.MinimumPasswordLength) return Results.BadRequest(new { error = "password_too_short" });
                // A concurrent sign-in may create the same name first; the password is then checked against the winner.
                await accounts.RegisterAsync(Username(body), HashPassword(password, options.PasswordIterations), context.RequestAborted).ConfigureAwait(false);
                account = await accounts.FindAsync(Username(body), context.RequestAborted).ConfigureAwait(false);
            }
            if (options.AllowLegacyPasswordEnrollment && account is { PasswordHash.Length: 0 } && !(account.BannedUntil > DateTime.UtcNow))
            {
                if (password.Length < options.MinimumPasswordLength) return Results.BadRequest(new { error = "password_too_short" });
                await accounts.TrySetInitialPasswordAsync(account.Username, HashPassword(password, options.PasswordIterations), context.RequestAborted).ConfigureAwait(false);
                // A concurrent login may have enrolled a different password; verify the committed winner.
                account = await accounts.FindAsync(account.Username, context.RequestAborted).ConfigureAwait(false);
            }
            var valid = VerifyPassword(password, account?.PasswordHash is { Length: > 0 } hash ? hash : dummyHash);
            if (account is null) return Error(401, "account_not_found");
            if (!valid) return Error(401, "wrong_password");
            if (account.BannedUntil > DateTime.UtcNow) return Error(401, "account_banned");
            var profile = await Profile(account, options, wallets, progress, context.RequestAborted).ConfigureAwait(false);
            return Grant(await sessions.SignInAsync(account.Username, context.RequestAborted).ConfigureAwait(false), profile, policy);
        });
        app.MapPost("/v1/auth/refresh", async (HttpContext context) =>
        {
            var body = await Body(context).ConfigureAwait(false);
            policy.Check(context, body);
            var grant = await sessions.RefreshAsync(RefreshToken(body), context.RequestAborted).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException();
            var account = await accounts.FindAsync(grant.Username, context.RequestAborted).ConfigureAwait(false);
            if (account is null || account.BannedUntil > DateTime.UtcNow)
            {
                await sessions.SignOutAsync(grant.RefreshToken, grant.AccessToken, context.RequestAborted).ConfigureAwait(false);
                throw new LauncherApiFailure(401, account is null ? "unauthorized" : "account_banned");
            }
            return Grant(grant, await Profile(account, options, wallets, progress, context.RequestAborted).ConfigureAwait(false), policy);
        });
        app.MapPost("/v1/auth/logout", async (HttpContext context) =>
        {
            // The refresh credential alone is enough: a restarted launcher holds no live bearer yet.
            var body = await Body(context).ConfigureAwait(false);
            var refresh = body.TryGetProperty("refreshToken", out _) ? RefreshToken(body) : null;
            var bearer = OptionalBearer(context);
            if (refresh is null && bearer is null) throw new InvalidDataException();
            if (!await sessions.SignOutAsync(refresh, bearer, context.RequestAborted).ConfigureAwait(false))
                return Results.Json(new { error = "session_mismatch" }, statusCode: 401);
            return Results.Json(new { signedOut = true });
        });
        app.MapGet("/v1/account", async (HttpContext context) =>
        {
            var account = await Authorized(context, accounts, sessions).ConfigureAwait(false);
            return Results.Json(await Profile(account, options, wallets, progress, context.RequestAborted).ConfigureAwait(false));
        });
        app.MapPost("/v1/launch", async (HttpContext context) =>
        {
            // A build whose players sign in inside the game is launched before anyone signs in; the game binds the
            // launch with its own sign-in (launchId + launchKey below). Every other launch needs the bearer first.
            var anonymous = OptionalBearer(context) is null;
            var account = anonymous ? null : await Authorized(context, accounts, sessions).ConfigureAwait(false);
            var body = await Body(context).ConfigureAwait(false);
            policy.Check(context, body);
            var build = body.TryGetProperty("build", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException();
            var preset = options.Instances.FirstOrDefault(i => i.Build == build);
            if (preset is null) return Results.BadRequest(new { error = "unsupported_build" });
            var metadata = LauncherLaunchMetadata.Read(body);
            RequireReady(readiness);
            if (account is null)
            {
                if (preset.LauncherLogin != "game") throw new UnauthorizedAccessException();
                var opened = gameLogins.Open(build!);
                logger?.LogInformation("Launch opened for an in-game sign-in on build {Build}, release {ReleaseVersion}.", build, metadata.ReleaseVersion);
                return Results.Json(new { launchId = opened.Id, launchKey = opened.Key, expiresAt = opened.ExpiresAt, gameLogin = true });
            }
            string? boundLaunch = null;
            if (body.TryGetProperty("launchId", out var launchValue))
            {
                var launchKey = body.TryGetProperty("launchKey", out var keyValue) && keyValue.ValueKind == JsonValueKind.String ? keyValue.GetString()! : throw new InvalidDataException();
                boundLaunch = launchValue.ValueKind == JsonValueKind.String ? launchValue.GetString()! : throw new InvalidDataException();
                if (!gameLogins.Bind(boundLaunch, launchKey, build!, account.Username)) return Error(404, "launch_not_found");
            }
            presence.CheckAdmission(account.Username);
            // The companion is optional: a full launch table leaves this launch without it rather than refusing the game.
            CompanionGrant? companionGrant = null;
            if (companion is not null)
            {
                try { companionGrant = companion.Open(account.Username, build!); }
                catch (InvalidOperationException e) { logger?.LogWarning("Companion launch not opened: {Reason}", e.Message); }
            }
            var ticket = sessions.Issue(Bearer(context), build!, metadata.LauncherLanguage, companionGrant?.LaunchId);
            logger?.LogInformation("Launcher ticket issued for build {Build}, release {ReleaseVersion}, patch {PatchVersion}, languages {LauncherLanguage}/{NativeLanguage}/{ClientLanguage}.",
                build, metadata.ReleaseVersion, metadata.PatchVersion, metadata.LauncherLanguage, metadata.NativeLanguage, metadata.ClientLanguage);
            // The messenger ticket is separate from the single-use game ticket, so the game login cannot consume it.
            var messengerTicket = messenger?.Issue(account.Username, account.Nickname ?? account.Username, build!);
            var reply = new Dictionary<string, object?> { ["account"] = account.Username, ["ticket"] = ticket.Token, ["expiresAt"] = ticket.ExpiresAt };
            if (messengerTicket is not null)
                reply["messenger"] = new { url = messenger!.PublicUrl.Replace("{host}", context.Request.Host.Host, StringComparison.Ordinal), ticket = messengerTicket.Value, expiresAt = messengerTicket.ExpiresAt };
            if (companionGrant is not null) reply["companion"] = CompanionReply(companionGrant, context.Request.Host.Host);
            // The launcher that opened an in-game sign-in launch fetches this grant with the launch key.
            if (boundLaunch is not null && companionGrant is not null) gameLogins.Attach(boundLaunch, companionGrant);
            return Results.Json(reply);
        });
        // The launcher of an in-game sign-in launch holds no bearer: its launch key fetches the companion grant once the
        // game has signed in (404 launch_not_bound until then), and a fresh one on every later call.
        app.MapPost("/v1/launch/companion", async (HttpContext context) =>
        {
            var body = await Body(context).ConfigureAwait(false);
            string Text(string name) => body.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException();
            var launchId = Text("launchId");
            var found = gameLogins.Companion(launchId, Text("launchKey"));
            if (found is null) return Error(404, gameLogins.Known(launchId) ? "launch_not_bound" : "launch_not_found");
            var (owner, grant, fresh) = found.Value;
            if (companion is null || grant is null) return Error(404, "companion_unavailable");
            var current = fresh ? grant : companion.Renew(owner, grant.LaunchId);
            return current is null ? Error(404, "launch_not_found") : Results.Json(CompanionReply(current, context.Request.Host.Host));
        });
        // A reconnecting launcher needs a fresh one-time companion ticket for the launch it already holds.
        app.MapPost("/v1/companion/tickets", async (HttpContext context) =>
        {
            var account = await Authorized(context, accounts, sessions).ConfigureAwait(false);
            var body = await Body(context).ConfigureAwait(false);
            var launchId = body.TryGetProperty("launchId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException();
            if (companion is null) return Error(404, "companion_unavailable");
            var grant = companion.Renew(account.Username, launchId);
            return grant is null ? Error(404, "launch_not_found") : Results.Json(CompanionReply(grant, context.Request.Host.Host));
        });
        try { await app.StartAsync(ct).ConfigureAwait(false); return new LauncherServer(app, workers, options); }
        catch { await app.DisposeAsync().ConfigureAwait(false); workers.Dispose(); throw; }
    }

    // An instance without a host is served from this machine: it advertises the address the launcher used to reach the API.
    private static Dictionary<string, object?> CatalogInstance(LauncherInstanceOptions instance, string requestHost)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = instance.Id, ["build"] = instance.Build,
            ["name"] = instance.Name.ToDictionary(n => n.Language, n => n.Text, StringComparer.Ordinal),
            ["releaseVersion"] = instance.ReleaseVersion, ["clientLanguages"] = instance.ClientLanguages,
            ["protocolVersion"] = instance.ProtocolVersion, ["patchVersion"] = instance.PatchVersion.Length == 0 ? null : instance.PatchVersion,
            ["endpoint"] = new { Host = instance.Host.Length == 0 ? requestHost : instance.Host, instance.Port }, ["launcherLogin"] = instance.LauncherLogin,
        };
        if (instance.DownloadSource is { } source)
            result["downloadSource"] = new { source.Kind, source.BaseUrl, source.CatalogPath, source.PackageId };
        return result;
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    private static object CompanionReply(CompanionGrant grant, string requestHost) => new
    {
        url = grant.Url.Replace("{host}", requestHost, StringComparison.Ordinal), ticket = grant.Ticket, launchId = grant.LaunchId, expiresAt = grant.ExpiresAt,
    };

    // Game listeners accept connections while later rows are still activating; new sign-ins and launches wait until
    // every row is active so a player never reaches a half-composed server.
    private static void RequireReady(IHostReadiness? readiness)
    {
        if (readiness is { IsReady: false }) throw new LauncherApiFailure(503, "server_not_ready");
    }

    private static async Task<object> Profile(LauncherAccount account, LauncherOptions options, IPlayerWalletRepository? wallets, IPlayerProgressRepository? progress, CancellationToken ct)
    {
        var wallet = account.UserNumber is { } user && wallets is not null ? await wallets.FindByUserNumAsync(user, ct).ConfigureAwait(false) : null;
        var stats = account.UserNumber is { } id && progress is not null ? await progress.FindAsync(id, ct).ConfigureAwait(false) : null;
        return new { account.Username, displayName = account.Nickname ?? account.Username,
            stats = new Dictionary<string, long?> { ["level"] = stats?.Level, ["exp"] = stats?.Exp, ["lucci"] = wallet?.Lucci, ["cash"] = wallet?.Cash },
            levelIconUrl = stats is null || options.LevelIconTemplate.Length == 0 ? null : options.LevelIconTemplate.Replace("{level}", stats.Level.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) };
    }
    private static async Task<LauncherAccount> Authorized(HttpContext context, ILauncherAccountRepository accounts, LauncherSessions sessions)
    {
        var token = Bearer(context); var name = sessions.Account(token) ?? throw new UnauthorizedAccessException();
        var account = await accounts.FindAsync(name, context.RequestAborted).ConfigureAwait(false);
        if (account is null || account.BannedUntil > DateTime.UtcNow)
        {
            await sessions.SignOutAsync(null, token, context.RequestAborted).ConfigureAwait(false);
            throw new LauncherApiFailure(401, account is null ? "unauthorized" : "account_banned");
        }
        return account;
    }
    private static string Bearer(HttpContext context) => OptionalBearer(context) ?? throw new UnauthorizedAccessException();
    private static string? OptionalBearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && header.Length <= 4103 ? header[7..] : null;
    }
    private static IResult Grant(LauncherGrant grant, object profile, LauncherClientPolicy policy) => Results.Json(new
    {
        accessToken = grant.AccessToken, accessExpiresAt = grant.AccessExpiresAt.UtcDateTime,
        refreshToken = grant.RefreshToken, refreshExpiresAt = grant.RefreshExpiresAt.UtcDateTime, account = profile,
        policy.MinimumLauncherVersion, clientPolicy = policy.ClientPolicy,
    });
    private static string RefreshToken(JsonElement body)
    {
        // A malformed credential is refused like an unknown one, not as a malformed request.
        if (!body.TryGetProperty("refreshToken", out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        return value.GetString()!;
    }
    private static async Task<JsonElement> Body(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 8 }, context.RequestAborted).ConfigureAwait(false);
        var root = document.RootElement;
        // New launchers may add fields; handlers validate only the fields they consume.
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) throw new InvalidDataException();
        return root.Clone();
    }
    private static string Username(JsonElement body)
    {
        if (!body.TryGetProperty("username", out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var name = value.GetString()!.Trim().ToLowerInvariant();
        return name.Length is >= 3 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? name : throw new InvalidDataException();
    }
    private static string Password(JsonElement body)
    {
        if (!body.TryGetProperty("password", out var value) || value.ValueKind != JsonValueKind.String || value.GetString()!.Length == 0) throw new InvalidDataException();
        return value.GetString()!;
    }
    private static string HashPassword(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    private static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations) || iterations is < 100000 or > 2000000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]); var expected = Convert.FromBase64String(parts[3]);
            return salt.Length == 16 && expected.Length == 32 && CryptographicOperations.FixedTimeEquals(expected, Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32));
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Stops request processing before releasing the concurrency limiter.</summary>
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ShutdownSeconds));
        try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally { await _app.DisposeAsync().ConfigureAwait(false); _workers.Dispose(); }
    }
}
