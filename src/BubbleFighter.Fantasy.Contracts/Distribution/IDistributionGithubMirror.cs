namespace BubbleFighter.Fantasy.Contracts.Distribution;

/// <summary>
/// Routes the store's GitHub payload downloads through public URL-prefix mirrors. Only release attachments and
/// archives travel through a mirror; release metadata stays on GitHub, so every mirrored byte is still checked
/// against the SHA-256 GitHub published.
/// </summary>
public interface IDistributionGithubMirror
{
    /// <summary>Gets the saved choice and whether the one-time mainland-China suggestion waits for an administrator.</summary>
    GithubMirrorState State { get; }

    /// <summary>Gets the known mirrors in their current order, including ones a directory refresh added.</summary>
    IReadOnlyList<GithubMirrorProvider> Providers { get; }

    /// <summary>Gets the latest measurement of each mirror while a test may still be running.</summary>
    IReadOnlyList<GithubMirrorResult> Results { get; }

    /// <summary>Saves the switch, the selected mirror, or both, and settles a pending suggestion.</summary>
    /// <param name="enabled">Whether GitHub downloads use mirrors; null keeps the saved value.</param>
    /// <param name="selection"><c>auto</c> for the fastest measured mirror, or one mirror id; null keeps the saved value.</param>
    /// <exception cref="ArgumentException">The selection names no known mirror.</exception>
    GithubMirrorState Save(bool? enabled, string? selection);

    /// <summary>Records that the suggestion was shown, so it is not shown again.</summary>
    GithubMirrorState MarkSuggestionShown();

    /// <summary>Measures every mirror with a bounded sample of a known release file.</summary>
    Task MeasureAsync(CancellationToken cancellationToken);
}

/// <summary>The saved mirror choice.</summary>
/// <param name="Enabled">Whether GitHub payload downloads use mirrors.</param>
/// <param name="Selection"><c>auto</c> or one mirror id.</param>
/// <param name="SuggestionPending">The server's address is in mainland China and the suggestion has not been shown yet.</param>
/// <param name="DetectionPending">The one-time location check is still running.</param>
public sealed record GithubMirrorState(bool Enabled, string Selection, bool SuggestionPending, bool DetectionPending);

/// <summary>One URL-prefix mirror.</summary>
/// <param name="Id">The stable identity a selection names.</param>
/// <param name="Name">The display name.</param>
public sealed record GithubMirrorProvider(string Id, string Name);

/// <summary>The latest measurement of one mirror.</summary>
/// <param name="Id">The mirror identity.</param>
/// <param name="State"><c>pending</c>, <c>testing</c>, <c>available</c> or <c>failed</c>.</param>
/// <param name="Milliseconds">The sample's transfer time, including connection setup.</param>
/// <param name="BytesPerSecond">The sample's transfer rate.</param>
public sealed record GithubMirrorResult(string Id, string State, long? Milliseconds = null, double? BytesPerSecond = null);
