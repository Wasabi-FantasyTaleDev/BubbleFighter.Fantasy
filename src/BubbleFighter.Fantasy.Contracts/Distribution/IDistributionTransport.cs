namespace BubbleFighter.Fantasy.Contracts.Distribution;

/// <summary>Reads metadata and verifies streamed payloads using the same source-root and transport rules.</summary>
public interface IDistributionTransport
{
    /// <summary>Reads one bounded document from an immutable selected source rather than looking up a possibly edited source ID.</summary>
    ValueTask<byte[]> ReadDocumentAsync(DistributionSource source, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Copies one source-relative payload to caller-owned staging and succeeds only after its exact size and SHA-256 match.</summary>
    /// <remarks>The caller must discard staging after any exception; bytes written before verification are not trusted installation content.</remarks>
    ValueTask CopyFileAsync(DistributionSource source, string relativePath, long expectedSize, string sha256,
        Stream staging, CancellationToken cancellationToken = default);

    /// <summary>Copies an explicitly selected repository attachment to staging, refusing attachments without a publisher SHA-256 digest.</summary>
    /// <remarks>This checks attachment bytes, not plugin compatibility, installation permission or publisher signatures.</remarks>
    ValueTask CopyRepositoryAssetAsync(RepositoryAsset asset, Stream staging, CancellationToken cancellationToken = default);
}
