using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>
/// Configures and reads the distribution sources over HTTP: plain HTTP(S) catalogs, AList servers
/// (metadata through <c>api/fs/get</c>, downloads through the returned address or <c>d/&lt;path&gt;</c>)
/// and GitHub repositories (release metadata through the REST API, catalog documents through raw
/// content). Every document read is bounded by the configured byte budget and deadline, every URL is
/// revalidated to stay credential-free, and HTTPS is never downgraded, including across redirects and
/// AList's returned address. The GitHub mirror switch is not wired: enabling it fails here rather
/// than pretending to accelerate anything. Source edits persist by optimistic revision: the state
/// file is replaced through flush-to-disk plus rename under the settings gate, so a crash keeps the
/// previous revision readable and a stale editor's save is refused instead of silently overwriting.
/// </summary>
internal sealed partial class DistributionSources : IDistributionSources, IDistributionTransport
{
    private readonly HttpClient _http;
    private readonly int _maximumBytes;
    private readonly int _timeoutMs;
    private readonly int _redirects;
    private readonly long _maximumPayloadBytes;
    private readonly int _transferTimeoutMs;
    private readonly CancellationToken _lifetime;
    private IReadOnlyList<DistributionSource> _sources;
    private readonly Lock _settingsGate = new();
    private readonly string? _statePath;
    private readonly int _maximumSources;
    private long _revision;

