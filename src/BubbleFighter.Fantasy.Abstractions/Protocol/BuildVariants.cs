namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>How a build obtained the variant it runs.</summary>
public enum VariantMatch
{
    /// <summary>The build has a variant of its own.</summary>
    Exact,

    /// <summary>The build reuses the variant of its nearest ancestor that has one.</summary>
    Inherited,
}

/// <summary>Lineage helpers for rows.</summary>
public static class RowLineage
{
    /// <summary>
    /// Gets a row's lineage; a row built without a profile (a test fixture) falls back to the
    /// profile id its client build implies, <c>&lt;region&gt;-&lt;major&gt;</c>.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <returns>The profile id and its ancestors, nearest first; empty when the row has no client build.</returns>
    public static IReadOnlyList<string> LineageOrBuild(this PluginRow row) =>
        row.Lineage.Count > 0 ? row.Lineage
        : row.ClientBuild is { } build ? [$"{build.Region}-{build.Major}"]
        : [];
}

/// <summary>The variant chosen for one build.</summary>
/// <typeparam name="T">The variant type, typically a codec.</typeparam>
/// <param name="Value">The variant.</param>
/// <param name="Profile">The profile the variant was written for.</param>
/// <param name="Match">Whether the build has its own variant or inherits an ancestor's.</param>
public sealed record VariantChoice<T>(T Value, string Profile, VariantMatch Match);

/// <summary>
/// Variants of one message's codec (or any build-specific behaviour), each written for the
/// profile where the layout first appears. A build without its own variant runs the one of its
/// nearest ancestor in <see cref="PluginRow.Lineage"/>, so a layout that did not change is
/// written once instead of once per build.
/// </summary>
/// <typeparam name="T">The variant type.</typeparam>
public sealed class BuildVariants<T>
{
    private readonly Dictionary<string, T> _byProfile = new(StringComparer.Ordinal);

    /// <summary>Creates an empty set for one message.</summary>
    /// <param name="what">The message or behaviour the variants implement, used in errors and logs.</param>
    public BuildVariants(string what) => What = what;

    /// <summary>Gets the message or behaviour the variants implement.</summary>
    public string What { get; }

    /// <summary>Gets the profiles that have a variant of their own.</summary>
    public IReadOnlyCollection<string> Profiles => _byProfile.Keys;

    /// <summary>Adds the variant a profile introduced.</summary>
    /// <param name="profile">The profile id whose layout the variant implements.</param>
    /// <param name="value">The variant.</param>
    /// <returns>This set, for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when the profile already has a variant.</exception>
    public BuildVariants<T> Add(string profile, T value)
    {
        if (!_byProfile.TryAdd(profile, value))
            throw new ArgumentException($"{What} already has a variant for '{profile}'.", nameof(profile));
        return this;
    }

    /// <summary>Picks the variant of the nearest profile in a lineage.</summary>
    /// <param name="lineage">The build's profile id followed by its ancestors, nearest first.</param>
    /// <returns>The choice, or <see langword="null"/> when no profile in the lineage has a variant.</returns>
    public VariantChoice<T>? Resolve(IReadOnlyList<string> lineage)
    {
        for (var i = 0; i < lineage.Count; i++)
        {
            if (_byProfile.TryGetValue(lineage[i], out var value))
                return new VariantChoice<T>(value, lineage[i], i == 0 ? VariantMatch.Exact : VariantMatch.Inherited);
        }
        return null;
    }

    /// <summary>Picks the variant of the nearest profile in a lineage, failing loud when there is none.</summary>
    /// <param name="lineage">The build's profile id followed by its ancestors, nearest first.</param>
    /// <returns>The choice.</returns>
    /// <exception cref="ConfigException">Thrown when no profile in the lineage has a variant.</exception>
    public VariantChoice<T> Require(IReadOnlyList<string> lineage) =>
        Resolve(lineage) ?? throw new ConfigException(
            $"{What} has no variant for lineage [{string.Join(", ", lineage)}]; variants exist for [{string.Join(", ", _byProfile.Keys.Order(StringComparer.Ordinal))}]. "
            + "Add a variant for the build or declare the parent whose layout it shares.");
}
