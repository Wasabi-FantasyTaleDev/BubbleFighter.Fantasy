using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures the bounded authenticated migration client and its single destination receiver.</summary>
public sealed class MigrationBrokerOptions : IValidatableOptions
{
    /// <summary>Gets or sets the stable identity also used by this node's heartbeat row.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets the directory HTTP origin; redirects are never followed.</summary>
    public string DirectoryUrl { get; set; } = "";
    /// <summary>Gets or sets the node's private directory key, resolved from deployment secrets.</summary>
    public string AccessKey { get; set; } = "";
    /// <summary>Gets or sets the destination callback deadline, in milliseconds.</summary>
    [Range(500, 30000)] public int MigrationRequestTimeoutMs { get; set; } = 5000;
    /// <summary>Gets or sets the maximum simultaneous outgoing operations; excess callers are refused without queuing.</summary>
    [Range(1, 32)] public int MaxConcurrentRequests { get; set; } = 4;
    /// <summary>Gets or sets the maximum buffered JSON request or response size.</summary>
    [Range(1024, 1048576)] public int MaxBodyBytes { get; set; } = 65536;
    /// <summary>Gets or sets the directory inbox long-poll duration.</summary>
    [Range(1, 25)] public int PollWaitSeconds { get; set; } = 20;
    /// <summary>Gets or sets the full HTTP deadline, including response buffering.</summary>
    [Range(1000, 90000)] public int HttpTimeoutMs { get; set; } = 35000;
    /// <summary>Gets or sets the delay before retrying failed inbox requests.</summary>
    [Range(100, 30000)] public int RetryDelayMs { get; set; } = 1000;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        var common = new RoomDirectoryClientOptions { NodeId = NodeId, DirectoryUrl = DirectoryUrl, AccessKey = AccessKey, Builds = ["migration"] };
        foreach (var result in common.Validate()) yield return result;
        if (HttpTimeoutMs <= PollWaitSeconds * 1000 || HttpTimeoutMs <= MigrationRequestTimeoutMs)
            yield return new("http_timeout_ms must exceed both the inbox wait and migration callback timeout.", [nameof(HttpTimeoutMs)]);
    }
}