    internal DistributionSources(HttpClient http, DistributionOptions options, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.UseGithubMirror) throw new NotSupportedException("GitHub mirror support is TODO.");
        _http = http;
        _maximumBytes = options.MaximumDocumentBytes;
        _timeoutMs = options.RequestTimeoutMs;
        _redirects = options.MaximumRedirects;
        _maximumPayloadBytes = options.MaximumPayloadBytes ?? long.MaxValue;
        _transferTimeoutMs = options.TransferTimeoutMs;
        _lifetime = lifetime;
        _maximumSources = options.MaximumSources;
        _statePath = string.IsNullOrWhiteSpace(options.StatePath) ? null : Path.GetFullPath(options.StatePath);
        _sources = ValidateSources(options.Sources.Select(row => new DistributionSource(row.Id, row.Name, row.Kind, row.BaseUrl, row.CatalogPath)));
        if (_statePath is not null && File.Exists(_statePath))
        {
            using var input = File.OpenRead(_statePath);
            if (input.Length > _maximumBytes) throw new InvalidDataException("The source settings exceed their byte budget.");
            var state = JsonSerializer.Deserialize<DistributionSourceSettings>(input) ?? throw new InvalidDataException("The source settings are empty.");
            if (state.Revision < 0) throw new InvalidDataException("The source settings revision is invalid.");
            _sources = ValidateSources(state.Sources);
            _revision = state.Revision;
        }
    }

    private IReadOnlyList<DistributionSource> ValidateSources(IEnumerable<DistributionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var rows = new List<DistributionSource>();
        foreach (var row in sources)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (rows.Count >= _maximumSources) throw new ArgumentException("The configured source count exceeds its budget.");
            ArgumentException.ThrowIfNullOrWhiteSpace(row.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(row.Name);
            if (rows.Any(source => source.Id == row.Id)) throw new ArgumentException("Duplicate distribution source identity.");
            var root = PublicUrl(row.BaseUrl);
            if (root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("Source roots cannot contain a query or fragment.");
            if (!root.AbsolutePath.EndsWith('/')) root = new Uri(root.AbsoluteUri + "/");
            if (row.Kind == "github") _ = GithubRoot(root);
            else if (row.Kind is not ("http" or "alist")) throw new ArgumentException("Source kind must be http, alist or github.");
            _ = RelativePath(row.CatalogPath);
            rows.Add(new(row.Id, row.Name, row.Kind, root.AbsoluteUri, row.CatalogPath));
        }
        return rows.AsReadOnly();
    }

    public IReadOnlyList<DistributionSource> Sources { get { lock (_settingsGate) return _sources; } }
    public DistributionSourceSettings Settings { get { lock (_settingsGate) return new(_revision, _sources); } }

    /// <summary>
    /// Persists the complete validated source list when <paramref name="expectedRevision"/> is still
    /// the current revision, bumping the revision by one. Source editing requires a configured state
    /// path; without one the store is read-only and this throws.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown when no state path is configured.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the expected revision no longer matches (<c>source_revision_conflict</c>).</exception>
    /// <exception cref="ArgumentException">Thrown for an invalid source row or a list that exceeds the configured byte or count budgets.</exception>
    public ValueTask<DistributionSourceSettings> SaveSourcesAsync(long expectedRevision, IReadOnlyList<DistributionSource> sources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lifetime.ThrowIfCancellationRequested();
        if (_statePath is null) throw new NotSupportedException("Source editing requires a configured state_path.");
        var validated = ValidateSources(sources);
        lock (_settingsGate)
        {
            if (_revision != expectedRevision) throw new InvalidOperationException("source_revision_conflict");
            var next = new DistributionSourceSettings(checked(_revision + 1), validated);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            if (bytes.Length > _maximumBytes) throw new ArgumentException("Source settings exceed their byte budget.");
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(flushToDisk: true); }
                cancellationToken.ThrowIfCancellationRequested();
                _lifetime.ThrowIfCancellationRequested();
                File.Move(temporary, _statePath, overwrite: true);
                _sources = validated;
                _revision = next.Revision;
                return ValueTask.FromResult(next);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public async ValueTask<byte[]> ReadCatalogAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var source = Sources.SingleOrDefault(row => row.Id == sourceId)
            ?? throw new KeyNotFoundException("The distribution source is not configured.");
        return await ReadCatalogAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadCatalogAsync(DistributionSource source, CancellationToken cancellationToken)
        => await ReadDocumentAsync(source, source.CatalogPath, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads one source-relative document within the metadata byte budget and request deadline. For an
    /// AList source the size returned by the metadata lookup must agree with the delivered bytes, so a
    /// lying resolver fails the read instead of feeding a truncated catalog downstream.
    /// </summary>
    public async ValueTask<byte[]> ReadDocumentAsync(DistributionSource source, string relativePath, CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken);
        var (address, expectedSize) = await ResolveFileAsync(source, relativePath, _maximumBytes, deadline.Token).ConfigureAwait(false);
        var bytes = await GetAsync(address, deadline.Token).ConfigureAwait(false);
        if (expectedSize is { } size && size != bytes.LongLength) throw new InvalidDataException("The document length disagrees with AList metadata.");
        return bytes;
    }

    private async Task<(Uri Address, long? ExpectedSize)> ResolveFileAsync(DistributionSource source, string relativePath, long maximumBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = RelativePath(relativePath);
        var root = new Uri(source.BaseUrl);
        _ = PublicUrl(source.BaseUrl);
        if (root.Query.Length != 0 || root.Fragment.Length != 0 || !root.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("A source snapshot requires a normalized root without query or fragment.");
        Uri address;
        long? expectedSize = null;
        if (source.Kind == "alist")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/fs/get"))
            { Content = JsonContent.Create(new { path = "/" + relativePath, password = "" }) };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, token).ConfigureAwait(false));
            var envelope = json.RootElement;
            if (envelope.GetProperty("code").GetInt32() != 200) throw new InvalidDataException("AList rejected the file lookup.");
            var data = envelope.GetProperty("data");
            if (data.GetProperty("is_dir").GetBoolean()) throw new InvalidDataException("The source path names a directory.");
            expectedSize = data.GetProperty("size").GetInt64();
            if (expectedSize < 0 || expectedSize > maximumBytes) throw new InvalidDataException("The source file exceeds its byte budget.");
            var raw = data.TryGetProperty("raw_url", out var rawValue) ? rawValue.GetString() : null;
            var sign = data.TryGetProperty("sign", out var signValue) ? signValue.GetString() : null;
            address = string.IsNullOrEmpty(raw)
                ? new Uri(root, "d/" + path + (string.IsNullOrEmpty(sign) ? "" : "?sign=" + Uri.EscapeDataString(sign)))
                : PublicUrl(raw);
            if (root.Scheme == "https" && address.Scheme != "https") throw new InvalidDataException("AList file resolution cannot downgrade HTTPS.");
        }
        else if (source.Kind is "http" or "github") address = new Uri(source.Kind == "github" ? GithubRoot(root) : root, path);
        else throw new ArgumentException("Source kind must be http, alist or github.");
        return (address, expectedSize);
    }

    /// <summary>
    /// Resolves one GitHub repository to its latest published stable release, or to the exact tag when
    /// one is named. Draft and (when browsing latest) prerelease releases are refused, the release page
    /// and every asset URL must belong to the requested repository and tag on github.com over HTTPS,
    /// duplicate asset identities are refused, and a <c>sha256:</c> digest field is carried through as
    /// the asset's binding hash. The returned record describes the release; nothing is downloaded by
    /// this call.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown for a non-GitHub or malformed repository URL.</exception>
    /// <exception cref="InvalidDataException">Thrown when the release data is unpublished, belongs to a different
    /// repository or tag, or carries invalid or duplicate asset identities.</exception>
    public async ValueTask<RepositoryRelease> ReadRepositoryReleaseAsync(string repositoryUrl, string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var repository = Repository(repositoryUrl);
        if (tag is not null) ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        using var deadline = Deadline(cancellationToken);
        var suffix = tag is null ? "latest" : "tags/" + Uri.EscapeDataString(tag);
        var bytes = await GetAsync(new Uri($"https://api.github.com/repos/{repository}/releases/{suffix}"), deadline.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || tag is null && release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The repository did not return a published stable release.");
        var selectedTag = RequiredText(release, "tag_name");
        if (tag is not null && tag != selectedTag) throw new InvalidDataException("The repository returned a different release tag.");
        var releaseId = release.GetProperty("id").GetInt64();
        if (releaseId <= 0) throw new InvalidDataException("The release identity is invalid.");
        var page = PublicUrl(RequiredText(release, "html_url"));
        if (page.Scheme != "https" || page.Host != "github.com" || !page.AbsolutePath.StartsWith("/" + repository + "/releases/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release page belongs to a different repository.");
        var assets = new List<RepositoryAsset>();
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var id = asset.GetProperty("id").GetInt64();
            var name = RequiredText(asset, "name");
            var size = asset.GetProperty("size").GetInt64();
            var url = PublicUrl(RequiredText(asset, "browser_download_url"));
            if (id <= 0 || size < 0 || assets.Any(row => row.Id == id || row.Name == name))
                throw new InvalidDataException("The release contains invalid or duplicate attachment identities.");
            if (url.Scheme != "https" || url.Host != "github.com" ||
                !url.AbsolutePath.StartsWith("/" + repository + "/releases/download/" + Uri.EscapeDataString(selectedTag) + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release attachment belongs to a different repository or tag.");
            string? hash = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
            {
                var text = digest.GetString() ?? "";
                if (!text.StartsWith("sha256:", StringComparison.Ordinal) || text.Length != 71 || text[7..].Any(c => !char.IsAsciiHexDigit(c)))
                    throw new InvalidDataException("The attachment digest is not a SHA-256 identity.");
                hash = text[7..].ToLowerInvariant();
            }
            assets.Add(new(id, name, size, url.AbsoluteUri, hash));
        }
        return new(repository, releaseId, selectedTag, release.TryGetProperty("name", out var title) ? title.GetString() ?? selectedTag : selectedTag,
            release.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "", page.AbsoluteUri, assets.AsReadOnly());
    }

    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        deadline.CancelAfter(_timeoutMs);
        return deadline;
    }

    private async Task<byte[]> GetAsync(Uri address, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            _ = PublicUrl(address.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("BubbleFighter-Fantasy-Distribution/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= _redirects || response.Headers.Location is not { } location) throw new InvalidDataException("The metadata redirect limit was reached.");
                var next = new Uri(address, location);
                if (address.Scheme == "https" && next.Scheme != "https") throw new InvalidDataException("Metadata redirects cannot downgrade HTTPS.");
                address = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > _maximumBytes) throw new InvalidDataException("The metadata document exceeds its byte budget.");
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > _maximumBytes) throw new InvalidDataException("The metadata document exceeds its byte budget.");
            output.Write(buffer, 0, count);
        }
    }

    private static string RequiredText(JsonElement element, string key)
    {
        var text = element.GetProperty(key).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw new InvalidDataException("The repository returned an invalid " + key + ".");
        return text;
    }

    private static Uri PublicUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0)
            throw new ArgumentException("Distribution URLs must use HTTP or HTTPS without embedded credentials.");
        return url;
    }

    private static string RelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains('%') || value.Contains(':') || value.Contains('?') || value.Contains('#') ||
            value.Any(char.IsControl) || value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Catalog paths must contain plain relative path segments.");
        return string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
    }

    private static string Repository(string value)
    {
        var url = PublicUrl(value);
        var parts = url.AbsolutePath.Trim('/').Split('/');
        if (url.Scheme != "https" || url.Host is not ("github.com" or "www.github.com") || !url.IsDefaultPort ||
            url.Query.Length != 0 || url.Fragment.Length != 0 || parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Use a GitHub repository URL with an owner and repository name.");
        var repo = parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1];
        if (repo.Length == 0 || parts[0].Concat(repo).Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("The GitHub repository identity is invalid.");
        return parts[0] + "/" + repo;
    }

    private static Uri GithubRoot(Uri root)
    {
        if (root.Scheme != "https" || !root.IsDefaultPort) throw new ArgumentException("GitHub roots require HTTPS on its default port.");
        if (root.Host == "raw.githubusercontent.com" || root.Host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase)) return root;
        if (root.Host is not ("github.com" or "www.github.com")) throw new ArgumentException("The GitHub source host is invalid.");
        var parts = root.AbsolutePath.Trim('/').Split('/');
        if (parts.Length == 2) return new Uri("https://raw.githubusercontent.com/" + Repository(root.AbsoluteUri) + "/HEAD/");
        if (parts.Length >= 5 && parts[2] == "releases" && parts[3] == "download") return root;
        if (parts.Length >= 4 && parts[2] is "tree" or "blob")
            return new Uri("https://raw.githubusercontent.com/" + string.Join('/', parts.Take(2).Concat(parts.Skip(3))) + "/");
        throw new ArgumentException("Use a repository, tree, raw content or release asset root.");
    }
}
