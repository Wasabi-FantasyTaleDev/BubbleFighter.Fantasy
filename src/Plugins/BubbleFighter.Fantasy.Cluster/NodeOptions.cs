using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures authenticated node reports to an explicitly mounted directory.</summary>
public sealed class NodeOptions : IValidatableOptions
{
    /// <summary>Gets or sets the preconfigured node identity.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets the explicitly configured HTTP or HTTPS directory origin.</summary>
    public string DirectoryUrl { get; set; } = "";
    /// <summary>Gets or sets the node's private heartbeat key, resolved from deployment secrets.</summary>
    public string HeartbeatKey { get; set; } = "";
    /// <summary>Gets or sets the local builds whose game populations are reported.</summary>
    public string[] Builds { get; set; } = [];
    /// <summary>Gets or sets whether new automatic placements are refused.</summary>
    public bool Draining { get; set; }
    /// <summary>Gets or sets the interval between reports; it must be shorter than the directory lease.</summary>
    [Range(1, 60)] public int HeartbeatSeconds { get; set; } = 3;
    /// <summary>Gets or sets the deadline for each HTTP operation.</summary>
    [Range(1, 30)] public int RequestTimeoutSeconds { get; set; } = 2;
    /// <summary>Gets or sets the maximum number of room summaries sent in one report.</summary>
    [Range(1, 4096)] public int MaxRooms { get; set; } = 512;
    /// <summary>Gets or sets the maximum serialized report size, matching the directory body limit.</summary>
    [Range(1024, 4194304)] public int MaxBodyBytes { get; set; } = 262144;

    /// <summary>Gets or sets whether private account observations accompany heartbeats; enable only after the directory supports them.</summary>
    public bool ReportLauncherPresence { get; set; }
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Id(NodeId)) yield return new("node_id must be a bounded identifier.", [nameof(NodeId)]);
        if (!Uri.TryCreate(DirectoryUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || uri.Scheme is not ("http" or "https"))
            yield return new("directory_url requires an HTTP or HTTPS origin.", [nameof(DirectoryUrl)]);
        if (HeartbeatKey is null || HeartbeatKey.Length is < 32 or > 256 || HeartbeatKey.Any(c => c is < '!' or > '~'))
            yield return new("heartbeat_key requires 32..256 non-whitespace characters.", [nameof(HeartbeatKey)]);
        if (Builds is null || Builds.Length is < 1 or > 64 || Builds.Any(build => !Id(build))
            || Builds.Distinct(StringComparer.Ordinal).Count() != Builds.Length)
            yield return new("builds must contain 1..64 distinct identifiers.", [nameof(Builds)]);
    }

    private static bool Id(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
