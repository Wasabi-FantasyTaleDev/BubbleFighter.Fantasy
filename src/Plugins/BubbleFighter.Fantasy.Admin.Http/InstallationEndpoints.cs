using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Installation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Admin.Http;

public sealed partial class AdminHttpPlugin
{
    private const string SessionCookie = "bff_admin_session";

    private static void ConfigureInstallation(WebApplication app, IAdminAccessControl access, IInstallationService? installation,
        AdminHttpOptions options, IPEndPoint endpoint)
    {
        var allowedHosts = options.AllowedHosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        allowedHosts.Add(endpoint.Address.ToString());
        if (IPAddress.IsLoopback(endpoint.Address))
        { allowedHosts.Add("localhost"); allowedHosts.Add("127.0.0.1"); allowedHosts.Add("::1"); }

        app.Use(async (http, next) =>
        {
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers["X-Frame-Options"] = "DENY";
            if (!allowedHosts.Contains(http.Request.Host.Host))
            { http.Response.StatusCode = 400; return; }
            if (!http.Request.Path.StartsWithSegments("/api")) { await next().ConfigureAwait(false); return; }
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Method is not ("GET" or "HEAD"))
            {
                var origin = http.Request.Headers.Origin.ToString();
                if (http.Request.Headers["X-BFF-Request"] != "1" ||
                    (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var source) ||
                        !string.Equals(source.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase) ||
                        source.Scheme != http.Request.Scheme)))
                { http.Response.StatusCode = 403; return; }
            }
            var path = http.Request.Path.Value;
            // A local proxy must not turn a remote request into a trusted first-run connection.
            if (!access.HasAdministrator && http.Request.Path.StartsWithSegments("/api/setup") &&
                new[] { "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded" }
                    .Any(http.Request.Headers.ContainsKey))
            { await RejectAsync(http, 403, "setup_proxy_refused").ConfigureAwait(false); return; }
            var publicAuth = path is "/api/auth/status" or "/api/auth/login";
            var freshSetup = !access.HasAdministrator && (path is "/api/setup/status" or "/api/setup/probe" or "/api/setup/complete" or "/api/setup/directories" or "/api/setup/client-data");
            if (freshSetup && !(http.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)) &&
                !access.AcceptsBootstrapToken(http.Request.Headers["X-BFF-Setup-Token"].ToString()))
            { await RejectAsync(http, 403, "bootstrap_token_required").ConfigureAwait(false); return; }
            // An installation signs administrators in by account and password only; a bearer token is not accepted here.
            if (!publicAuth && !freshSetup && !access.Authorize(http.Request.Cookies[SessionCookie]))
            { await RejectAsync(http, 401, "unauthorized").ConfigureAwait(false); return; }
            try { await next().ConfigureAwait(false); }
            catch (Exception error) when (!http.RequestAborted.IsCancellationRequested &&
                (path?.StartsWith("/api/setup/", StringComparison.Ordinal) == true ||
                 path?.StartsWith("/api/auth/", StringComparison.Ordinal) == true))
            {
                if (http.Response.HasStarted) throw;
                var code = error switch
                {
                    BadHttpRequestException e when e.StatusCode == 413 => "client_data_limit",
                    ArgumentException e when e.Message is "directory_unavailable" or "client_data_invalid" or "client_data_limit" or "client_data_exists" => e.Message,
                    ArgumentException e when e.Message.StartsWith("client_data_missing:", StringComparison.Ordinal) => e.Message,
                    InvalidOperationException e when e.Message == "client_data_busy" => e.Message,
                    ArgumentException => "configuration_invalid",
                    InvalidOperationException e when e.Message is "login_rate_limited" or "login_busy" or "session_capacity" => e.Message,
                    InvalidOperationException e when e.Message is "administrator_already_claimed" or "operation_busy" or "retry_unavailable" => e.Message,
                    InvalidOperationException e when e.Message == "database_failed" => "database_failed",
                    _ => "operation_failed",
                };
                var status = error is BadHttpRequestException requestError ? requestError.StatusCode :
                    error is ArgumentException ? 400 : code is "login_rate_limited" or "login_busy" ? 429 : 409;
                await RejectAsync(http, status, code).ConfigureAwait(false);
            }
        });

        app.MapGet("/api/auth/status", (HttpContext http) => Results.Ok(new
        {
            mode = "account", installation = installation is not null, hasAdministrator = access.HasAdministrator,
            authenticated = access.Authorize(http.Request.Cookies[SessionCookie]),
        }));
        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (http.Connection.RemoteIpAddress is not { } sourceAddress)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            var session = await access.SignInAsync(request.Username, request.Password, sourceAddress.ToString(), ct).ConfigureAwait(false);
            if (session is null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            http.Response.Cookies.Append(SessionCookie, session.Token, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps,
                Path = "/api", Expires = session.ExpiresUtc, IsEssential = true,
            });
            return Results.Ok(new { authenticated = true, session.ExpiresUtc });
        });
        app.MapPost("/api/auth/logout", (HttpContext http) =>
        {
            access.SignOut(http.Request.Cookies[SessionCookie]);
            http.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/api", SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps });
            return Results.Ok(new { authenticated = false });
        });
        if (installation is null) return;
        app.MapGet("/api/setup/status", () => Results.Ok(installation.Status));
        app.MapPost("/api/setup/directories", (DirectoryRequest request, CancellationToken ct) =>
            Results.Ok(installation.BrowseDirectories(request.Path, ct)));
        app.MapPost("/api/setup/client-data", async (HttpContext http, CancellationToken ct) =>
        {
            var request = http.Request;
            var profile = request.Query["profile"].ToString();
            var kind = request.Query["kind"].ToString();
            if (kind is not ("folder" or "zip") || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
                || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("client_data_invalid");
            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new ArgumentException("client_data_invalid");
            if (request.ContentLength > installation.ClientDataUploadLimit) throw new BadHttpRequestException("client_data_limit", 413);
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = installation.ClientDataUploadLimit;
            var reader = new MultipartReader(boundary, request.Body)
            {
                HeadersCountLimit = 8, HeadersLengthLimit = 8192, BodyLengthLimit = installation.ClientDataUploadLimit,
            };
            var root = await installation.ImportClientDataAsync(profile, kind == "zip", ReadUploadsAsync(reader, ct), ct).ConfigureAwait(false);
            return Results.Ok(new { path = root });
        });
        app.MapGet("/api/setup/configuration", () => Results.Ok(installation.GetConfiguration()));
        app.MapPost("/api/setup/configuration/probe", async (InstallationUpdate update, CancellationToken ct) =>
        {
            await installation.ProbeUpdateAsync(update, ct).ConfigureAwait(false);
            return Results.Ok(new { connected = true });
        });
        app.MapPost("/api/setup/probe", async (InstallationSettings settings, CancellationToken ct) =>
        {
            await installation.ProbeAsync(settings, ct).ConfigureAwait(false);
            return Results.Ok(new { connected = true });
        });
        app.MapPost("/api/setup/complete", async (InstallationRequest request, CancellationToken ct) =>
        {
            await installation.BeginAsync(request, ct).ConfigureAwait(false);
            return Results.Accepted(value: new { request.OperationId });
        });
        app.MapPost("/api/setup/retry", async (CancellationToken ct) =>
        {
            await installation.RetryAsync(ct).ConfigureAwait(false);
            return Results.Accepted();
        });
        app.MapPut("/api/setup/configuration", async (InstallationUpdate update, CancellationToken ct) =>
        {
            await installation.UpdateAsync(update, ct).ConfigureAwait(false);
            return Results.Accepted();
        });
    }

    private static async IAsyncEnumerable<InstallationUpload> ReadUploadsAsync(MultipartReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.ReadNextSectionAsync(ct).ConfigureAwait(false) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                || HeaderUtilities.RemoveQuotes(disposition.Name).Value != "files") throw new ArgumentException("client_data_invalid");
            var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("client_data_invalid");
            yield return new InstallationUpload(name, section.Body);
        }
    }

    private static Task RejectAsync(HttpContext http, int status, string code)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { error = code });
    }

    private sealed record LoginRequest(string Username, string Password);
    private sealed record DirectoryRequest(string? Path);
}
