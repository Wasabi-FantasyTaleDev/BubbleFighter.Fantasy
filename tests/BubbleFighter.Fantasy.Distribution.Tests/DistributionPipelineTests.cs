using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionPipelineTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("alist")]
    [InlineData("github")]
    public async Task CatalogSelectionManifestAndVerifiedPayloadUseTheSameTransport(string kind)
    {
        var payload = Encoding.UTF8.GetBytes("server file");
        var manifest = Encoding.UTF8.GetBytes($$"""
            schema = 1
            [release]
            id = "server"
            game = "bubblefighter"
            version = "1.0.0"
            builds = ["kr-1256"]
            [[files]]
            path = "bff.bin"
            source_path = "objects/server.bin"
            size = {{payload.Length}}
            sha256 = "{{Hash(payload)}}"
            """);
        var catalog = Encoding.UTF8.GetBytes($$"""
            schema = 2
            [catalog]
            id = "store"
            [[packages]]
            id = "server"
            kind = "server"
            game = "bubblefighter"
            version = "1.0.0"
            revision = 1
            builds = ["kr-1256"]
            size = {{payload.Length}}
            manifest = "releases/server.toml"
            manifest_sha256 = "{{Hash(manifest)}}"
            """);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { ["source.toml"] = catalog, ["releases/server.toml"] = manifest, ["objects/server.bin"] = payload };
        var payloadRequests = 0;
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("alist", kind);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
                var path = body.RootElement.GetProperty("path").GetString()!.TrimStart('/');
                Assert.True(files.TryGetValue(path, out var content));
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    { code = 200, data = new { is_dir = false, size = content.Length, raw_url = "https://objects.example/" + path } })) };
            }
            var key = files.Keys.Single(path => request.RequestUri!.AbsolutePath.EndsWith("/" + path, StringComparison.Ordinal));
            if (key == "objects/server.bin") payloadRequests++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(files[key]) };
        });
        using var http = new HttpClient(handler);
        var sources = new DistributionSources(http, new() { StatePath = "", Sources = [new()
            { Id = "chosen", Name = "Chosen", Kind = kind, BaseUrl = kind == "github" ? "https://github.com/owner/store/" : "https://files.example/" }] });
        var snapshot = await sources.BrowseAsync();
        Assert.Empty(snapshot.Failures);
        var choice = Assert.Single(Assert.Single(snapshot.Entries).Downloads);
        using var plans = new DistributionPlans(sources, new());
        var selection = await plans.CreateAsync(new(snapshot.SourceRevision, choice.SourceId, choice.Package.Identity, choice.SelectionFingerprint, null));
        var packages = new DistributionPackages(plans, sources, new());
        var prepared = await packages.PrepareAsync(selection.Id);
        Assert.Equal(0, payloadRequests);
        Assert.Equal(Hash(manifest), prepared.ManifestSha256);
        Assert.Equal("server-application-schema-1", prepared.Format);
        using var staging = new MemoryStream();
        var file = Assert.Single(prepared.Files);
        await sources.CopyFileAsync(prepared.Selection.Source, file.SourcePath!, file.Size, file.Sha256, staging);
        Assert.Equal(1, payloadRequests);
        Assert.Equal(payload, staging.ToArray());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
}
