using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Bounds opt-in native room browsing independently of directory transport and account sharing.</summary>
public sealed class RoomBrowserOptions : IValidatableOptions
{
    /// <summary>Gets or sets this node's configured directory identity.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Gets or sets the build whose native room list is served.</summary>
    public string ClientBuild { get; set; } = "";

    /// <summary>Gets or sets the connection's lifetime room alias budget; exhausted histories are never recycled. The default is
    /// the native ceiling: kr-1256 sub_854C90 sign-extends a signed 16-bit alias into the room click.</summary>
    [Range(1, short.MaxValue)] public int MaxAliasesPerConnection { get; set; } = short.MaxValue;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!RoomDirectoryClientOptions.BackendId(NodeId))
            yield return new("node_id must match a configured lowercase backend identifier.", [nameof(NodeId)]);
        if (!RoomDirectoryClientOptions.Identifier(ClientBuild))
            yield return new("client_build must be an explicit build identifier.", [nameof(ClientBuild)]);
    }
}
