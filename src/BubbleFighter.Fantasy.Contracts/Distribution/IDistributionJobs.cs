namespace BubbleFighter.Fantasy.Contracts.Distribution;

/// <summary>Reports one owned download operation without exposing staging paths or treating a download as installation.</summary>
public sealed record DistributionDownloadJob(string Id, string PlanId, string SourceId, DistributionPackageIdentity Package,
    string Version, string Phase, long BytesReceived, long TotalBytes, int CompletedFiles, int FileCount,
    string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Returns a bounded page of current or persisted jobs and an opaque cursor for older entries.</summary>
public sealed record DistributionDownloadJobPage(IReadOnlyList<DistributionDownloadJob> Entries, string? NextCursor);

/// <summary>Reports a completed readback of the staged inventory and payload bytes without granting installation authority.</summary>
public sealed record DistributionStagingVerification(string JobId, int Files, long Bytes, DateTimeOffset VerifiedAt);

/// <summary>Owns cancellable server-only downloads into isolated, verified staging outside the running application.</summary>
public interface IDistributionJobs
{
    /// <summary>Reads one configured-size history page, newest admission first, without loading the full staging history into memory.</summary>
    DistributionDownloadJobPage List(string? after = null);

    /// <summary>Admits one supervised download using the exact selected plan and waits for its first persisted receipt; accepted work continues independently of the HTTP request.</summary>
    ValueTask<DistributionDownloadJob> StartAsync(string planId, CancellationToken cancellationToken = default);

    /// <summary>Reads current progress or a stored terminal receipt; an interrupted process is never reported as still running.</summary>
    DistributionDownloadJob Get(string jobId);

    /// <summary>Rechecks stored inventory identity and every staged payload's exact size and digest while preventing concurrent job deletion.</summary>
    ValueTask<DistributionStagingVerification> VerifyAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation of an active download without deleting a terminal receipt or any other job.</summary>
    DistributionDownloadJob Cancel(string jobId);

    /// <summary>Deletes one terminal job and its owned staging after validating its identity; active jobs must be cancelled first.</summary>
    bool Remove(string jobId);
}
