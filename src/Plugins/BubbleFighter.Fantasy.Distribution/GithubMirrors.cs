using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>The packaged mirror list and measurement settings, read from the file the row names.</summary>
internal sealed record GithubMirrorConfiguration(Uri ProbeUrl, byte[] ProbePrefix, int SampleBytes, TimeSpan Timeout, int Parallelism,
    Uri? DirectoryUrl, Uri? CountryLookupUrl, IReadOnlyList<(GithubMirrorProvider Provider, Uri Address, bool Snapshot)> Mirrors)
{
    internal static GithubMirrorConfiguration Load(string path)
    {
        using var input = File.OpenRead(path);
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        var mirrors = root.GetProperty("mirrors").EnumerateArray().Select(row =>
        {
            var address = new Uri(row.GetProperty("url").GetString()!);
            if (!GithubMirrors.ValidMirror(address)) throw new InvalidDataException("A GitHub mirror needs a plain HTTPS origin.");
            return (new GithubMirrorProvider(row.GetProperty("id").GetString()!, row.GetProperty("name").GetString()!), address,
                row.TryGetProperty("directorySnapshot", out var snapshot) && snapshot.GetBoolean());
        }).ToArray();
        if (mirrors.Length == 0 || mirrors.Any(row => row.Item1.Id is "" or "auto") ||
            mirrors.Select(row => row.Item1.Id).Distinct(StringComparer.Ordinal).Count() != mirrors.Length)
            throw new InvalidDataException("GitHub mirrors need unique ids other than auto.");
        Uri? Optional(string key) => root.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } text ? HttpsOnly(new Uri(text)) : null;
        var probe = HttpsOnly(new Uri(root.GetProperty("probeUrl").GetString()!));
        if (!GithubMirrors.IsGithub(probe)) throw new InvalidDataException("The mirror probe must be a GitHub file.");
        var prefix = Convert.FromHexString(root.GetProperty("probePrefix").GetString()!);
        var sample = root.GetProperty("sampleBytes").GetInt32();
        var timeout = TimeSpan.FromMilliseconds(root.GetProperty("timeoutMs").GetInt32());
        var parallelism = root.GetProperty("parallelism").GetInt32();
        if (prefix.Length == 0 || sample < prefix.Length || timeout <= TimeSpan.Zero || parallelism < 1)
            throw new InvalidDataException("The mirror measurement settings are invalid.");
        return new(probe, prefix, sample, timeout, parallelism, Optional("directoryUrl"), Optional("countryLookupUrl"), mirrors);
    }

    private static Uri HttpsOnly(Uri address) => address.Scheme == "https" && address.UserInfo.Length == 0
        ? address : throw new InvalidDataException("Mirror settings use public HTTPS addresses.");
}

/// <summary>
/// Measures the configured mirrors, keeps the operator's choice in its state file, and orders the routes of one
/// GitHub payload. The choice and the one-time location check survive restarts; measurements do not.
/// </summary>
internal sealed partial class GithubMirrors : IDistributionGithubMirror, IDisposable
{
    private readonly GithubMirrorConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly string? _statePath;
    private readonly SemaphoreSlim _measuring = new(1, 1);
    private readonly Lock _gate = new();
    private (GithubMirrorProvider Provider, Uri Address)[] _providers;
    private GithubMirrorResult[] _results;
    private DateTimeOffset _measuredAt;
    private Saved _saved;
    private bool _detecting;

    private sealed record Saved(bool Enabled = false, string Selection = "auto", bool Checked = false, bool Prompted = false, bool Pending = false);

