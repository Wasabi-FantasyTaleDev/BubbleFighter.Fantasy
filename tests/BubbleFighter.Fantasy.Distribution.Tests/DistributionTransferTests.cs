using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionTransferTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified plugin payload");
    private static string Digest => Convert.ToHexStringLower(SHA256.HashData(Payload));

    [Theory]
    [InlineData("http", "https://files.example/store/", "https://files.example/store/objects/plugin.dll")]
    [InlineData("github", "https://github.com/owner/store/", "https://raw.githubusercontent.com/owner/store/HEAD/objects/plugin.dll")]
    public async Task FileTransferUsesSelectedSourceRootAndDoesNotCloseCallerStaging(string kind, string root, string expected)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(expected, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Reply(Payload));
        });
        using var http = new HttpClient(handler);
        var service = new DistributionSources(http, new());
        using var staging = new MemoryStream();
        await service.CopyFileAsync(Source(kind, root), "objects/plugin.dll", Payload.Length, Digest.ToUpperInvariant(), staging);
        Assert.Equal(Payload, staging.ToArray());
        Assert.True(staging.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlistFileTransferResolvesThePayloadPathAndChecksPublisherSize(bool raw)
    {
        var calls = 0;
        using var handler = new Handler(async (request, token) =>
        {
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
                Assert.Equal("/objects/plugin.dll", body.RootElement.GetProperty("path").GetString());
                return JsonReply(new { code = 200, data = new { is_dir = false, size = Payload.Length,
                    raw_url = raw ? "https://objects.example/plugin" : "", sign = "a+b" } });
            }
            Assert.Equal(raw ? "https://objects.example/plugin" : "https://files.example/d/objects/plugin.dll?sign=a%2Bb", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Reply(Payload);
        });
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        await new DistributionSources(http, new()).CopyFileAsync(Source("alist"), "objects/plugin.dll", Payload.Length, Digest, staging);
        Assert.Equal(2, calls);
        Assert.Equal(Payload, staging.ToArray());
    }

    [Theory]
    [InlineData("size")]
    [InlineData("downgrade")]
    public async Task InvalidAlistResolutionFailsBeforeReadingPayload(string failure)
    {
        var calls = 0;
        using var handler = new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonReply(new { code = 200, data = new { is_dir = false,
                size = failure == "size" ? Payload.Length + 1 : Payload.Length, raw_url = failure == "downgrade" ? "http://objects.example/plugin" : "https://objects.example/plugin" } }));
        });
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => new DistributionSources(http, new()).CopyFileAsync(Source("alist"), "plugin.dll", Payload.Length, Digest, staging).AsTask());
        Assert.Equal(1, calls);
        Assert.Equal(0, staging.Length);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("digest")]
    public async Task ChunkedPayloadMustMatchActualLengthAndDigest(string failure)
    {
        var bytes = failure switch { "short" => Payload[..^1], "long" => Payload.Concat(new byte[] { 99 }).ToArray(), _ => Payload };
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekableStream(new MemoryStream(bytes))) }));
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => new DistributionSources(http, new()).CopyFileAsync(Source(), "plugin.dll", Payload.Length,
            failure == "digest" ? new string('a', 64) : Digest, staging).AsTask());
        Assert.True(staging.CanWrite);
        Assert.True(staging.Length <= Payload.Length);
    }

    [Fact]
    public async Task OptionalOperatorLimitDoesNotImposeAnEightGiBDefault()
    {
        var reachedNetwork = false;
        using var handler = new Handler((_, _) =>
        {
            reachedNetwork = true;
            return Task.FromResult(Reply(Payload));
        });
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        Assert.Null(new DistributionOptions().MaximumPayloadBytes);
        // A large declared file reaches the header check without allocating or transferring its declared size.
        await Assert.ThrowsAsync<InvalidDataException>(() => new DistributionSources(http, new()).CopyFileAsync(Source(), "large.zip", 8589934593, Digest, staging).AsTask());
        Assert.True(reachedNetwork);
        reachedNetwork = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new DistributionSources(http, new() { MaximumPayloadBytes = 10 }).CopyFileAsync(Source(), "plugin.dll", Payload.Length, Digest, staging).AsTask());
        Assert.False(reachedNetwork);
    }

    [Theory]
    [InlineData("../plugin.dll")]
    [InlineData("https://other.example/plugin.dll")]
    [InlineData("%2e%2e/plugin.dll")]
    public async Task PayloadPathCannotEscapeTheSelectedSource(string path)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected network request."));
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => new DistributionSources(http, new()).CopyFileAsync(Source(), path, Payload.Length, Digest, staging).AsTask());
    }

    [Fact]
    public async Task RepositoryAttachmentWithoutPublisherDigestIsNotSilentlyTrusted()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected network request."));
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        var asset = new RepositoryAsset(45, "plugin.zip", Payload.Length, "https://github.com/owner/plugin/releases/download/v1/plugin.zip", null);
        await Assert.ThrowsAsync<InvalidDataException>(() => new DistributionSources(http, new()).CopyRepositoryAssetAsync(asset, staging).AsTask());
    }

    [Fact]
    public async Task RepositoryAttachmentFollowsPublishedRedirectAndVerifiesBytes()
    {
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            if (++calls == 1)
            {
                Assert.Equal("github.com", request.RequestUri!.Host);
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/object");
                return Task.FromResult(response);
            }
            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri!.Host);
            return Task.FromResult(Reply(Payload));
        });
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        await new DistributionSources(http, new()).CopyRepositoryAssetAsync(new(45, "plugin.zip", Payload.Length,
            "https://github.com/owner/plugin/releases/download/v1/plugin.zip", Digest), staging);
        Assert.Equal(Payload, staging.ToArray());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("http://files.example/plugin.dll")]
    [InlineData("https://user:password@files.example/plugin.dll")]
    [InlineData("https://files.example/loop")]
    public async Task PayloadRedirectsRefuseDowngradeCredentialsAndLoops(string destination)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(destination);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var staging = new MemoryStream();
        var error = await Record.ExceptionAsync(() => new DistributionSources(http, new() { MaximumRedirects = 1 }).CopyFileAsync(Source(), "plugin.dll", Payload.Length, Digest, staging).AsTask());
        Assert.True(error is ArgumentException or InvalidDataException);
        Assert.Equal(0, staging.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerOrPluginCancellationStopsInflightTransfer(bool pluginCancellation)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable.");
        });
        using var http = new HttpClient(handler);
        using var cancelled = new CancellationTokenSource();
        using var staging = new MemoryStream();
        var service = new DistributionSources(http, new(), pluginCancellation ? cancelled.Token : default);
        var pending = service.CopyFileAsync(Source(), "plugin.dll", Payload.Length, Digest, staging,
            pluginCancellation ? default : cancelled.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, staging.Length);
    }

    [Fact]
    public async Task DocumentReaderUsesTheSameSelectedRootForManifestMetadata()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://files.example/releases/v1/release.toml", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Reply(Encoding.UTF8.GetBytes("schema = 1")));
        });
        using var http = new HttpClient(handler);
        Assert.Equal("schema = 1", Encoding.UTF8.GetString(await new DistributionSources(http, new()).ReadDocumentAsync(Source(), "releases/v1/release.toml")));
    }

    private static DistributionSource Source(string kind = "http", string root = "https://files.example/") => new("chosen", "Chosen", kind, root, "source.toml");
    private static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage JsonReply(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }

    private sealed class NonSeekableStream(Stream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
