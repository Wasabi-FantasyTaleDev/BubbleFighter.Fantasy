using System.Globalization;

namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>Identifies a client region, executable major version and separate resource revision.</summary>
public readonly record struct ClientBuild
{
    /// <summary>Creates a validated build identity; resource revision is not an executable version.</summary>
    /// <param name="region">Lowercase ASCII region identifier.</param>
    /// <param name="major">Positive executable major version.</param>
    /// <param name="resource">Nonnegative resource revision.</param>
    public ClientBuild(string region, int major, int resource)
    {
        if (string.IsNullOrWhiteSpace(region) || region.Length > 32 || region.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("Client region must be 1..32 lowercase ASCII letters, digits or hyphens.", nameof(region));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(major);
        ArgumentOutOfRangeException.ThrowIfNegative(resource);
        Region = region;
        Major = major;
        Resource = resource;
    }

    /// <summary>Gets the region identifier used by build ranges.</summary>
    public string Region { get; }

    /// <summary>Gets the executable major version.</summary>
    public int Major { get; }

    /// <summary>Gets the independently versioned resource revision.</summary>
    public int Resource { get; }

    /// <summary>Parses the exact profile form <c>region:major:resource</c>.</summary>
    /// <param name="value">The build identity from configuration.</param>
    /// <returns>The validated identity.</returns>
    public static ClientBuild Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var resource))
            throw new FormatException($"Invalid client build '{value}'; expected region:major:resource.");
        return new ClientBuild(parts[0], major, resource);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Region}:{Major}:{Resource}";
}

/// <summary>An inclusive executable-version interval within a single client region.</summary>
public readonly record struct BuildRange
{
    /// <summary>Creates an inclusive interval after validating its region and endpoints.</summary>
    /// <param name="region">The region served by this interval.</param>
    /// <param name="minMajor">First supported executable major version.</param>
    /// <param name="maxMajor">Last supported executable major version.</param>
    public BuildRange(string region, int minMajor, int maxMajor)
    {
        _ = new ClientBuild(region, minMajor, 0);
        if (maxMajor < minMajor)
            throw new ArgumentOutOfRangeException(nameof(maxMajor), "Build range must be ascending.");
        Region = region;
        MinMajor = minMajor;
        MaxMajor = maxMajor;
    }

    /// <summary>Gets the region served by this interval.</summary>
    public string Region { get; }

    /// <summary>Gets the first supported executable major version.</summary>
    public int MinMajor { get; }

    /// <summary>Gets the last supported executable major version.</summary>
    public int MaxMajor { get; }

    /// <summary>Checks region and executable version; resource revision does not affect membership.</summary>
    /// <param name="build">The client build to check.</param>
    /// <returns>Whether the build lies within this interval.</returns>
    public bool Contains(ClientBuild build) => MinMajor > 0 && build.Major > 0 && string.Equals(Region, build.Region, StringComparison.Ordinal)
        && build.Major >= MinMajor && build.Major <= MaxMajor;

    /// <summary>Parses <c>region:major</c> or <c>region:min-max</c>, with inclusive bounds.</summary>
    /// <param name="value">The plugin's supported build interval.</param>
    /// <returns>The validated interval.</returns>
    public static BuildRange Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split(':');
        var bounds = parts.Length == 2 ? parts[1].Split('-') : [];
        if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            || !int.TryParse(bounds[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var max))
            throw new FormatException($"Invalid supported build range '{value}'; expected region:major or region:min-max.");
        return new BuildRange(parts[0], min, max);
    }
}
