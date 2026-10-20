using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;

namespace BubbleFighter.Fantasy.WebUi;

/// <summary>Configuration for the <see cref="WebUiPlugin"/> row.</summary>
public sealed class WebUiOptions : IValidatableOptions
{
    /// <summary>Gets or sets the optional absolute directory of operator-supplied tutorial packs; empty serves only embedded content.</summary>
    public string TutorialDirectory { get; set; } = "";

    /// <summary>Gets or sets the total memory budget for the four local tutorial packs, in bytes.</summary>
    [Range(1048576, 67108864)]
    public int TutorialMaxBytes { get; set; } = 16777216;

    /// <summary>
    /// Gets or sets the release name shown for each client build the interface lists. A build left
    /// out is shown by its id alone; a name is only worth adding where the game folder or the
    /// client itself says what the release is.
    /// </summary>
    public List<BuildReleaseOptions> Releases { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (TutorialDirectory.Length > 0 && !Path.IsPathFullyQualified(TutorialDirectory))
            yield return new ValidationResult("tutorial_directory must be an absolute directory", [nameof(TutorialDirectory)]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Releases.Count; i++)
        {
            var entry = Releases[i];
            if (string.IsNullOrWhiteSpace(entry.Build))
                yield return new ValidationResult($"releases[{i}].build must name a client build id", [nameof(Releases)]);
            else if (!seen.Add(entry.Build))
                yield return new ValidationResult($"releases[{i}].build '{entry.Build}' is listed twice", [nameof(Releases)]);
            if (string.IsNullOrWhiteSpace(entry.Release))
                yield return new ValidationResult($"releases[{i}].release must not be empty; leave the build out to show only its id", [nameof(Releases)]);
        }
    }
}

/// <summary>One client build and the release name the interface shows for it.</summary>
public sealed class BuildReleaseOptions
{
    /// <summary>Gets or sets the client profile id, for example <c>kr-1256</c>.</summary>
    [Required]
    public string Build { get; set; } = "";

    /// <summary>Gets or sets the release name, for example <c>CN2016 0.2.70.4</c>.</summary>
    [Required]
    public string Release { get; set; } = "";
}

/// <summary>Immutable <see cref="IBuildReleaseLabels"/> built from the row's configured names.</summary>
public sealed class BuildReleaseLabels : IBuildReleaseLabels
{
    private readonly Dictionary<string, string> _releases;

    /// <summary>Creates the lookup from configured entries.</summary>
    /// <param name="releases">The entries; build ids must be unique.</param>
    public BuildReleaseLabels(IEnumerable<BuildReleaseOptions> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        _releases = releases.ToDictionary(entry => entry.Build, entry => entry.Release, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool TryGetRelease(string buildId, out string release)
    {
        if (buildId is not null && _releases.TryGetValue(buildId, out var found))
        {
            release = found;
            return true;
        }
        release = "";
        return false;
    }
}
