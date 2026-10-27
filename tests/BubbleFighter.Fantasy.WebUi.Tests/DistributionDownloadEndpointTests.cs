using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Admin.Http;
using BubbleFighter.Fantasy.Contracts.Distribution;
using BubbleFighter.Fantasy.Distribution;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed partial class DistributionHttpTests
{
    [Fact]
    public async Task HttpDownloadJobUsesRealSupervisionAndStagesVerifiedBytesWithoutReplacingTheApplication()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-distribution-endpoint-" + Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("verified staged application file");
        var manifest = Encoding.UTF8.GetBytes($$"""
            schema = 1
            [release]
            id = "example-server"
            game = "bubblefighter"
            version = "1.0.0"
            builds = ["kr-1256"]
            [[files]]
            path = "bff.exe"
            source_path = "objects/server.bin"
            size = {{bytes.Length}}
            sha256 = "{{Hash(bytes)}}"
            """);
        var catalog = Encoding.UTF8.GetBytes($$"""
            schema = 2
            [catalog]
            id = "local"
            [[packages]]
            id = "example-server"
            kind = "server"
            game = "bubblefighter"
            version = "1.0.0"
            builds = ["kr-1256"]
            size = {{bytes.Length}}
            manifest = "releases/server.toml"
            manifest_sha256 = "{{Hash(manifest)}}"
            """);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var source = builder.Build();
        source.MapGet("/{**path}", async (HttpContext context, string path) =>
        {
            var content = path switch { "source.toml" => catalog, "releases/server.toml" => manifest, "objects/server.bin" => bytes,
                _ => throw new InvalidOperationException("Unexpected source path.") };
            if (path == "objects/server.bin")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }
            context.Response.ContentLength = content.Length;
            await context.Response.Body.WriteAsync(content, context.RequestAborted);
        });
        await source.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var sourceAddress = source.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + "/";
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
            using var save = await http.PutAsJsonAsync("api/distribution/v1/sources", new DistributionSourceSettings(0,
                [new("local", "Local", "http", sourceAddress, "source.toml")]));
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            var store = await http.GetFromJsonAsync<DistributionStoreSnapshot>("api/distribution/v1/catalog");
            Assert.NotNull(store);
            var choice = Assert.Single(Assert.Single(store.Entries).Downloads);
            using var select = await http.PostAsJsonAsync("api/distribution/v1/plans", new DistributionPlanRequest(store.SourceRevision,
                choice.SourceId, choice.Package.Identity, choice.SelectionFingerprint, null));
            select.EnsureSuccessStatusCode();
            var plan = await select.Content.ReadFromJsonAsync<DistributionPlan>();
            Assert.NotNull(plan);
            using var begin = await http.PostAsJsonAsync("api/distribution/v1/jobs", new { planId = plan.Id });
            Assert.Equal(HttpStatusCode.Accepted, begin.StatusCode);
            var accepted = await begin.Content.ReadFromJsonAsync<DistributionDownloadJob>();
            Assert.NotNull(accepted);
            Assert.True(File.Exists(Path.Combine(directory, "staging", accepted.Id, "receipt.json")));
            Assert.DoesNotContain(directory, await begin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var running = await http.GetFromJsonAsync<DistributionDownloadJob>("api/distribution/v1/jobs/" + accepted.Id);
            Assert.Equal("transferring", running!.Phase);
            var activePage = await http.GetFromJsonAsync<DistributionDownloadJobPage>("api/distribution/v1/jobs");
            Assert.NotNull(activePage);
            Assert.Equal(accepted.Id, Assert.Single(activePage.Entries).Id);
            Assert.Null(activePage.NextCursor);
            using var removeActive = await http.DeleteAsync("api/distribution/v1/jobs/" + accepted.Id);
            Assert.Equal(HttpStatusCode.Conflict, removeActive.StatusCode);
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            DistributionDownloadJob completed;
            do
            {
                completed = (await http.GetFromJsonAsync<DistributionDownloadJob>("api/distribution/v1/jobs/" + accepted.Id, timeout.Token))!;
                if (completed.Phase is "failed" or "cancelled" or "staged") break;
                await Task.Delay(10, timeout.Token);
            } while (true);
            Assert.Equal("staged", completed.Phase);
            Assert.Equal(bytes.Length, completed.BytesReceived);
            var staged = Path.Combine(directory, "staging", completed.Id);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(staged, "objects", "00000000.blob")));
            Assert.False(File.Exists(Path.Combine(directory, "bff.exe")));
            using var recheck = await http.PostAsync("api/distribution/v1/jobs/" + completed.Id + "/verify", null);
            recheck.EnsureSuccessStatusCode();
            var verification = await recheck.Content.ReadFromJsonAsync<DistributionStagingVerification>();
            Assert.Equal(completed.Id, verification!.JobId);
            Assert.Equal(bytes.Length, verification.Bytes);
            await File.WriteAllBytesAsync(Path.Combine(staged, "objects", "00000000.blob"), new byte[bytes.Length]);
            using var corrupt = await http.PostAsync("api/distribution/v1/jobs/" + completed.Id + "/verify", null);
            Assert.Equal(HttpStatusCode.Conflict, corrupt.StatusCode);
            var finishedPage = await http.GetFromJsonAsync<DistributionDownloadJobPage>("api/distribution/v1/jobs");
            Assert.Equal("staged", Assert.Single(finishedPage!.Entries).Phase);
            using var remove = await http.DeleteAsync("api/distribution/v1/jobs/" + completed.Id);
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
            Assert.False(Directory.Exists(staged));
            var emptyPage = await http.GetFromJsonAsync<DistributionDownloadJobPage>("api/distribution/v1/jobs");
            Assert.Empty(emptyPage!.Entries);
        }
        finally
        {
            release.TrySetResult();
            await host.ShutdownAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var absolute = Path.GetFullPath(directory);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
