namespace BubbleFighter.Fantasy.Contracts.Distribution;

/// <summary>Selects one previously browsed source offer and, when applicable, one exact repository attachment.</summary>
public sealed record DistributionPlanRequest(long SourceRevision, string SourceId, DistributionPackageIdentity Package,
    string SelectionFingerprint, long? RepositoryAssetId);

/// <summary>Captures a source-bound selection without authorizing a plugin load or application restart.</summary>
public sealed record DistributionPlan(string Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    long SourceRevision, DistributionSource Source, DistributionDownloadChoice Selection, RepositoryAsset? Asset);

/// <summary>Creates and revalidates bounded source-specific plans without selecting a different mirror or version.</summary>
public interface IDistributionPlans
{
    /// <summary>Refreshes the explicitly selected source and refuses changed source, catalog, release or attachment metadata.</summary>
    ValueTask<DistributionPlan> CreateAsync(DistributionPlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Rechecks one unexpired stored plan against its exact source and returns it unchanged if still current.</summary>
    ValueTask<DistributionPlan> ValidateAsync(string planId, CancellationToken cancellationToken = default);

    /// <summary>Releases a plan that the operator no longer needs.</summary>
    bool Remove(string planId);
}
