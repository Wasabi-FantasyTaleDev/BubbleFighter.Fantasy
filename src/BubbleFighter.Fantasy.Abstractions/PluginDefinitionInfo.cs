namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Describes an accepted plugin definition without exposing executable objects or code ownership anchors.</summary>
/// <param name="Id">The stable identity used by configured plugin rows.</param>
/// <param name="Description">The optional human-readable declaration description.</param>
/// <param name="Version">The accepted definition's release version.</param>
/// <param name="SupportedBuilds">The declared executable build intervals copied for this snapshot.</param>
/// <param name="Origin">The diagnostic source of the definition.</param>
public sealed record PluginDefinitionInfo(string Id, string? Description, string Version,
    IReadOnlyList<string> SupportedBuilds, string Origin)
{
    /// <summary>Gets the identity accepted by the host catalog.</summary>
    public string Id { get; init; } = Id;

    /// <summary>Gets the optional declaration description.</summary>
    public string? Description { get; init; } = Description;

    /// <summary>Gets the accepted definition's release version rather than a live instance's resolved revision.</summary>
    public string Version { get; init; } = Version;

    /// <summary>Gets copied build intervals supported by this definition.</summary>
    public IReadOnlyList<string> SupportedBuilds { get; init; } = SupportedBuilds;

    /// <summary>Gets the diagnostic definition source without retaining its backend resource.</summary>
    public string Origin { get; init; } = Origin;
}
