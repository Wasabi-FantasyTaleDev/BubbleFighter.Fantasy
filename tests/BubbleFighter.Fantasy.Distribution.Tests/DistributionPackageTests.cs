using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionPackageTests
{
    private const string Manifest = """
        format = "fantasytale-distribution-release"
        schema = 1
        [release]
        catalog_id = "store"
        id = "example"
        target = "server"
        kind = "plugin"
        game = "bubblefighter"
        version = "1.0.0"
        revision = 7
        builds = ["kr-1256"]
        platforms = ["win-x64"]
        payload_origin = "@catalog"
        [[files]]
        path = "plugin.dll"
        source_path = "objects/plugin"
        size = 3
        sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        """;

    [Fact]
    public async Task ManifestBytesAndIdentityAreBoundToTheSelectedSourceBeforePayloadReads()
    {
        var (plans, transport, service) = Create(Manifest);
        var result = await service.PrepareAsync("plan");
        Assert.Same(plans.Current, result.Selection);
        Assert.Equal("fantasytale-distribution-release", result.Format);
        Assert.Equal(3, result.TotalBytes);
        var file = Assert.Single(result.Files);
        Assert.Equal("plugin.dll", file.Path);
        Assert.Equal("objects/plugin", file.SourcePath);
        Assert.Null(file.Asset);
        Assert.Equal(plans.Current.Selection.Package.ManifestSha256, result.ManifestSha256);
        Assert.Equal(plans.Current.Source, transport.RequestedSource);
        Assert.Equal("releases/release.toml", transport.RequestedPath);
        Assert.Equal(new[] { "kr-1256" }, result.Builds);
        Assert.Equal(new[] { "win-x64" }, result.Platforms);
        Assert.Equal(2, plans.Validations);
    }

    [Fact]
    public async Task ExperimentalNeutralFormatIsNotEnabledByDefault()
    {
        var (plans, transport, _) = Create(Manifest);
        await Assert.ThrowsAsync<NotSupportedException>(() => new DistributionPackages(plans, transport, new()).PrepareAsync("plan").AsTask());
    }

    [Fact]
    public async Task ChangedManifestBytesAreRefusedBeforeParsing()
    {
        var (_, transport, service) = Create(Manifest);
        transport.Bytes = Encoding.UTF8.GetBytes("not a manifest");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
        Assert.Contains("SHA-256", error.Message);
    }

    [Theory]
    [InlineData("catalog_id = \"store\"", "catalog_id = \"other\"")]
    [InlineData("id = \"example\"", "id = \"other\"")]
    [InlineData("target = \"server\"", "target = \"client\"")]
    [InlineData("kind = \"plugin\"", "kind = \"application\"")]
    [InlineData("version = \"1.0.0\"", "version = \"2.0.0\"")]
    [InlineData("revision = 7", "revision = 8")]
    [InlineData("builds = [\"kr-1256\"]", "builds = [\"cn-774\"]")]
    [InlineData("platforms = [\"win-x64\"]", "platforms = [\"linux-x64\"]")]
    [InlineData("payload_origin = \"@catalog\"", "payload_origin = \"other-source\"")]
    [InlineData("schema = 1", "schema = 2")]
    [InlineData("format = \"fantasytale-distribution-release\"", "format = \"other\"")]
    public async Task ManifestCannotOverrideCatalogIdentityOrOrigin(string before, string after)
    {
        var (_, _, service) = Create(Manifest.Replace(before, after, StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
    }

    [Theory]
    [InlineData("../plugin.dll")]
    [InlineData("/absolute/plugin.dll")]
    [InlineData("CON.dll")]
    [InlineData("dir/../plugin.dll")]
    [InlineData("trailing./plugin.dll")]
    public async Task ManifestDestinationsMustBePortableRelativeFiles(string path)
    {
        var (_, _, service) = Create(Manifest.Replace("path = \"plugin.dll\"", "path = \"" + path + "\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
    }

    [Theory]
    [InlineData("PLUGIN.dll")]
    [InlineData("plugin.dll/child")]
    public async Task DuplicateAndFileDirectoryCollisionsAreRejected(string second)
    {
        var (_, _, service) = Create(Manifest + $$"""

            [[files]]
            path = "{{second}}"
            source_path = "objects/second"
            size = 0
            sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
    }

    [Theory]
    [InlineData("objects/%2e%2e/payload")]
    [InlineData("https://other.example/payload")]
    [InlineData("objects/payload#fragment")]
    public async Task PayloadSourcePathsCannotIntroduceAnotherOrigin(string source)
    {
        var (_, _, service) = Create(Manifest.Replace("objects/plugin", source, StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
    }

    [Fact]
    public async Task InventoryTotalMustMatchTheDeclaredCatalogSize()
    {
        var (_, _, service) = Create(Manifest.Replace("size = 3", "size = 4", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
    }

    [Fact]
    public async Task SchemaOneApplicationReaderIsSeparateFromNeutralPluginReader()
    {
        const string manifest = """
            schema = 1
            [release]
            id = "example"
            game = "bubblefighter"
            builds = ["kr-1256"]
            version = "1.0.0"
            [[files]]
            path = "bff"
            source_path = "objects/server"
            size = 3
            sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            """;
        var (plans, _, service) = Create(manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
        var offer = plans.Current.Selection.Package with { Identity = new("server", "application", "bubblefighter", "example"), Platforms = [] };
        plans.Current = plans.Current with { Selection = plans.Current.Selection with { Package = offer } };
        var prepared = await service.PrepareAsync("plan");
        Assert.Equal("server-application-schema-1", prepared.Format);
        Assert.Empty(prepared.Platforms);
    }

    [Fact]
    public async Task PlanWithdrawalDuringManifestReadCannotProduceAFilePlan()
    {
        var (plans, transport, service) = Create(Manifest);
        transport.OnRead = () => plans.Withdrawn = true;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.PrepareAsync("plan").AsTask());
    }

    [Fact]
    public async Task SelectionChangeDuringManifestReadIsRefused()
    {
        var (plans, transport, service) = Create(Manifest);
        transport.OnRead = () => plans.Current = plans.Current with { SourceRevision = 99 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync("plan").AsTask());
    }

    [Fact]
    public async Task ClientTargetCannotUseFilePreparationAsAServerDownload()
    {
        var (plans, transport, service) = Create(Manifest);
        plans.Current = plans.Current with { Selection = plans.Current.Selection with { Package = plans.Current.Selection.Package with
            { Identity = new("client", "plugin", "bubblefighter", "example") } } };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
        Assert.Null(transport.RequestedPath);
    }

    [Fact]
    public async Task RepositoryInventoryRequiresPriorDigestAndNeverDownloadsAttachmentDuringPreparation()
    {
        var (plans, transport, service) = Create(Manifest);
        var asset = new RepositoryAsset(77, "plugin.zip", 123, "https://github.com/owner/plugin/releases/download/v1/plugin.zip", null);
        plans.Current = plans.Current with { Asset = asset };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync("plan").AsTask());
        plans.Current = plans.Current with { Asset = asset with { Sha256 = new string('c', 64) } };
        var result = await service.PrepareAsync("plan");
        Assert.Equal("repository-asset", result.Format);
        Assert.Equal(plans.Current.Asset, Assert.Single(result.Files).Asset);
        Assert.Null(result.ManifestSha256);
        Assert.Null(transport.RequestedPath);
    }

    private static (Plans, Transport, DistributionPackages) Create(string manifest)
    {
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var offer = new DistributionPackageOffer(new("server", "plugin", "bubblefighter", "example"), "1.0.0", "Example", null,
            "releases/release.toml", Convert.ToHexStringLower(SHA256.HashData(bytes)))
            { CatalogId = "store", Revision = 7, Builds = ["kr-1256"], Platforms = ["win-x64"], Size = 3 };
        var now = DateTimeOffset.UtcNow;
        var plans = new Plans(new("plan", now, now.AddHours(1), 3, new("chosen", "Chosen", "http", "https://chosen.example/", "source.toml"),
            new("chosen", "Chosen", offer) { SelectionFingerprint = new string('d', 64) }, null));
        var transport = new Transport(bytes);
        return (plans, transport, new(plans, transport, new() { ExperimentalReleaseFormat = true }));
    }

    private sealed class Plans(DistributionPlan current) : IDistributionPlans
    {
        internal DistributionPlan Current = current;
        internal bool Withdrawn;
        internal int Validations;
        public ValueTask<DistributionPlan> ValidateAsync(string planId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validations++;
            if (Withdrawn) throw new KeyNotFoundException();
            Assert.Equal(Current.Id, planId);
            return ValueTask.FromResult(Current);
        }
        public ValueTask<DistributionPlan> CreateAsync(DistributionPlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool Remove(string planId) => throw new NotSupportedException();
    }

    private sealed class Transport(byte[] bytes) : IDistributionTransport
    {
        internal byte[] Bytes = bytes;
        internal DistributionSource? RequestedSource;
        internal string? RequestedPath;
        internal Action? OnRead;
        public ValueTask<byte[]> ReadDocumentAsync(DistributionSource source, string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedSource = source;
            RequestedPath = relativePath;
            OnRead?.Invoke();
            return ValueTask.FromResult(Bytes);
        }
        public ValueTask CopyFileAsync(DistributionSource source, string relativePath, long expectedSize, string sha256, Stream staging, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Preparation must not fetch payloads.");
        public ValueTask CopyRepositoryAssetAsync(RepositoryAsset asset, Stream staging, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Preparation must not fetch payloads.");
    }
}