    internal GithubMirrors(GithubMirrorConfiguration configuration, string? statePath, HttpMessageHandler? handler = null)
    {
        _configuration = configuration;
        _statePath = statePath is { Length: > 0 } ? Path.GetFullPath(statePath) : null;
        _providers = configuration.Mirrors.Select(row => (row.Provider, row.Address)).ToArray();
        _results = _providers.Select(row => new GithubMirrorResult(row.Provider.Id, "pending")).ToArray();
        _http = new(handler ?? new SocketsHttpHandler { UseCookies = false, MaxConnectionsPerServer = 2, ConnectTimeout = configuration.Timeout })
        { Timeout = Timeout.InfiniteTimeSpan };
        _saved = _statePath is not null && File.Exists(_statePath)
            ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(_statePath)) ?? new() : new();
        // A selection saved from a directory refresh outlives the packaged snapshot that predates it.
        if (_saved.Selection != "auto" && !_providers.Any(row => row.Provider.Id == _saved.Selection) &&
            Uri.TryCreate("https://" + _saved.Selection + "/", UriKind.Absolute, out var kept) && ValidMirror(kept) && kept.IdnHost == _saved.Selection)
        {
            _providers = [.. _providers, (new GithubMirrorProvider(_saved.Selection, _saved.Selection), kept)];
            _results = [.. _results, new GithubMirrorResult(_saved.Selection, "pending")];
        }
    }

    /// <summary>The per-attempt deadline for a mirror's response headers.</summary>
    internal TimeSpan RequestTimeout => _configuration.Timeout;

    internal bool Enabled { get { lock (_gate) return _saved.Enabled; } }

    public GithubMirrorState State { get { lock (_gate) return new(_saved.Enabled, _saved.Selection, _saved.Pending && !_saved.Prompted, _detecting); } }

    public IReadOnlyList<GithubMirrorProvider> Providers { get { lock (_gate) return _providers.Select(row => row.Provider).ToArray(); } }

    public IReadOnlyList<GithubMirrorResult> Results { get { lock (_gate) return _results.ToArray(); } }

    public GithubMirrorState Save(bool? enabled, string? selection)
    {
        lock (_gate)
        {
            if (selection is not null && selection != "auto" && !_providers.Any(row => row.Provider.Id == selection))
                throw new ArgumentException("Unknown GitHub mirror selection.");
            // A deliberate choice settles the location suggestion as well.
            Write(_saved with { Enabled = enabled ?? _saved.Enabled, Selection = selection ?? _saved.Selection,
                Checked = enabled.HasValue || _saved.Checked, Pending = !enabled.HasValue && _saved.Pending });
        }
        return State;
    }

    public GithubMirrorState MarkSuggestionShown()
    {
        lock (_gate) if (_saved.Pending && !_saved.Prompted) Write(_saved with { Pending = false, Prompted = true });
        return State;
    }

    /// <summary>Checks once where the server is; a mainland-China address queues the suggestion without enabling anything.</summary>
    internal async Task DetectCountryAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { if (_saved.Checked || _configuration.CountryLookupUrl is null) return; _detecting = true; }
        string? country = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_configuration.Timeout);
            using var response = await _http.GetAsync(_configuration.CountryLookupUrl, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("country_code", out var code) &&
                code.ValueKind == JsonValueKind.String && code.GetString() is { Length: 2 } value)
                country = value.ToUpperInvariant();
        }
        // An unreachable lookup is treated as "not detected"; it never blocks the store.
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                _detecting = false;
                if (!cancellationToken.IsCancellationRequested && !_saved.Checked) Write(_saved with { Checked = true, Pending = country == "CN" });
            }
        }
    }

    public async Task MeasureAsync(CancellationToken cancellationToken)
    {
        await _measuring.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await MeasureCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _measuring.Release(); }
    }

    /// <summary>The mirrored addresses to try before the original, fastest measured first; empty when mirrors are off.</summary>
    internal async Task<IReadOnlyList<Uri>> RoutesAsync(Uri address, CancellationToken cancellationToken)
    {
        string selection;
        lock (_gate) { if (!_saved.Enabled) return []; selection = _saved.Selection; }
        if (selection == "auto")
        {
            // Automatic choice measures before each payload, so a mirror that went down since the last test is skipped.
            var requested = DateTimeOffset.UtcNow;
            await _measuring.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { if (_measuredAt < requested) await MeasureCoreAsync(cancellationToken).ConfigureAwait(false); }
            finally { _measuring.Release(); }
        }
        lock (_gate)
        {
            var results = _results.ToDictionary(row => row.Id, StringComparer.Ordinal);
            return _providers
                .Where(row => selection != "auto" || results[row.Provider.Id].State == "available")
                .OrderBy(row => row.Provider.Id == selection ? 0 : 1)
                .ThenBy(row => results[row.Provider.Id].State == "available" ? 0 : 1)
                .ThenByDescending(row => results[row.Provider.Id].BytesPerSecond ?? 0)
                .Select(row => Route(address, row.Address)).ToArray();
        }
    }

    /// <summary>Whether an address is a GitHub release attachment or archive, the only downloads a mirror carries.</summary>
    internal static bool IsPayload(Uri address) => address.Scheme == "https" && address.UserInfo.Length == 0 && address.IsDefaultPort &&
        (address.Host == "github.com" && (address.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal) ||
            address.AbsolutePath.Contains("/archive/", StringComparison.Ordinal)) ||
         address.Host is "objects.githubusercontent.com" or "release-assets.githubusercontent.com" or "codeload.github.com");

    internal static bool IsGithub(Uri address) => address.Host is "github.com" or "www.github.com" or "api.github.com" or "codeload.github.com" ||
        address.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    internal static bool ValidMirror(Uri address) => address.Scheme == "https" && address.UserInfo.Length == 0 && address.AbsolutePath == "/" &&
        address.Query.Length == 0 && address.Fragment.Length == 0 && address.HostNameType == UriHostNameType.Dns;

    // URL-prefix mirrors take the complete original address after their own origin.
    private static Uri Route(Uri address, Uri mirror) => new(mirror.AbsoluteUri.TrimEnd('/') + "/" + address.AbsoluteUri);

    private async Task MeasureCoreAsync(CancellationToken cancellationToken)
    {
        await RefreshFromDirectoryAsync(cancellationToken).ConfigureAwait(false);
        (GithubMirrorProvider Provider, Uri Address)[] providers;
        lock (_gate)
        {
            _measuredAt = default;
            _results = _providers.Select(row => new GithubMirrorResult(row.Provider.Id, "pending")).ToArray();
            providers = _providers.ToArray();
        }
        await Parallel.ForEachAsync(providers, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _configuration.Parallelism },
            async (row, token) =>
            {
                Update(new(row.Provider.Id, "testing"));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_configuration.Timeout);
                var watch = Stopwatch.StartNew();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Route(_configuration.ProbeUrl, row.Address));
                    request.Headers.Range = new RangeHeaderValue(0, _configuration.SampleBytes - 1);
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType == "text/html") throw new InvalidDataException("The mirror returned a web page.");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var buffer = new byte[_configuration.SampleBytes];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        read += count;
                    }
                    // The sample must start like the known file, so a mirror answering with anything else is not counted.
                    if (read < _configuration.ProbePrefix.Length || !buffer.AsSpan(0, _configuration.ProbePrefix.Length).SequenceEqual(_configuration.ProbePrefix))
                        throw new InvalidDataException("The mirror sample has an unexpected signature.");
                    Update(new(row.Provider.Id, "available", watch.ElapsedMilliseconds, read / Math.Max(watch.Elapsed.TotalSeconds, 0.001)));
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    Update(new(row.Provider.Id, "failed"));
                    token.ThrowIfCancellationRequested();
                }
            }).ConfigureAwait(false);
        lock (_gate) _measuredAt = DateTimeOffset.UtcNow;
    }

    private void Update(GithubMirrorResult result)
    {
        lock (_gate)
        {
            var index = Array.FindIndex(_results, row => row.Id == result.Id);
            if (index >= 0) _results[index] = result;
        }
    }

    // The directory page publishes the current node list in its own scripts; they are read as text and never run.
    [GeneratedRegex("src=\"([^\"]+\\.js)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Scripts();

    [GeneratedRegex("label:\"(?:contribute|search)\",value:\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Nodes();

    private async Task RefreshFromDirectoryAsync(CancellationToken cancellationToken)
    {
        if (_configuration.DirectoryUrl is not { } directory) return;
        var found = new ConcurrentBag<(GithubMirrorProvider, Uri)>();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_configuration.Timeout);
            var page = await _http.GetStringAsync(directory, deadline.Token).ConfigureAwait(false);
            var scripts = Scripts().Matches(page).Select(match => new Uri(directory, match.Groups[1].Value))
                .Where(uri => uri.Scheme == directory.Scheme && uri.Authority == directory.Authority && uri.UserInfo.Length == 0).Distinct().ToArray();
            await Parallel.ForEachAsync(scripts, new ParallelOptions { MaxDegreeOfParallelism = _configuration.Parallelism, CancellationToken = deadline.Token },
                async (script, token) =>
                {
                    string source;
                    try { source = await _http.GetStringAsync(script, token).ConfigureAwait(false); }
                    catch (HttpRequestException) { return; }
                    foreach (Match match in Nodes().Matches(source))
                        if (Uri.TryCreate("https://" + match.Groups[1].Value + "/", UriKind.Absolute, out var address) && ValidMirror(address) && address.IsDefaultPort)
                            found.Add((new GithubMirrorProvider(address.IdnHost, match.Groups[1].Value), address));
                }).ConfigureAwait(false);
        }
        // The packaged snapshot stays in use when the directory cannot be read.
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
        if (found.IsEmpty) return;
        lock (_gate)
        {
            // Directly configured mirrors and the saved selection stay; the directory replaces the packaged snapshot.
            _providers = _configuration.Mirrors.Where(row => !row.Snapshot).Select(row => (row.Provider, row.Address))
                .Concat(found.OrderBy(row => row.Item1.Id, StringComparer.Ordinal))
                .Concat(_providers.Where(row => row.Provider.Id == _saved.Selection))
                .DistinctBy(row => row.Item1.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private void Write(Saved next)
    {
        if (_statePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".next";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, _statePath, true);
        }
        _saved = next;
    }

    public void Dispose() { _http.Dispose(); _measuring.Dispose(); }
}
