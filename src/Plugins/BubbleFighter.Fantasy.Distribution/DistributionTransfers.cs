using System.Buffers;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>
/// The transfer half of <see cref="DistributionSources"/>: copies one verified payload into
/// caller-owned staging over HTTP(S). Every copy is bounded by the configured payload budget and
/// transfer deadline, follows the shared redirect cap without ever downgrading HTTPS, checks the
/// Content-Length when the server sends one, reads exactly the declared number of bytes (one extra
/// byte is requested at the boundary so an oversized chunked response fails instead of truncating),
/// and hashes while copying so the staging stream only ever receives bytes that matched the
/// publisher's SHA-256 in constant time. A failed copy may have written bytes; the caller owns the
/// staging stream and must discard it after any exception.
/// </summary>
internal sealed partial class DistributionSources
{
    /// <summary>Copies one source-relative payload after re-checking the AList metadata length, when the source publishes one.</summary>
    public async ValueTask CopyFileAsync(DistributionSource source, string relativePath, long expectedSize, string sha256,
        Stream staging, CancellationToken cancellationToken = default)
    {
        ValidatePayload(expectedSize, sha256, staging);
        using var deadline = TransferDeadline(cancellationToken);
        deadline.Token.ThrowIfCancellationRequested();
        var (address, sourceSize) = await ResolveFileAsync(source, relativePath, _maximumPayloadBytes, deadline.Token).ConfigureAwait(false);
        if (sourceSize is { } size && size != expectedSize) throw new InvalidDataException("AList length disagrees with the selected release.");
        await CopyVerifiedAsync(address, expectedSize, sha256, staging, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies one GitHub release attachment to staging. The attachment must already carry an exact id
    /// and a publisher SHA-256, and its URL must be the asset's own published HTTPS download address
    /// on github.com without query or fragment; the copy verifies every received byte against that
    /// digest. This checks attachment bytes only — not package compatibility, not installation
    /// permission, not a publisher signature.
    /// </summary>
    public async ValueTask CopyRepositoryAssetAsync(RepositoryAsset asset, Stream staging, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id <= 0 || string.IsNullOrWhiteSpace(asset.Sha256))
            throw new InvalidDataException("A repository attachment requires an exact identity and publisher SHA-256 before download.");
        var address = PublicUrl(asset.DownloadUrl);
        if (address.Scheme != "https" || address.Host != "github.com" || !address.IsDefaultPort ||
            address.Query.Length != 0 || address.Fragment.Length != 0 || !address.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal))
            throw new ArgumentException("A repository attachment must use its published HTTPS download URL.");
        ValidatePayload(asset.Size, asset.Sha256, staging);
        using var deadline = TransferDeadline(cancellationToken);
        deadline.Token.ThrowIfCancellationRequested();
        await CopyVerifiedAsync(address, asset.Size, asset.Sha256, staging, deadline.Token).ConfigureAwait(false);
    }

    private void ValidatePayload(long size, string sha256, Stream staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!staging.CanWrite) throw new ArgumentException("Payload staging must be writable.");
        if (size < 0 || size > _maximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(size), "The payload exceeds its configured byte budget.");
        if (sha256 is not { Length: 64 } || sha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("A publisher SHA-256 digest is required.");
    }

    private CancellationTokenSource TransferDeadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        deadline.CancelAfter(_transferTimeoutMs);
        return deadline;
    }

    // A GitHub payload tries the mirrors first, fastest measured first, and GitHub itself last. Every route is held to the
    // same size and SHA-256 that GitHub published, so a mirror can only fail a download, never change what is staged.
    private async Task CopyVerifiedAsync(Uri address, long expectedSize, string sha256, Stream staging, CancellationToken token)
    {
        var routes = _mirrors is not null && GithubMirrors.IsPayload(address) && staging.CanSeek
            ? await _mirrors.RoutesAsync(address, token).ConfigureAwait(false) : [];
        var start = routes.Count > 0 ? staging.Position : 0;
        foreach (var route in routes)
        {
            try
            {
                await CopyFromAsync(route, expectedSize, sha256, staging, _mirrors!.RequestTimeout, token).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                staging.SetLength(start);
                staging.Position = start;
            }
        }
        await CopyFromAsync(address, expectedSize, sha256, staging, null, token).ConfigureAwait(false);
    }

    private async Task CopyFromAsync(Uri address, long expectedSize, string sha256, Stream staging, TimeSpan? headerTimeout, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            token.ThrowIfCancellationRequested();
            _ = PublicUrl(address.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("BubbleFighter-Fantasy-Distribution/1.0");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            // A mirror that does not answer within its deadline yields to the next route; the body keeps the transfer deadline.
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (headerTimeout is { } limit) headers.CancelAfter(limit);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= _redirects || response.Headers.Location is not { } location)
                    throw new InvalidDataException("The payload redirect limit was reached.");
                var next = new Uri(address, location);
                if (address.Scheme == "https" && next.Scheme != "https") throw new InvalidDataException("Payload redirects cannot downgrade HTTPS.");
                address = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (headerTimeout is not null && response.Content.Headers.ContentType?.MediaType == "text/html")
                throw new InvalidDataException("The mirror answered with a web page.");
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedSize)
                throw new InvalidDataException("The payload Content-Length disagrees with the selected release.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                long total = 0;
                while (true)
                {
                    // Read one extra byte at the expected boundary to detect an oversized chunked response.
                    var remaining = expectedSize - total;
                    var requested = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
                    var count = await input.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (count > expectedSize - total) throw new InvalidDataException("The payload exceeds the selected release size.");
                    hash.AppendData(buffer, 0, count);
                    await staging.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    total += count;
                }
                if (total != expectedSize) throw new InvalidDataException("The payload ended before the selected release size.");
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(sha256)))
                    throw new InvalidDataException("The payload SHA-256 disagrees with the selected release.");
                token.ThrowIfCancellationRequested();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return;
        }
    }
}
