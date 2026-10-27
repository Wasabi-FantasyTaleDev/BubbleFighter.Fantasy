using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionCatalogMergeTests
{
    private static readonly DistributionSource First = new("first", "First source", "http", "https://one.example/", "source.toml");
    private static readonly DistributionSource Second = new("second", "Second source", "github", "https://github.com/owner/store/", "source.toml");
    private static DistributionPackageOffer Offer(string id = "example-plugin", string target = "server", string name = "Example", string version = "1.0.0") =>
        new(new(target, "plugin", "bubblefighter", id), version, name, null, "release.toml", new string('a', 64));

    [Fact]
    public void AllSourcesDeduplicatesByIdentityWhilePreservingEverySourceAndItsVersion()
    {
        var entries = DistributionCatalogMerge.Merge([First, Second],
            [new("second", [Offer(name: "Another display name", version: "2.0.0")]), new("first", [Offer()])]);
        var card = Assert.Single(entries);
        Assert.Equal(new[] { "first", "second" }, card.Downloads.Select(choice => choice.SourceId));
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, card.Downloads.Select(choice => choice.Package.Version));
        Assert.Equal("First source", card.Downloads[0].SourceName);
    }

    [Fact]
    public void ThreeTransportSourcesKeepConfiguredOrderAndDistinctManifests()
    {
        var third = new DistributionSource("third", "Third source", "alist", "https://three.example/store/", "source.toml");
        var firstOffer = Offer(version: "1.0.0");
        var secondOffer = Offer(version: "9.0.0") with { ManifestPath = "second.toml", ManifestSha256 = new string('b', 64) };
        var thirdOffer = Offer(version: "2.0.0") with { ManifestPath = "third.toml", ManifestSha256 = new string('c', 64) };
        var card = Assert.Single(DistributionCatalogMerge.Merge([First, third, Second],
            [new(Second.Id, [secondOffer]), new(First.Id, [firstOffer]), new(third.Id, [thirdOffer])]));

        Assert.Equal(new[] { "first", "third", "second" }, card.Downloads.Select(choice => choice.SourceId));
        Assert.Equal(new[] { "1.0.0", "2.0.0", "9.0.0" }, card.Downloads.Select(choice => choice.Package.Version));
        Assert.Same(firstOffer, card.Downloads[0].Package);
        Assert.Same(thirdOffer, card.Downloads[1].Package);
        Assert.Same(secondOffer, card.Downloads[2].Package);
        Assert.Equal(3, card.Downloads.Select(choice => choice.SelectionFingerprint).Distinct().Count());
    }

    [Fact]
    public void ReorderingSourcesChangesDefaultWithoutMixingVersionAndManifestIdentity()
    {
        var repository = Offer(version: "2.0.0") with { RepositoryUrl = "https://github.com/owner/plugin", ManifestPath = null, ManifestSha256 = null };
        var card = Assert.Single(DistributionCatalogMerge.Merge([Second, First], [new("first", [Offer()]), new("second", [repository])]));
        Assert.Equal("second", card.Downloads[0].SourceId);
        Assert.Same(repository, card.Downloads[0].Package);
        Assert.Null(card.Downloads[0].Package.ManifestSha256);
    }

    [Fact]
    public void EqualDisplayNamesAndClientServerTargetsDoNotMergeUnrelatedPackages()
    {
        var entries = DistributionCatalogMerge.Merge([First], [new("first", [Offer(), Offer(target: "client"), Offer(id: "another-plugin")])]);
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void IndividualSourceFilterHasOnlyThatSourcesOffer()
    {
        var card = Assert.Single(DistributionCatalogMerge.Merge([First, Second], [new("first", [Offer()]), new("second", [Offer(version: "2.0.0")])], "second"));
        Assert.Equal("second", Assert.Single(card.Downloads).SourceId);
    }

    [Fact]
    public void UnavailableFirstSourceDoesNotHideAnAvailableSecondSource()
    {
        var card = Assert.Single(DistributionCatalogMerge.Merge([First, Second], [new("second", [Offer()])]));
        Assert.Equal("second", card.Downloads[0].SourceId);
    }

    [Fact]
    public void DuplicateOrUnconfiguredSnapshotsFailInsteadOfChangingSourceAuthority()
    {
        Assert.Throws<InvalidDataException>(() => DistributionCatalogMerge.Merge([First], [new("first", []), new("first", [])]));
        Assert.Throws<InvalidDataException>(() => DistributionCatalogMerge.Merge([First], [new("other", [])]));
        Assert.Throws<InvalidDataException>(() => DistributionCatalogMerge.Merge([First], [new("first", [Offer(), Offer()])]));
        Assert.Throws<KeyNotFoundException>(() => DistributionCatalogMerge.Merge([First], [], "other"));
    }
}
