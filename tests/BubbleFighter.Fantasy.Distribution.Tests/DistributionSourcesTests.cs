using System.Net;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionSourcesTests
{
    [Theory]
    [InlineData("http", "https://files.example.org/catalogs/", "https://files.example.org/catalogs/source.toml")]
    [InlineData("github", "https://github.com/owner/plugins", "https://raw.githubusercontent.com/owner/plugins/HEAD/source.toml")]
    [InlineData("github", "https://github.com/owner/plugins/tree/main/store/", "https://raw.githubusercontent.com/owner/plugins/main/store/source.toml")]
    public async Task CatalogAdaptersResolveTheSameRelativeCatalog(string kind, string root, string expected)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(expected, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            return Reply("schema = 2");
        });
        using var http = new HttpClient(handler);
        var service = Create(http, kind, root);
        Assert.Equal("schema = 2", Encoding.UTF8.GetString(await service.ReadCatalogAsync("community")));
        Assert.Single(service.Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlistResolvesPublicRawOrSignedProxyWithoutForwardingCredentials(bool raw)
    {
        var count = 0;
        using var handler = new Handler(request =>
        {
            count++;
            if (count == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://files.example.org/api/fs/get", request.RequestUri!.AbsoluteUri);
                return Reply(JsonSerializer.Serialize(new { code = 200, data = new { is_dir = false, size = 10, raw_url = raw ? "https://objects.example.org/catalog" : "", sign = "a+b" } }));
            }
            Assert.Equal(raw ? "https://objects.example.org/catalog" : "https://files.example.org/d/source.toml?sign=a%2Bb", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Reply("schema = 2");
        });
        using var http = new HttpClient(handler);
        Assert.Equal(10, (await Create(http, "alist", "https://files.example.org/").ReadCatalogAsync("community")).Length);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(null, "latest")]
    [InlineData("v1.2.3", "tags/v1.2.3")]
    public async Task RepositoryReferenceDiscoversExactReleaseAndAllAttachmentsWithoutDownloadingPayload(string? tag, string suffix)
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            Assert.Equal("https://api.github.com/repos/owner/plugin/releases/" + suffix, request.RequestUri!.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Release();
        });
        using var http = new HttpClient(handler);
        var release = await Create(http).ReadRepositoryReleaseAsync("https://github.com/owner/plugin.git", tag);
        Assert.Equal("owner/plugin", release.Repository);
        Assert.Equal(123, release.Id);
        Assert.Equal("v1.2.3", release.Tag);
        Assert.Equal("Release notes", release.Notes);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(new string('a', 64), release.Assets[0].Sha256);
        Assert.Null(release.Assets[1].Sha256);
        Assert.Equal("plugin-linux-x64.zip", release.Assets[1].Name);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("https://github.com/owner/plugin/tree/main")]
    [InlineData("http://github.com/owner/plugin")]
    [InlineData("https://github.com.attacker.example/owner/plugin")]
    [InlineData("https://user:password@github.com/owner/plugin")]
    [InlineData("https://github.com/owner/plugin?token=secret")]
    public async Task InvalidRepositoryReferencesFailBeforeNetwork(string address)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected network access."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(http).ReadRepositoryReleaseAsync(address).AsTask());
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("foreign-asset")]
    [InlineData("bad-digest")]
    [InlineData("duplicate-asset")]
    public async Task UnpublishedOrInconsistentReleaseMetadataCannotBecomeAnInstallCandidate(string invalid)
    {
        using var handler = new Handler(_ => Release(invalid));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(http).ReadRepositoryReleaseAsync("https://github.com/owner/plugin").AsTask());
    }

    [Fact]
    public async Task ExplicitTagCannotSilentlyResolveToAnotherRelease()
    {
        using var handler = new Handler(_ => Release());
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(http).ReadRepositoryReleaseAsync("https://github.com/owner/plugin", "v0.9").AsTask());
    }

    [Theory]
    [InlineData("../source.toml")]
    [InlineData("/source.toml")]
    [InlineData("%2e%2e/source.toml")]
    [InlineData("https://elsewhere.example/source.toml")]
    public void CatalogPathsCannotEscapeSourceRoot(string path)
    {
        using var http = new HttpClient();
        var options = Options();
        options.Sources[0].CatalogPath = path;
        Assert.Throws<ArgumentException>(() => new DistributionSources(http, options));
    }

    [Fact]
    public async Task ChunkedMetadataIsBoundedWithoutTrustingContentLength()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[1025])) });
        using var http = new HttpClient(handler);
        var options = Options();
        options.MaximumDocumentBytes = 1024;
        await Assert.ThrowsAsync<InvalidDataException>(() => new DistributionSources(http, options).ReadCatalogAsync("community").AsTask());
    }

    [Fact]
    public async Task RedirectsCannotDowngradeHttpsOrLoopIndefinitely()
    {
        using var handler = new Handler(_ =>
        {
            var reply = new HttpResponseMessage(HttpStatusCode.Redirect);
            reply.Headers.Location = new Uri("http://files.example.org/source.toml");
            return reply;
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(http).ReadCatalogAsync("community").AsTask());
    }

    [Fact]
    public async Task PluginLifetimeCancellationStopsInflightRepositoryRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable.");
        });
        using var http = new HttpClient(handler);
        using var lifetime = new CancellationTokenSource();
        var pending = new DistributionSources(http, Options(), lifetime.Token).ReadRepositoryReleaseAsync("https://github.com/owner/plugin").AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static DistributionOptions Options(string kind = "http", string root = "https://files.example.org/") => new()
    { Sources = [new() { Id = "community", Name = "Community", Kind = kind, BaseUrl = root }] };

    private static DistributionSources Create(HttpClient http, string kind = "http", string root = "https://files.example.org/") => new(http, Options(kind, root));

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static HttpResponseMessage Release(string? invalid = null)
    {
        var first = new { id = 42, name = "plugin-win-x64.zip", size = 100,
            browser_download_url = invalid == "foreign-asset" ? "https://github.com/other/plugin/releases/download/v1.2.3/payload.zip" : "https://github.com/owner/plugin/releases/download/v1.2.3/plugin-win-x64.zip",
            digest = invalid == "bad-digest" ? "sha256:bad" : "sha256:" + new string('a', 64) };
        var second = new { id = invalid == "duplicate-asset" ? 42 : 43, name = "plugin-linux-x64.zip", size = 120,
            browser_download_url = "https://github.com/owner/plugin/releases/download/v1.2.3/plugin-linux-x64.zip", digest = (string?)null };
        return Reply(JsonSerializer.Serialize(new { id = 123, tag_name = "v1.2.3", name = "Plugin", body = "Release notes", draft = invalid == "draft", prerelease = invalid == "prerelease",
            html_url = "https://github.com/owner/plugin/releases/tag/v1.2.3", assets = new object[] { first, second } }));
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _reply;
        internal Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = (request, _) => Task.FromResult(reply(request));
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _reply(request, cancellationToken);
    }
}
