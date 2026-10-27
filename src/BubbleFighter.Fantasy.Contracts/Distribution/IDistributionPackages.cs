namespace BubbleFighter.Fantasy.Contracts.Distribution;

/// <summary>Describes one verified-manifest file or exact repository attachment without authorizing installation.</summary>
public sealed record DistributionPayloadFile(string Path, string? SourcePath, long Size, string Sha256, RepositoryAsset? Asset);

/// <summary>Captures the file inventory of one source-bound selection before any payload transfer.</summary>
public sealed record DistributionFilePlan(DistributionPlan Selection, string Format, string? ManifestSha256,
    IReadOnlyList<string> Builds, IReadOnlyList<string> Platforms, IReadOnlyList<DistributionPayloadFile> Files, long TotalBytes);

/// <summary>Prepares digest-verified file inventories for server-only staging, independently of the plugin loader.</summary>
public interface IDistributionPackages
{
    /// <summary>Revalidates a selection, verifies its manifest bytes and identities, and refuses unsupported origins or file shapes.</summary>
    /// <remarks>Repository-only inventories verify the selected attachment but do not establish package compatibility or installation authority.</remarks>
    ValueTask<DistributionFilePlan> PrepareAsync(string planId, CancellationToken cancellationToken = default);
}
