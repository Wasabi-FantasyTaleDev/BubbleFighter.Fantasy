using System.Net;
using System.Net.Http.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Admin.Http;
using BubbleFighter.Fantasy.Contracts.Distribution;
using BubbleFighter.Fantasy.Distribution;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed partial class AdminHttpEndpointTests
{
    [Fact]
    public async Task MissingDistributionPluginReturnsUnavailableInsteadOfAnEmptyCatalog()
    {
        using var sources = await _http.GetAsync("/api/distribution/v1/sources");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sources.StatusCode);
        using var release = await _http.PostAsJsonAsync("/api/distribution/v1/repository-release",
            new { repositoryUrl = "https://github.com/owner/plugin" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, release.StatusCode);
        Assert.Contains("distribution_unavailable", await release.Content.ReadAsStringAsync());
        using var plan = await _http.PostAsJsonAsync("/api/distribution/v1/plans",
            new DistributionPlanRequest(0, "community", new("server", "plugin", "bubblefighter", "example"), new string('a', 64), null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, plan.StatusCode);
        using var files = await _http.PostAsync("/api/distribution/v1/plans/missing/files", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, files.StatusCode);
        using var job = await _http.PostAsJsonAsync("/api/distribution/v1/jobs", new { planId = "missing" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, job.StatusCode);
    }


}

public sealed partial class DistributionHttpTests
{
    [Fact]
    public async Task SourceEditorPersistsViaHttpApiAndRejectsStaleEdits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-distribution-http-" + Guid.NewGuid().ToString("N"));
        using var addresses = new AddressLog();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(addresses));
        var host = new PluginHost([
            new() { Id = "sources", Plugin = "distribution.sources", Source = "test", Config = new Dictionary<string, object?>
                { ["state_path"] = Path.Combine(directory, "sources.json") } },
            new() { Id = "plans", Plugin = "distribution.plans", Source = "test" },
            new() { Id = "packages", Plugin = "distribution.packages", Source = "test" },
            new() { Id = "jobs", Plugin = "distribution.jobs", Source = "test", Config = new Dictionary<string, object?>
                { ["staging_path"] = Path.Combine(directory, "staging") } },
            new() { Id = "web", Plugin = "webui", Source = "test" },
            new() { Id = "admin", Plugin = "admin.http", Source = "test", Config = new Dictionary<string, object?> { ["listen"] = "127.0.0.1:0" } }
        ], PluginCatalog.ScanAssemblies([typeof(AdminHttpPlugin).Assembly, typeof(WebUiPlugin).Assembly, typeof(DistributionPlugin).Assembly]), logging);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            using var http = new HttpClient { BaseAddress = new Uri(await addresses.Address.Task.WaitAsync(TimeSpan.FromSeconds(10))) };
            var initial = await http.GetFromJsonAsync<DistributionSourceSettings>("api/distribution/v1/sources");
            Assert.NotNull(initial);
            Assert.Equal(0, initial.Revision);
            var source = new DistributionSource("community", "Community", "github", "https://github.com/owner/store/", "source.toml");
            using var save = await http.PutAsJsonAsync("api/distribution/v1/sources", new DistributionSourceSettings(0, [source]));
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            var updated = await http.GetFromJsonAsync<DistributionSourceSettings>("api/distribution/v1/sources");
            Assert.Equal(1, updated!.Revision);
            Assert.Equal(source, Assert.Single(updated.Sources));
            using var conflict = await http.PutAsJsonAsync("api/distribution/v1/sources", new DistributionSourceSettings(0, []));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var stalePlan = await http.PostAsJsonAsync("api/distribution/v1/plans",
                new DistributionPlanRequest(0, "community", new("server", "plugin", "bubblefighter", "example"), new string('a', 64), null));
            Assert.Equal(HttpStatusCode.Conflict, stalePlan.StatusCode);
            Assert.Contains("distribution_source_changed", await stalePlan.Content.ReadAsStringAsync());
            using var clientPlan = await http.PostAsJsonAsync("api/distribution/v1/plans",
                new DistributionPlanRequest(1, "community", new("client", "plugin", "bubblefighter", "example"), new string('a', 64), null));
            Assert.Equal(HttpStatusCode.BadRequest, clientPlan.StatusCode);
            using var absentPlan = await http.PostAsync("api/distribution/v1/plans/missing/validate", null);
            Assert.Equal(HttpStatusCode.NotFound, absentPlan.StatusCode);
            using var absentFiles = await http.PostAsync("api/distribution/v1/plans/missing/files", null);
            Assert.Equal(HttpStatusCode.NotFound, absentFiles.StatusCode);
            using var absentJobPlan = await http.PostAsJsonAsync("api/distribution/v1/jobs", new { planId = "missing" });
            Assert.Equal(HttpStatusCode.NotFound, absentJobPlan.StatusCode);
            using var invalidJob = await http.GetAsync("api/distribution/v1/jobs/invalid");
            Assert.Equal(HttpStatusCode.BadRequest, invalidJob.StatusCode);
            using var missingJob = await http.GetAsync("api/distribution/v1/jobs/" + new string('a', 32));
            Assert.Equal(HttpStatusCode.NotFound, missingJob.StatusCode);
            using var deletedPlan = await http.DeleteAsync("api/distribution/v1/plans/missing");
            Assert.Equal(HttpStatusCode.NotFound, deletedPlan.StatusCode);
            using var invalid = await http.PostAsJsonAsync("api/distribution/v1/repository-release", new { repositoryUrl = "http://not-github.example/plugin" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("community", await File.ReadAllTextAsync(Path.Combine(directory, "sources.json")));
        }
        finally
        {
            await host.ShutdownAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class AddressLog : ILoggerProvider, ILogger
    {
        internal readonly TaskCompletionSource<string> Address = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.StartsWith("WebUI: ", StringComparison.Ordinal)) Address.TrySetResult(text[7..]);
        }
        public void Dispose() { }
    }
}
