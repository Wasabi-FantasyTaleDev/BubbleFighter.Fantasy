using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures an explicit private ticket authority independently of directory membership.</summary>
public sealed class TicketAuthorityOptions : IValidatableOptions
{
    /// <summary>Gets or sets the private listener protected by TLS termination or a secure tunnel.</summary>
    public string Listen { get; set; } = "127.0.0.1:8094";
    /// <summary>Gets or sets the nodes explicitly trusted to redeem this authority's tickets.</summary>
    public TicketPeerOptions[] Peers { get; set; } = [];
    /// <summary>Gets or sets the maximum simultaneous connections.</summary>
    [Range(1, 256)] public int MaxConnections { get; set; } = 32;
    /// <summary>Gets or sets the request and shutdown deadline in seconds.</summary>
    [Range(1, 30)] public int TimeoutSeconds { get; set; } = 3;
    /// <summary>Gets or sets the companion preparation budget within the complete ticket request deadline.</summary>
    [Range(100, 10000)] public int CompanionTimeoutMilliseconds { get; set; } = 1000;
    /// <summary>Gets or sets the maximum request body size in bytes.</summary>
    [Range(1024, 65536)] public int MaxBodyBytes { get; set; } = 4096;
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!IPEndPoint.TryParse(Listen, out _)) yield return new("listen must be an IP endpoint.", [nameof(Listen)]);
        if (CompanionTimeoutMilliseconds >= TimeoutSeconds * 1000)
            yield return new("companion_timeout_milliseconds must leave time for the ticket response.", [nameof(CompanionTimeoutMilliseconds)]);
        if (Peers is null || Peers.Length is < 1 or > 128 || Peers.Any(peer => peer is null))
        { yield return new("peers requires 1..128 explicit trusted nodes.", [nameof(Peers)]); yield break; }
        if (Peers.Select(peer => peer.NodeId).Distinct(StringComparer.Ordinal).Count() != Peers.Length
            || Peers.Select(peer => peer.Key).Distinct(StringComparer.Ordinal).Count() != Peers.Length)
            yield return new("Each trusted node requires a unique identity and key.", [nameof(Peers)]);
        foreach (var peer in Peers)
        {
            if (!TicketValidation.Id(peer.NodeId) || !TicketValidation.Key(peer.Key) || !TicketValidation.Builds(peer.Builds))
                yield return new("Each peer requires a bounded identity, private key and explicit build allowlist.", [nameof(Peers)]);
            if (peer.CompanionUrl is null || peer.CompanionUrl.Length > 0 && (!Uri.TryCreate(peer.CompanionUrl, UriKind.Absolute, out var companion)
                || companion.Scheme is not ("http" or "https") || companion.AbsolutePath != "/"
                || companion.UserInfo.Length != 0 || companion.Query.Length != 0 || companion.Fragment.Length != 0))
                yield return new("A peer companion_url must be an explicit HTTP or HTTPS origin.", [nameof(Peers)]);
        }
    }
}

/// <summary>A node's explicit authorization to redeem specified builds at this authority.</summary>
public sealed class TicketPeerOptions
{
    /// <summary>Gets or sets the trusted node's identity.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets the distinct private redemption key from deployment secrets.</summary>
    public string Key { get; set; } = "";
    /// <summary>Gets or sets the builds this peer may redeem.</summary>
    public string[] Builds { get; set; } = [];

    /// <summary>Gets or sets the trusted worker's companion HTTP origin; empty keeps remote companion routing disabled.</summary>
    public string CompanionUrl { get; set; } = "";
}

/// <summary>Configures a worker to redeem tickets only at its explicitly trusted authentication authority.</summary>
public sealed class TicketClientOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether the companion listener accepts launch preparation by this ticket authority's key.</summary>
    public bool Companion { get; set; }
    /// <summary>Gets or sets the explicitly configured HTTP or HTTPS authority origin.</summary>
    public string AuthorityUrl { get; set; } = "";
    /// <summary>Gets or sets this node's configured authority identity.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets this node's private redemption key from deployment secrets.</summary>
    public string Key { get; set; } = "";
    /// <summary>Gets or sets the supported build allowlist.</summary>
    public string[] Builds { get; set; } = [];
    /// <summary>Gets or sets the complete redemption attempt deadline in seconds.</summary>
    [Range(1, 30)] public int TimeoutSeconds { get; set; } = 3;
    /// <summary>Gets or sets the maximum response body size in bytes.</summary>
    [Range(1024, 65536)] public int MaxBodyBytes { get; set; } = 4096;
    /// <summary>Gets or sets the maximum simultaneous redemption attempts.</summary>
    [Range(1, 256)] public int MaxConcurrentRequests { get; set; } = 16;
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Uri.TryCreate(AuthorityUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || uri.Scheme is not ("http" or "https"))
            yield return new("authority_url requires an HTTP or HTTPS origin.", [nameof(AuthorityUrl)]);
        if (!TicketValidation.Id(NodeId) || !TicketValidation.Key(Key) || !TicketValidation.Builds(Builds))
            yield return new("A bounded node identity, private key and explicit build allowlist are required.");
    }
}

internal static class TicketValidation
{
    internal static bool Id(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static bool Key(string? value) => value is { Length: >= 32 and <= 256 } && value.All(c => c is >= '!' and <= '~');
    internal static bool Builds(string[]? value) => value is { Length: > 0 and <= 64 } && value.All(Id) && value.Distinct(StringComparer.Ordinal).Count() == value.Length;
    internal static bool Token(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
