namespace BubbleFighter.Fantasy.Contracts.Admin;

/// <summary>
/// Human-readable release names for client builds, such as <c>CN2016 0.2.70.4</c>. A client
/// profile carries only its wire id and a free-text description, so the name operators know a
/// build by (the region, year and the version the game folder is named after) is separate
/// display data that a row publishes for the administration interface.
/// </summary>
public interface IBuildReleaseLabels
{
    /// <summary>Looks up the release name of one client build.</summary>
    /// <param name="buildId">The client profile id, for example <c>cn-735</c>.</param>
    /// <param name="release">Receives the release name.</param>
    /// <returns><see langword="true"/> when a name is configured for the build; builds without one have no release name.</returns>
    bool TryGetRelease(string buildId, out string release);
}
