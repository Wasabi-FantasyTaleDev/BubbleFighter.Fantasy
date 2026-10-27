using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BubbleFighter.Fantasy.Admin.Http;

public sealed partial class AdminHttpPlugin
{
    private static void ConfigureDistribution(WebApplication app, IPluginContext context)
    {
        app.MapGet("/api/distribution/v1/sources", () =>
        {
            if (!context.Services.TryGet<IDistributionSources>(out var sources) || sources is null)
                return Results.Json(new { error = "distribution_unavailable" }, statusCode: 503);
            var settings = sources.Settings;
            return Results.Ok(new { settings.Sources, settings.Revision, githubMirrorAvailable = false });
        });

        app.MapPut("/api/distribution/v1/sources", async (SourceSettingsRequest request, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionSources>(out var sources) || sources is null)
                return Results.Json(new { error = "distribution_unavailable" }, statusCode: 503);
            if (request.Sources is null) return Results.Json(new { error = "source_settings_invalid" }, statusCode: 400);
            try { return Results.Ok(await sources.SaveSourcesAsync(request.Revision, request.Sources, ct).ConfigureAwait(false)); }
            catch (ArgumentException) { return Results.Json(new { error = "source_settings_invalid" }, statusCode: 400); }
            catch (InvalidOperationException) { return Results.Json(new { error = "source_revision_conflict" }, statusCode: 409); }
            catch (NotSupportedException) { return Results.Json(new { error = "source_editing_disabled" }, statusCode: 409); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "source_settings_write_failed" }, statusCode: 503); }
        });

        app.MapPost("/api/distribution/v1/repository-release", async (RepositoryReleaseRequest request, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionSources>(out var sources) || sources is null)
                return Results.Json(new { error = "distribution_unavailable" }, statusCode: 503);
            try
            {
                return Results.Ok(await sources.ReadRepositoryReleaseAsync(request.RepositoryUrl, request.Tag, ct).ConfigureAwait(false));
            }
            catch (ArgumentException) { return Results.Json(new { error = "repository_invalid" }, statusCode: 400); }
            catch (HttpRequestException error)
            {
                var status = (int?)error.StatusCode;
                return Results.Json(new { error = status == 404 ? "release_not_found" : status is 403 or 429 ? "repository_rate_limited" : "repository_unavailable" }, statusCode: 502);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { return Results.Json(new { error = "release_metadata_invalid" }, statusCode: 502); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return Results.Json(new { error = "repository_timeout" }, statusCode: 504); }
        });

        app.MapGet("/api/distribution/v1/catalog", async (string? sourceId, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionSources>(out var sources) || sources is null)
                return Results.Json(new { error = "distribution_unavailable" }, statusCode: 503);
            try { return Results.Ok(await sources.BrowseAsync(sourceId, ct).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { return Results.Json(new { error = "source_not_found" }, statusCode: 404); }
        });

        app.MapPost("/api/distribution/v1/plans", async (DistributionPlanRequest request, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionPlans>(out var plans) || plans is null)
                return Results.Json(new { error = "distribution_plans_unavailable" }, statusCode: 503);
            try { return Results.Ok(await plans.CreateAsync(request, ct).ConfigureAwait(false)); }
            catch (ArgumentException) { return Results.Json(new { error = "distribution_selection_invalid" }, statusCode: 400); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("distribution_", StringComparison.Ordinal))
            { return Results.Json(new { error = error.Message }, statusCode: 409); }
        });

        app.MapPost("/api/distribution/v1/plans/{planId}/validate", async (string planId, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionPlans>(out var plans) || plans is null)
                return Results.Json(new { error = "distribution_plans_unavailable" }, statusCode: 503);
            try { return Results.Ok(await plans.ValidateAsync(planId, ct).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { return Results.Json(new { error = "distribution_plan_missing" }, statusCode: 404); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("distribution_", StringComparison.Ordinal))
            { return Results.Json(new { error = error.Message }, statusCode: 409); }
        });

        app.MapDelete("/api/distribution/v1/plans/{planId}", (string planId) =>
        {
            if (!context.Services.TryGet<IDistributionPlans>(out var plans) || plans is null)
                return Results.Json(new { error = "distribution_plans_unavailable" }, statusCode: 503);
            return plans.Remove(planId) ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/distribution/v1/plans/{planId}/files", async (string planId, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionPackages>(out var packages) || packages is null)
                return Results.Json(new { error = "distribution_packages_unavailable" }, statusCode: 503);
            try { return Results.Ok(await packages.PrepareAsync(planId, ct).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { return Results.Json(new { error = "distribution_plan_missing" }, statusCode: 404); }
            catch (NotSupportedException) { return Results.Json(new { error = "distribution_release_format_unavailable" }, statusCode: 409); }
            catch (InvalidDataException) { return Results.Json(new { error = "distribution_manifest_invalid" }, statusCode: 502); }
            catch (ArgumentException) { return Results.Json(new { error = "distribution_selection_invalid" }, statusCode: 400); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("distribution_", StringComparison.Ordinal))
            { return Results.Json(new { error = error.Message }, statusCode: 409); }
            catch (HttpRequestException) { return Results.Json(new { error = "distribution_manifest_unavailable" }, statusCode: 502); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return Results.Json(new { error = "distribution_manifest_timeout" }, statusCode: 504); }
        });

        app.MapGet("/api/distribution/v1/jobs", (string? after) =>
        {
            if (!context.Services.TryGet<IDistributionJobs>(out var jobs) || jobs is null)
                return Results.Json(new { error = "distribution_jobs_unavailable" }, statusCode: 503);
            try { return Results.Ok(jobs.List(after)); }
            catch (ArgumentException) { return Results.Json(new { error = "distribution_job_cursor_invalid" }, statusCode: 400); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { return Results.Json(new { error = "distribution_job_storage_failed" }, statusCode: 503); }
        });

        app.MapPost("/api/distribution/v1/jobs", async (DownloadJobRequest request, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionJobs>(out var jobs) || jobs is null)
                return Results.Json(new { error = "distribution_jobs_unavailable" }, statusCode: 503);
            try
            {
                var job = await jobs.StartAsync(request.PlanId, ct).ConfigureAwait(false);
                return Results.Accepted("/api/distribution/v1/jobs/" + job.Id, job);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "distribution_job_storage_failed" }, statusCode: 503); }
            catch (KeyNotFoundException) { return Results.Json(new { error = "distribution_plan_missing" }, statusCode: 404); }
            catch (ArgumentException) { return Results.Json(new { error = "distribution_selection_invalid" }, statusCode: 400); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("distribution_", StringComparison.Ordinal))
            { return Results.Json(new { error = error.Message }, statusCode: 409); }
        });

        app.MapGet("/api/distribution/v1/jobs/{jobId}", (string jobId) => ReadJob(jobId, cancel: false));
        app.MapPost("/api/distribution/v1/jobs/{jobId}/cancel", (string jobId) => ReadJob(jobId, cancel: true));
        app.MapPost("/api/distribution/v1/jobs/{jobId}/verify", async (string jobId, CancellationToken ct) =>
        {
            if (!context.Services.TryGet<IDistributionJobs>(out var jobs) || jobs is null)
                return Results.Json(new { error = "distribution_jobs_unavailable" }, statusCode: 503);
            try { return Results.Ok(await jobs.VerifyAsync(jobId, ct).ConfigureAwait(false)); }
            catch (ArgumentException) { return Results.Json(new { error = "distribution_job_invalid" }, statusCode: 400); }
            catch (KeyNotFoundException) { return Results.Json(new { error = "distribution_job_missing" }, statusCode: 404); }
            catch (InvalidOperationException error) when (error.Message == "distribution_job_not_staged")
            { return Results.Json(new { error = error.Message }, statusCode: 409); }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            { return Results.Json(new { error = "distribution_staging_integrity_failed" }, statusCode: 409); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "distribution_job_storage_failed" }, statusCode: 503); }
        });
        app.MapDelete("/api/distribution/v1/jobs/{jobId}", (string jobId) =>
        {
            if (!context.Services.TryGet<IDistributionJobs>(out var jobs) || jobs is null)
                return Results.Json(new { error = "distribution_jobs_unavailable" }, statusCode: 503);
            try { return jobs.Remove(jobId) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException) { return Results.Json(new { error = "distribution_job_invalid" }, statusCode: 400); }
            catch (InvalidOperationException error) when (error.Message is "distribution_job_active" or "distribution_job_in_use")
            { return Results.Json(new { error = error.Message }, statusCode: 409); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { return Results.Json(new { error = "distribution_job_storage_failed" }, statusCode: 503); }
        });

        IResult ReadJob(string jobId, bool cancel)
        {
            if (!context.Services.TryGet<IDistributionJobs>(out var jobs) || jobs is null)
                return Results.Json(new { error = "distribution_jobs_unavailable" }, statusCode: 503);
            try { return Results.Ok(cancel ? jobs.Cancel(jobId) : jobs.Get(jobId)); }
            catch (KeyNotFoundException) { return Results.Json(new { error = "distribution_job_missing" }, statusCode: 404); }
            catch (ArgumentException) { return Results.Json(new { error = "distribution_job_invalid" }, statusCode: 400); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { return Results.Json(new { error = "distribution_job_storage_failed" }, statusCode: 503); }
        }
    }

    private sealed record RepositoryReleaseRequest(string RepositoryUrl, string? Tag);
    private sealed record SourceSettingsRequest(long Revision, IReadOnlyList<DistributionSource>? Sources);
    private sealed record DownloadJobRequest(string PlanId);
}
