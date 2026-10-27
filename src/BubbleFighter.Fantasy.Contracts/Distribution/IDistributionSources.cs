namespace BubbleFighter.Fantasy.Contracts.Distribution;

/// <summary>Identifies a configured public catalog transport without credentials.</summary>
public sealed record DistributionSource(string Id, string Name, string Kind, string BaseUrl, string CatalogPath);

/// <summary>Captures the source order and revision used to reject conflicting operator edits.</summary>
public sealed record DistributionSourceSettings(long Revision, IReadOnlyList<DistributionSource> Sources);

/// <summary>Describes a published GitHub attachment without granting installation authority.</summary>
public sealed record RepositoryAsset(long Id, string Name, long Size, string DownloadUrl, string? Sha256);

/// <summary>Captures an exact published release and its attachments for subsequent package selection.</summary>
public sealed record RepositoryRelease(string Repository, long Id, string Tag, string Name, string Notes,
    string PageUrl, IReadOnlyList<RepositoryAsset> Assets);

/// <summary>Identifies one package independently of its mirrors, display name and release version.</summary>
public sealed record DistributionPackageIdentity(string Target, string Kind, string Game, string Id);

/// <summary>Preserves one source's exact package release and its optional repository reference.</summary>
public sealed record DistributionPackageOffer(DistributionPackageIdentity Identity, string Version, string Name,
    string? RepositoryUrl, string? ManifestPath, string? ManifestSha256)
{
    /// <summary>Gets the exact discovered repository release when this offer came from a repository reference.</summary>
    public RepositoryRelease? RepositoryRelease { get; init; }

    /// <summary>Gets the publisher's monotonic revision, or zero when the catalog does not declare one.</summary>
    public long Revision { get; init; }

    /// <summary>Gets the digest of the exact catalog bytes from which this offer was read.</summary>
    public string CatalogSha256 { get; init; } = "";

    /// <summary>Gets the publisher's catalog label within this exact configured source.</summary>
    public string CatalogId { get; init; } = "";

    /// <summary>Gets declared client build compatibility, retained exactly rather than inferred from a repository tag.</summary>
    public IReadOnlyList<string> Builds { get; init; } = Array.Empty<string>();

    /// <summary>Gets declared platform identities when the catalog supplies them.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    /// <summary>Gets the declared aggregate payload size, or null when absent from the catalog.</summary>
    public long? Size { get; init; }
}

/// <summary>Associates a validated catalog snapshot with its configured source identity.</summary>
public sealed record DistributionCatalogSnapshot(string SourceId, IReadOnlyList<DistributionPackageOffer> Packages);

/// <summary>Identifies one selectable download origin without dropping its own release metadata.</summary>
public sealed record DistributionDownloadChoice(string SourceId, string SourceName, DistributionPackageOffer Package)
{
    /// <summary>Gets a fingerprint of the configured source and exact offer, not an authorization token.</summary>
    public string SelectionFingerprint { get; init; } = "";
}

/// <summary>Represents one deduplicated store card and its source-ordered download choices.</summary>
public sealed record DistributionStoreEntry(DistributionPackageIdentity Identity, IReadOnlyList<DistributionDownloadChoice> Downloads);

/// <summary>Identifies a source that could not provide a validated catalog during refresh.</summary>
public sealed record DistributionSourceFailure(string SourceId, string Code);

/// <summary>Returns deduplicated entries together with explicit per-source refresh failures.</summary>
public sealed record DistributionStoreSnapshot(IReadOnlyList<DistributionStoreEntry> Entries, IReadOnlyList<DistributionSourceFailure> Failures)
{
    /// <summary>Gets the configuration revision captured before source refresh began.</summary>
    public long SourceRevision { get; init; }
}

/// <summary>Reads configured public catalogs and resolves repository release metadata without executing packages.</summary>
public interface IDistributionSources
{
    /// <summary>Returns the validated configured source identities.</summary>
    IReadOnlyList<DistributionSource> Sources { get; }

    /// <summary>Returns one consistent source configuration snapshot, including its edit revision.</summary>
    DistributionSourceSettings Settings { get; }

    /// <summary>Atomically persists validated sources in operator order if the expected revision is still current.</summary>
    ValueTask<DistributionSourceSettings> SaveSourcesAsync(long expectedRevision, IReadOnlyList<DistributionSource> sources,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the configured catalog bytes using its HTTP, AList or GitHub adapter.</summary>
    ValueTask<byte[]> ReadCatalogAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>Refreshes all sources in operator order, or one selected source, preserving distinct download offers.</summary>
    ValueTask<DistributionStoreSnapshot> BrowseAsync(string? sourceId = null, CancellationToken cancellationToken = default);

    /// <summary>Resolves a GitHub repository to its latest stable release, or an explicitly selected tag.</summary>
    ValueTask<RepositoryRelease> ReadRepositoryReleaseAsync(string repositoryUrl, string? tag = null,
        CancellationToken cancellationToken = default);
}
