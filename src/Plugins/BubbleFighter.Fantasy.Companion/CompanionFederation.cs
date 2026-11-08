using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Companion;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

internal sealed class CompanionFederation(CompanionLaunches launches, CompanionOptions options,
    TimeProvider clock, ILogger logger) : ICompanionTicketIssuer, ICompanionFederation, IDisposable
{
    internal const string Prefix = "/companion/internal/v1/";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Remote> _remote = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Authority> _authorities = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requests = new(options.MaxRemoteRequests);
    private readonly HttpClient _http = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, MaxConnectionsPerServer = options.MaxRemoteRequests }) { Timeout = Timeout.InfiniteTimeSpan };
    private bool _closed;

    internal sealed record Identity(string Account, string Build, string LaunchId);
    private sealed record Authority(string[] Builds);
    private sealed class Remote(Identity identity, string node, Uri origin, string key, long verified)
    {
        internal Identity Identity { get; } = identity;
        internal string Node { get; } = node;
        internal Uri Origin { get; } = origin;
        internal string Key { get; } = key;
        internal long Verified { get; set; } = verified;
    }

    public CompanionGrant Open(string account, string build) => launches.Open(account, build);
    public CompanionGrant? Renew(string account, string launchId) => launches.Renew(account, launchId);

    public async ValueTask<CompanionGrant?> RenewAsync(string account, string launchId, CancellationToken ct)
    {
        Remote? remote;
        lock (_gate)
        {
            if (_closed) return null;
            _remote.TryGetValue(launchId, out remote);
            if (remote is not null && remote.Identity.Account != account) return null;
        }
        if (remote is null) return launches.Renew(account, launchId);
        using var response = await SendAsync(remote, "tickets", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { Forget(remote); return null; }
        response.EnsureSuccessStatusCode();
        var grant = await response.Content.ReadFromJsonAsync<CompanionGrant>(Json, ct).ConfigureAwait(false);
        if (grant is null || grant.LaunchId != launchId || string.IsNullOrWhiteSpace(grant.Ticket) || grant.Ticket.Length > 256
            || grant.ExpiresAt <= clock.GetUtcNow())
            throw new InvalidDataException("The companion node returned an invalid grant.");
        var url = grant.Url.Replace("{host}", remote.Origin.Host, StringComparison.Ordinal);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("ws" or "wss")
            || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0
            || target.AbsolutePath != CompanionServer.Path)
            throw new InvalidDataException("The companion node returned an invalid endpoint.");
        lock (_gate)
        {
            if (!Current(remote)) return null;
            remote.Verified = clock.GetTimestamp();
        }
        return grant with { Url = url };
    }

    public async ValueTask<bool> RedirectAsync(string account, string build, string launchId, string nodeId,
        Uri companionOrigin, string nodeKey, CancellationToken cancellationToken)
    {
        var identity = new Identity(account, build, launchId);
        if (!ValidIdentity(identity) || !ValidOrigin(companionOrigin) || !ValidKey(nodeKey)
            || !launches.OwnsPending(account, build, launchId)) return false;
        var remote = new Remote(identity, nodeId, companionOrigin, nodeKey, clock.GetTimestamp());
        using var response = await SendAsync(remote, "launches", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        lock (_gate)
        {
            if (_closed || !launches.Redirect(account, build, launchId)) return false;
            _remote.Add(launchId, remote);
        }
        return true;
    }

    public IDisposable TrustAuthority(string key, IReadOnlyList<string> builds)
    {
        if (!ValidKey(key) || builds.Count == 0 || builds.Any(build => !ValidName(build)))
            throw new ArgumentException("A companion authority requires a private key and allowed builds.");
        var hash = Hash(key);
        var authority = new Authority(builds.ToArray());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_authorities.Count >= options.MaxRemoteAuthorities || !_authorities.TryAdd(hash, authority))
                throw new InvalidOperationException("The companion authority capacity is occupied or the key is already registered.");
        }
        return new Registration(() =>
        {
            lock (_gate)
                if (_authorities.TryGetValue(hash, out var current) && ReferenceEquals(current, authority)) _authorities.Remove(hash);
        });
    }

    internal string? Authorize(string header, string build)
    {
        if (header.Length > 263 || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var hash = Hash(header[7..]);
        lock (_gate) return Trusted(hash, build) ? hash : null;
    }

    private bool Trusted(string hash, string build) => !_closed && _authorities.TryGetValue(hash, out var authority)
        && authority.Builds.Contains(build, StringComparer.Ordinal);

    internal bool Import(Identity identity, string authority)
    {
        lock (_gate) return ValidIdentity(identity) && Trusted(authority, identity.Build)
            && launches.Import(identity.Account, identity.Build, identity.LaunchId, authority);
    }

    internal bool Exists(Identity identity, string authority)
    {
        lock (_gate) return ValidIdentity(identity) && Trusted(authority, identity.Build)
            && launches.IsImported(identity.Account, identity.Build, identity.LaunchId, authority);
    }

    internal CompanionGrant? RenewImported(Identity identity, string authority)
    {
        lock (_gate) return Exists(identity, authority) ? launches.Renew(identity.Account, identity.LaunchId) : null;
    }

    internal async ValueTask RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.RemoteProbeSeconds), clock);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            Remote[] snapshot;
            lock (_gate) snapshot = _remote.Values.ToArray();
            foreach (var batch in snapshot.Chunk(options.MaxRemoteRequests))
                await Task.WhenAll(batch.Select(remote => ProbeAsync(remote, ct))).ConfigureAwait(false);
        }
    }

    private async Task ProbeAsync(Remote remote, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(remote, "status", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) { Forget(remote); return; }
            response.EnsureSuccessStatusCode();
            lock (_gate) if (Current(remote)) remote.Verified = clock.GetTimestamp();
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Companion node {Node} could not confirm a launch ({FailureType}).", remote.Node, error.GetType().Name);
            lock (_gate)
                if (Current(remote) && clock.GetElapsedTime(remote.Verified).TotalSeconds >= options.RemoteFailureSeconds)
                    Forget(remote);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Remote remote, string operation, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RemoteTimeoutSeconds));
        if (!await _requests.WaitAsync(0, deadline.Token).ConfigureAwait(false))
            throw new HttpRequestException("Companion forwarding capacity is occupied.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(remote.Origin, Prefix + operation))
            { Content = JsonContent.Create(remote.Identity, options: Json) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remote.Key);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            try { await response.Content.LoadIntoBufferAsync(options.MessageBytes, deadline.Token).ConfigureAwait(false); }
            catch { response.Dispose(); throw; }
            return response;
        }
        finally { _requests.Release(); }
    }

    private bool Current(Remote remote) => !_closed && _remote.TryGetValue(remote.Identity.LaunchId, out var found) && ReferenceEquals(found, remote);
    private void Forget(Remote remote)
    {
        lock (_gate)
        {
            if (!Current(remote)) return;
            _remote.Remove(remote.Identity.LaunchId);
            launches.ForgetRemote(remote.Identity.LaunchId);
        }
    }

    internal static bool ValidIdentity(Identity? identity) => identity is not null
        && identity.Account is { Length: > 0 and <= 256 } && !identity.Account.Any(char.IsControl)
        && ValidName(identity.Build) && identity.LaunchId is { Length: 32 }
        && identity.LaunchId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool ValidOrigin(Uri origin) => origin.IsAbsoluteUri && origin.Scheme is "http" or "https"
        && origin.AbsolutePath == "/" && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    private static bool ValidName(string value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool ValidKey(string value) => value is { Length: >= 32 and <= 256 } && value.All(c => c is >= '!' and <= '~');
    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    public void Dispose()
    {
        lock (_gate) { _closed = true; _remote.Clear(); _authorities.Clear(); }
        _http.Dispose();
        _requests.Dispose();
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
