using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class GithubMirrorTests : IDisposable
{
    private const string Probe = "https://github.com/owner/server/releases/download/v1/server.zip";
    private const string AssetUrl = "https://github.com/owner/plugin/releases/download/v1/plugin.zip";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("PK\u0003\u0004 verified plugin payload");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-github-mirror-" + Guid.NewGuid().ToString("N"));

    private static GithubMirrorConfiguration Configuration(string? country = null) => new(new Uri(Probe), [0x50, 0x4B, 0x03, 0x04], 16,
        TimeSpan.FromSeconds(5), 4, null, country is null ? null : new Uri("https://lookup.example/"),
        [(new("fast", "fast"), new Uri("https://fast.example/"), false), (new("broken", "broken"), new Uri("https://broken.example/"), false)]);

    [Fact]
    public async Task MeasurementCountsOnlyMirrorsThatReturnTheKnownFile()
    {
        using var mirrors = new GithubMirrors(Configuration(), null, new Handler(request => request.RequestUri!.Host == "fast.example"
            ? Bytes(Payload) : new(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>", Encoding.UTF8, "text/html") }));
        await mirrors.MeasureAsync(default);
        Assert.Equal("available", mirrors.Results.Single(row => row.Id == "fast").State);
        Assert.Equal("failed", mirrors.Results.Single(row => row.Id == "broken").State);
    }

    [Fact]
    public async Task PayloadsTryTheMirrorThenGithubAndAlwaysMatchGithubsDigest()
    {
        var requested = new List<string>();
        var tampered = Encoding.UTF8.GetBytes("PK\u0003\u0004 a different payload!!");
        var handler = new Handler(request =>
        {
            lock (requested) requested.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri!.Host switch
            {
                "fast.example" when request.Headers.Range is not null => Bytes(Payload),
                "fast.example" => Bytes(tampered),
                "github.com" => Bytes(Payload),
                _ => new(HttpStatusCode.BadGateway),
            };
        });
        using var mirrors = new GithubMirrors(Configuration(), Path.Combine(_directory, "mirror.json"), handler);
        mirrors.Save(true, "auto");
        using var http = new HttpClient(handler);
        var sources = new DistributionSources(http, new(), mirrors: mirrors);
        using var staging = new MemoryStream();
        await sources.CopyRepositoryAssetAsync(new(7, "plugin.zip", Payload.Length, AssetUrl, Convert.ToHexStringLower(SHA256.HashData(Payload))), staging);
        // The mirror's bytes did not match GitHub's digest, so they were discarded and GitHub served the file.
        Assert.Equal(Payload, staging.ToArray());
        Assert.Contains("https://fast.example/" + AssetUrl, requested);
        Assert.Equal(AssetUrl, requested.Last());
        Assert.DoesNotContain(requested, url => url.StartsWith("https://broken.example/" + AssetUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MetadataIsNeverMirrored()
    {
        var hosts = new List<string>();
        var handler = new Handler(request => { lock (hosts) hosts.Add(request.RequestUri!.Host); return new(HttpStatusCode.NotFound); });
        using var mirrors = new GithubMirrors(Configuration(), null, handler);
        mirrors.Save(true, "fast");
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new DistributionSources(http, new(), mirrors: mirrors).ReadRepositoryReleaseAsync("https://github.com/owner/plugin").AsTask());
        Assert.Equal(["api.github.com"], hosts.Distinct());
    }

    [Fact]
    public async Task AMainlandChinaAddressQueuesOneSuggestionAndTheChoiceSurvivesRestart()
    {
        var path = Path.Combine(_directory, "mirror.json");
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"country_code\":\"cn\"}") });
        using (var mirrors = new GithubMirrors(Configuration("cn"), path, handler))
        {
            await mirrors.DetectCountryAsync(default);
            Assert.True(mirrors.State.SuggestionPending);
            Assert.False(mirrors.State.Enabled);
            Assert.False(mirrors.MarkSuggestionShown().SuggestionPending);
            mirrors.Save(true, "fast");
            Assert.Throws<ArgumentException>(() => mirrors.Save(null, "unknown.example"));
        }
        using var reopened = new GithubMirrors(Configuration("cn"), path, new Handler(_ => throw new InvalidOperationException("No second lookup.")));
        await reopened.DetectCountryAsync(default);
        Assert.Equal(new GithubMirrorState(true, "fast", false, false), reopened.State);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private static HttpResponseMessage Bytes(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
}
