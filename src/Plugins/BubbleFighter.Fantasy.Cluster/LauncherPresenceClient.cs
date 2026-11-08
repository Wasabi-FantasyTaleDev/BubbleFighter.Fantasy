using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures one authenticated population authority for a worker's public Launcher API.</summary>
public sealed class LauncherPresenceClientOptions : IValidatableOptions
{
    /// <summary>Gets or sets the requesting physical node's directory identity.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets the directory HTTP origin; redirects and embedded credentials are refused.</summary>
    public string DirectoryUrl { get; set; } = "";
    /// <summary>Gets or sets the node's private directory key from deployment configuration.</summary>
    public string AccessKey { get; set; } = "";
    /// <summary>Gets or sets the only configured group this client can request.</summary>
    public string Group { get; set; } = "";
    /// <summary>Gets or sets the response byte budget, enforced before deserializing player cards.</summary>
    [Range(1024, 4194304)] public int MaxResponseBytes { get; set; } = 1048576;
    /// <summary>Gets or sets the maximum population accepted from the authority.</summary>
    [Range(1, 1048576)] public int MaxPlayers { get; set; } = 16384;
    /// <summary>Gets or sets the simultaneous fetch budget; excess callers fail without queuing.</summary>
    [Range(1, 32)] public int MaxConcurrentRequests { get; set; } = 2;
    /// <summary>Gets or sets the total HTTP request and body-read deadline.</summary>
    [Range(1, 30)] public int RequestTimeoutSeconds { get; set; } = 2;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!RoomDirectoryClientOptions.BackendId(NodeId) || !RoomDirectoryClientOptions.Identifier(Group))
            yield return new("Presence clients require a node identity and group.", [nameof(NodeId), nameof(Group)]);
        if (!Uri.TryCreate(DirectoryUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || uri.Scheme is not ("http" or "https"))
            yield return new("directory_url must be an HTTP or HTTPS origin.", [nameof(DirectoryUrl)]);
        if (AccessKey is null || AccessKey.Length is < 32 or > 256 || AccessKey.Any(c => c is < '!' or > '~'))
            yield return new("access_key requires 32..256 non-whitespace characters.", [nameof(AccessKey)]);
    }
}

/// <summary>Connects a worker's Launcher API to a shared population authority without changing selector eligibility.</summary>
[Plugin("cluster.launcher-presence", Description = "Authenticated bounded Launcher population snapshots.",
    Provides = [typeof(ILauncherPresenceDirectory)])]
public sealed class LauncherPresenceClientPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<LauncherPresenceClientOptions>();
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = options.MaxConcurrentRequests })
        { BaseAddress = new Uri(options.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan };
        context.Effect(http);
        context.Effect(context.Services.Register<ILauncherPresenceDirectory>(new LauncherPresenceClient(options, http)));
        return ValueTask.CompletedTask;
    }
}

internal sealed class LauncherPresenceClient(LauncherPresenceClientOptions options, HttpClient http) : ILauncherPresenceDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true };
    private int _active;

    public IReadOnlyList<string> Groups => Array.AsReadOnly(new[] { options.Group });

    public async ValueTask<LauncherPresenceSnapshot> ReadAsync(string group, bool includeRoster, CancellationToken ct)
    {
        if (group != options.Group) throw new KeyNotFoundException("Presence group differs from the configured authority.");
        ct.ThrowIfCancellationRequested();
        try
        {
            if (Interlocked.Increment(ref _active) > options.MaxConcurrentRequests)
                throw new IOException("Presence request capacity is exhausted.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/nodes/" + Uri.EscapeDataString(options.NodeId)
                + "/presence/" + Uri.EscapeDataString(group) + "?roster=" + (includeRoster ? "true" : "false"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Presence authority did not return JSON.");
            await response.Content.LoadIntoBufferAsync(options.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            LauncherPresenceSnapshot snapshot;
            try { snapshot = JsonSerializer.Deserialize<LauncherPresenceSnapshot>(bytes, Json)
                ?? throw new InvalidDataException("Presence authority omitted its snapshot."); }
            catch (JsonException error) { throw new InvalidDataException("Invalid presence snapshot JSON.", error); }
            if (snapshot.OnlinePlayers < 0 || snapshot.OnlinePlayers > options.MaxPlayers
                || snapshot.PeakOnlinePlayers < snapshot.OnlinePlayers || snapshot.Players is null
                || snapshot.Players.Count > snapshot.OnlinePlayers || !includeRoster && snapshot.Players.Count != 0
                || snapshot.RosterComplete && (!includeRoster || !snapshot.Complete || snapshot.Players.Count != snapshot.OnlinePlayers)
                || snapshot.Players.Any(player => player is null || string.IsNullOrWhiteSpace(player.Name)
                    || player.Name.Length > 128 || player.Name.Any(char.IsControl) || player.Level < 0
                    || !RoomDirectoryClientOptions.Identifier(player.BuildId)))
                throw new InvalidDataException("Presence authority returned an invalid or oversized population.");
            return snapshot with { Players = Array.AsReadOnly(snapshot.Players.ToArray()) };
        }
        finally { Interlocked.Decrement(ref _active); }
    }
}
