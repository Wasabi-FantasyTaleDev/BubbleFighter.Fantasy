using System.Net;
using System.Text.Json;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionCatalogBrowseTests
{
    [Fact]
    public async Task SourceCatalogWithOnlyRepositoryReferenceDiscoversVersionAndAssetsAndMergesWithMirror()
    {
        using var handler = new Handler(request => request.RequestUri!.Host switch
        {
            "one.example" => Reply("""
                schema = 3
                [catalog]
                id = "primary"
                [[packages]]
                id = "example-plugin"
                target = "server"
                kind = "plugin"
                game = "bubblefighter"
                name = { en = "Example" }
                github_repository = "https://github.com/owner/plugin"
                """),
            "two.example" => Reply("""
                schema = 3
                [catalog]
                id = "mirror"
                [[packages]]
                id = "example-plugin"
                target = "server"
                kind = "plugin"
                game = "bubblefighter"
                name = { en = "Mirror display name" }
                version = "1.0.0"
                manifest = { path = "release.toml", sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
                """),
            "api.github.com" => Reply(JsonSerializer.Serialize(new { id = 123, tag_name = "v2.0.0", draft = false, prerelease = false,
                html_url = "https://github.com/owner/plugin/releases/tag/v2.0.0", assets = new[] { new { id = 456, name = "plugin.zip", size = 321,
                    browser_download_url = "https://github.com/owner/plugin/releases/download/v2.0.0/plugin.zip" } } })),
            _ => throw new InvalidOperationException("Unexpected payload request.")
        });
        using var http = new HttpClient(handler);
        var sources = new DistributionSources(http, Options());
        var snapshot = await sources.BrowseAsync();
        Assert.Empty(snapshot.Failures);
        var card = Assert.Single(snapshot.Entries);
        Assert.Equal(new[] { "primary", "mirror" }, card.Downloads.Select(row => row.SourceId));
        Assert.Equal(new[] { "v2.0.0", "1.0.0" }, card.Downloads.Select(row => row.Package.Version));
        var release = Assert.IsType<Contracts.Distribution.RepositoryRelease>(card.Downloads[0].Package.RepositoryRelease);
        Assert.Equal(123, release.Id);
        Assert.Equal(456, Assert.Single(release.Assets).Id);
        Assert.Equal("https://github.com/owner/plugin/releases/download/v2.0.0/plugin.zip", release.Assets[0].DownloadUrl);
        Assert.Null(card.Downloads[1].Package.RepositoryRelease);
    }

    [Fact]
    public async Task FailedTopSourceIsReportedWhileLowerSourceRemainsAvailable()
    {
        using var handler = new Handler(request => request.RequestUri!.Host == "one.example"
            ? new(HttpStatusCode.ServiceUnavailable) : Reply(Catalog(1)));
        using var http = new HttpClient(handler);
        var snapshot = await new DistributionSources(http, Options()).BrowseAsync();
        Assert.Equal("primary", Assert.Single(snapshot.Failures).SourceId);
        Assert.Equal("source_unavailable", snapshot.Failures[0].Code);
        Assert.Equal("mirror", Assert.Single(Assert.Single(snapshot.Entries).Downloads).SourceId);
    }

    [Fact]
    public async Task LegacyCatalogHistoryUsesPublisherRevisionAndDoesNotGuessLexicalVersions()
    {
        using var handler = new Handler(_ => Reply(Catalog(2) + """

            [[packages]]
            id = "example-plugin"
            kind = "plugin"
            game = "bubblefighter"
            version = "0.1.0"
            revision = 3
            manifest = "new-release.toml"
            manifest_sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            """));
        using var http = new HttpClient(handler);
        var snapshot = await new DistributionSources(http, Options()).BrowseAsync("primary");
        var selected = Assert.Single(Assert.Single(snapshot.Entries).Downloads);
        Assert.Equal("0.1.0", selected.Package.Version);
        Assert.Equal("new-release.toml", selected.Package.ManifestPath);
        Assert.Equal("client", selected.Package.Identity.Target);
    }

    [Fact]
    public async Task RepositoryFieldsAreNotSilentlyAddedToExistingSchemaTwo()
    {
        using var handler = new Handler(_ => Reply(Catalog(1) + "\ngithub_repository = \"https://github.com/owner/plugin\"\n"));
        using var http = new HttpClient(handler);
        var snapshot = await new DistributionSources(http, Options()).BrowseAsync("primary");
        Assert.Empty(snapshot.Entries);
        Assert.Equal("catalog_invalid", Assert.Single(snapshot.Failures).Code);
    }

    private static string Catalog(int revision) => $$"""
        schema = 2
        [catalog]
        id = "store"
        [[packages]]
        id = "example-plugin"
        kind = "plugin"
        game = "bubblefighter"
        version = "9.0.0"
        revision = {{revision}}
        manifest = "release.toml"
        manifest_sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        """;

    private static DistributionOptions Options() => new() { Sources = [
        new() { Id = "primary", Name = "Primary", BaseUrl = "https://one.example/" },
        new() { Id = "mirror", Name = "Mirror", BaseUrl = "https://two.example/" }] };

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
}
