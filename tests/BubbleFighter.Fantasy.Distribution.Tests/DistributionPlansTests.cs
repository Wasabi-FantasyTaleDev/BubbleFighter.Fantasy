using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionPlansTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly DistributionPackageIdentity Identity = new("server", "plugin", "bubblefighter", "example");
    private static readonly DistributionSource Primary = new("primary", "Primary", "http", "https://one.example/", "source.toml");
    private static readonly DistributionSource Mirror = new("mirror", "Mirror", "github", "https://github.com/owner/store/", "source.toml");
    private static DistributionPackageOffer Offer(string version = "1.0.0") => new(Identity, version, "Example", null, "release.toml", new string('a', 64))
        { CatalogSha256 = new string('b', 64) };
    private static DistributionPlanRequest Request(FakeSources sources, string sourceId = "mirror", long? assetId = null) =>
        new(sources.Revision, sourceId, Identity, sources.Snapshot.Entries.Single().Downloads.Single(choice => choice.SourceId == sourceId).SelectionFingerprint, assetId);

    [Fact]
    public async Task ExplicitMirrorSelectionDoesNotFallBackToFirstOrNewestSource()
    {
        var sources = new FakeSources();
        using var plans = new DistributionPlans(sources, new());
        var plan = await plans.CreateAsync(Request(sources));
        Assert.Equal("mirror", plan.Source.Id);
        Assert.Equal("mirror", plan.Selection.SourceId);
        Assert.Equal("2.0.0", plan.Selection.Package.Version);
        Assert.Same(plan, await plans.ValidateAsync(plan.Id));
        Assert.Equal(new[] { "mirror", "mirror" }, sources.RequestedSources);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("version")]
    [InlineData("manifest")]
    public async Task ChangedSelectedOfferIsRefusedWithoutFollowingAnotherSource(string change)
    {
        var sources = new FakeSources();
        var request = Request(sources);
        var updated = Offer("2.0.0");
        updated = change switch
        {
            "catalog" => updated with { CatalogSha256 = new string('c', 64) },
            "version" => updated with { Version = "3.0.0" },
            _ => updated with { ManifestSha256 = new string('d', 64) }
        };
        sources.MirrorOffer = updated;
        using var plans = new DistributionPlans(sources, new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plans.CreateAsync(request).AsTask());
        Assert.Equal("distribution_selection_changed", error.Message);
        Assert.Equal("mirror", Assert.Single(sources.RequestedSources));
    }

    [Fact]
    public async Task SourceRevisionChangeDuringRefreshRefusesSelection()
    {
        var sources = new FakeSources { BeforeReturn = self => self.Revision++ };
        using var plans = new DistributionPlans(sources, new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plans.CreateAsync(Request(sources)).AsTask());
        Assert.Equal("distribution_source_changed", error.Message);
    }

    [Fact]
    public async Task RepositoryAttachmentMustBeSelectedByExactAssetIdentity()
    {
        var sources = new FakeSources();
        var asset = new RepositoryAsset(456, "plugin.zip", 123, "https://github.com/owner/plugin/releases/download/v1/plugin.zip", new string('a', 64));
        sources.MirrorOffer = Offer() with { RepositoryUrl = "https://github.com/owner/plugin", ManifestPath = null, ManifestSha256 = null,
            RepositoryRelease = new("owner/plugin", 123, "v1", "Release", "", "https://github.com/owner/plugin/releases/tag/v1", [asset]) };
        using var plans = new DistributionPlans(sources, new());
        await Assert.ThrowsAsync<ArgumentException>(() => plans.CreateAsync(Request(sources)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => plans.CreateAsync(Request(sources, assetId: 999)).AsTask());
        var plan = await plans.CreateAsync(Request(sources, assetId: 456));
        Assert.Equal(asset, plan.Asset);
        sources.MirrorOffer = sources.MirrorOffer with { RepositoryRelease = sources.MirrorOffer.RepositoryRelease! with { Assets = [asset with { Sha256 = new string('c', 64) }] } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => plans.ValidateAsync(plan.Id).AsTask());
    }

    [Fact]
    public async Task ClientPreviewsCannotCreateServerPlans()
    {
        var sources = new FakeSources();
        using var plans = new DistributionPlans(sources, new());
        await Assert.ThrowsAsync<ArgumentException>(() => plans.CreateAsync(Request(sources) with { Package = Identity with { Target = "client" } }).AsTask());
        Assert.Empty(sources.RequestedSources);
    }

    [Fact]
    public async Task PendingWorkReservesCapacityAndCancellationReleasesReservation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new FakeSources { Wait = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        using var plans = new DistributionPlans(sources, new() { MaximumPlans = 1 });
        using var cancelled = new CancellationTokenSource();
        var pending = plans.CreateAsync(Request(sources), cancelled.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => plans.CreateAsync(Request(sources)).AsTask());
        Assert.Equal("distribution_plan_capacity", busy.Message);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Limit));
        sources.Wait = null;
        Assert.NotNull(await plans.CreateAsync(Request(sources)));
    }

    [Fact]
    public async Task ExpirationAndExplicitRemovalInvalidatePlansAndReleaseStorage()
    {
        var clock = new Clock();
        var sources = new FakeSources();
        using var plans = new DistributionPlans(sources, new() { MaximumPlans = 1, PlanLifetimeSeconds = 10 }, clock: clock);
        var first = await plans.CreateAsync(Request(sources));
        clock.Now = clock.Now.AddSeconds(11);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => plans.ValidateAsync(first.Id).AsTask());
        var second = await plans.CreateAsync(Request(sources));
        Assert.True(plans.Remove(second.Id));
        Assert.False(plans.Remove(second.Id));
        Assert.NotNull(await plans.CreateAsync(Request(sources)));
    }

    [Fact]
    public async Task RemovingPlanDuringValidationCannotReturnAnApprovedSelection()
    {
        var sources = new FakeSources();
        using var plans = new DistributionPlans(sources, new());
        var plan = await plans.CreateAsync(Request(sources));
        sources.BeforeReturn = _ => plans.Remove(plan.Id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plans.ValidateAsync(plan.Id).AsTask());
        Assert.Equal("distribution_plan_withdrawn", error.Message);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSources : IDistributionSources
    {
        internal long Revision = 7;
        internal DistributionPackageOffer MirrorOffer = Offer("2.0.0");
        internal readonly List<string?> RequestedSources = [];
        internal Func<CancellationToken, Task>? Wait;
        internal Action<FakeSources>? BeforeReturn;
        public IReadOnlyList<DistributionSource> Sources => [Primary, Mirror];
        public DistributionSourceSettings Settings => new(Revision, Sources);
        internal DistributionStoreSnapshot Snapshot => new(DistributionCatalogMerge.Merge(Sources,
            [new(Primary.Id, [Offer("9.0.0")]), new(Mirror.Id, [MirrorOffer])]), []) { SourceRevision = Revision };
        public async ValueTask<DistributionStoreSnapshot> BrowseAsync(string? sourceId = null, CancellationToken cancellationToken = default)
        {
            RequestedSources.Add(sourceId);
            if (Wait is not null) await Wait(cancellationToken);
            var snapshot = Snapshot;
            BeforeReturn?.Invoke(this);
            return snapshot;
        }
        public ValueTask<byte[]> ReadCatalogAsync(string sourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<DistributionSourceSettings> SaveSourcesAsync(long expectedRevision, IReadOnlyList<DistributionSource> sources, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<RepositoryRelease> ReadRepositoryReleaseAsync(string repositoryUrl, string? tag = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
