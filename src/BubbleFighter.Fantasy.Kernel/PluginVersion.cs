using System.Globalization;
using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// A build-version parser and SemVer-precedence comparison for plugin manifests. Versions parse as
/// <c>major[.minor[.patch]][-prerelease][+build]</c> with optional components only when the caller
/// allows a partial form (constraint tails such as <c>&gt;=1.2</c>); numeric components and
/// prerelease identifiers reject leading zeros, and build metadata is ignored by comparison.
/// </summary>
internal sealed partial record PluginVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<PluginVersion>
{
    /// <summary>
    /// Parses one version. With <paramref name="partial"/> set, missing minor and patch components
    /// parse as zero; without it, all three components are required.
    /// </summary>
    /// <exception cref="ConfigException">Thrown for a value over 256 characters, a malformed version,
    /// a missing required component, an overflowing numeric component, or a leading zero in a numeric
    /// prerelease identifier.</exception>
    public static PluginVersion Parse(string value, bool partial = false)
    {
        if (value.Length > 256)
            throw new ConfigException("Plugin version is limited to 256 characters.");
        var match = VersionPattern().Match(value);
        if (!match.Success || (!partial && (!match.Groups[2].Success || !match.Groups[3].Success)))
            throw new ConfigException($"Invalid plugin version '{value}'; expected SemVer major.minor.patch[-prerelease][+build].");
        int Number(int group) => match.Groups[group].Success && int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : match.Groups[group].Success ? throw new ConfigException($"Plugin version component is too large: '{value}'.") : 0;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(id => Numeric(id) && id.Length > 1 && id[0] == '0') == true)
            throw new ConfigException($"Numeric prerelease identifiers must not contain leading zeros: '{value}'.");
        return new PluginVersion(Number(1), Number(2), Number(3), pre);
    }

    /// <summary>Compares by SemVer precedence: numeric fields first, then a release over any prerelease, then prerelease identifiers left to right — numeric identifiers compare numerically regardless of length, alphanumeric ones ordinally, and a shorter identifier list sorts first.</summary>
    public int CompareTo(PluginVersion? other)
    {
        if (other is null) return 1;
        foreach (var value in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) })
            if (value != 0) return value;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var ln = Numeric(left[i]);
            var rn = Numeric(right[i]);
            var result = ln != rn ? (ln ? -1 : 1) : ln && left[i].Length != right[i].Length
                ? left[i].Length.CompareTo(right[i].Length) : string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>
    /// Evaluates a version constraint of 1..16 space-separated terms. Each term is one of
    /// <c>&gt;=</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&lt;</c> or <c>=</c> followed by a version; a bare
    /// version means exact equality. Term versions parse partially, so <c>&gt;=1.2</c> compares
    /// against 1.2.0 and accepts the whole 1.2 series only through an explicit upper bound.
    /// </summary>
    /// <exception cref="ConfigException">Thrown for an empty or oversized constraint or more than 16 terms.</exception>
    public bool Satisfies(string constraint)
    {
        if (constraint.Length is 0 or > 1024)
            throw new ConfigException("Plugin version constraint must contain 1..1024 characters.");
        var terms = constraint.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length is 0 or > 16)
            throw new ConfigException("Plugin version constraint must contain 1..16 comparison terms.");
        var accepted = true;
        foreach (var term in terms)
        {
            var op = term.StartsWith(">=", StringComparison.Ordinal) || term.StartsWith("<=", StringComparison.Ordinal) ? term[..2]
                : term[0] is '>' or '<' or '=' ? term[..1] : "";
            var comparison = CompareTo(Parse(term[op.Length..], partial: true));
            accepted &= op switch { ">=" => comparison >= 0, "<=" => comparison <= 0, ">" => comparison > 0, "<" => comparison < 0, _ => comparison == 0 };
        }
        return accepted;
    }

    private static bool Numeric(string value) => value.All(c => c is >= '0' and <= '9');

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?(?:\.(0|[1-9][0-9]*))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
