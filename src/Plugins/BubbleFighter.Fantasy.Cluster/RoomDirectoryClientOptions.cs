using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures authenticated room discovery independently of account sharing and native transfer support.</summary>
public sealed class RoomDirectoryClientOptions : IValidatableOptions
{
    /// <summary>Gets or sets the directory's configured identity for the requesting node.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Gets or sets the explicit HTTP or HTTPS directory origin; redirects are refused.</summary>
    public string DirectoryUrl { get; set; } = "";

    /// <summary>Gets or sets this node's directory key, resolved from deployment secrets.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>Gets or sets the build identifiers this node may request.</summary>
    public string[] Builds { get; set; } = [];

    /// <summary>Gets or sets the maximum number of rooms accepted in a directory response.</summary>
    [Range(1, 65536)] public int MaxRooms { get; set; } = 2048;

    /// <summary>Gets or sets the maximum response body bytes buffered before JSON parsing.</summary>
    [Range(1024, 4194304)] public int MaxResponseBytes { get; set; } = 1048576;

    /// <summary>Gets or sets the maximum combined native settings bytes accepted for one room.</summary>
    [Range(1, 1048576)] public int MaxRoomSettingsBytes { get; set; } = 65536;

    /// <summary>Gets or sets the maximum simultaneous fetches; excess callers are refused without queuing.</summary>
    [Range(1, 32)] public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>Gets or sets the deadline for a directory fetch, including response buffering.</summary>
    [Range(1, 30)] public int RequestTimeoutSeconds { get; set; } = 2;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!BackendId(NodeId)) yield return new("node_id must match a configured lowercase backend identifier.", [nameof(NodeId)]);
        if (!Uri.TryCreate(DirectoryUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/"
            || uri.Scheme is not ("http" or "https") || uri.Port < 1)
            yield return new("directory_url requires an HTTP or HTTPS origin.", [nameof(DirectoryUrl)]);
        if (AccessKey is null || AccessKey.Length is < 32 or > 256 || AccessKey.Any(c => c is < '!' or > '~'))
            yield return new("access_key requires 32..256 non-whitespace characters.", [nameof(AccessKey)]);
        if (Builds is null || Builds.Length is < 1 or > 64 || Builds.Any(build => !Identifier(build))
            || Builds.Distinct(StringComparer.Ordinal).Count() != Builds.Length)
            yield return new("builds must contain 1..64 distinct identifiers.", [nameof(Builds)]);
    }

    internal static bool Identifier(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static bool BackendId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
