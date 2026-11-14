namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>A player's successful completion of one build's supplementary practice map.</summary>
public sealed record TutorialPracticeKey
{
    /// <summary>Creates a bounded identity independent of the initial tutorial's dismissed offer.</summary>
    public TutorialPracticeKey(string profile, long userNum, string mapKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapKey);
        if (userNum <= 0 || profile.Length > 32 || mapKey.Length > 128
            || profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || mapKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Practice identity requires a positive player and bounded ASCII profile and map names.");
        Profile = profile.ToLowerInvariant();
        UserNum = userNum;
        MapKey = mapKey.ToLowerInvariant();
    }

    /// <summary>Gets the client profile whose practice progress is recorded.</summary>
    public string Profile { get; }

    /// <summary>Gets the authenticated player number.</summary>
    public long UserNum { get; }

    /// <summary>Gets the client MapMeta key, compared without regard to case.</summary>
    public string MapKey { get; }
}

/// <summary>Stores successful supplementary practice separately from numbered stages and dismissed onboarding.</summary>
public interface ITutorialPracticeStore
{
    /// <summary>Returns whether this player successfully completed the named build's practice map.</summary>
    ValueTask<bool> HasClearedAsync(TutorialPracticeKey key, CancellationToken ct);

    /// <summary>Records successful completion and returns true only for the first durable clear.</summary>
    ValueTask<bool> RecordClearAsync(TutorialPracticeKey key, CancellationToken ct);
}
