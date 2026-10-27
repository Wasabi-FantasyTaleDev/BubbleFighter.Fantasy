namespace BubbleFighter.Fantasy.Launcher.Http;

// Launcher releases use strict SemVer, independently of plugin version-range syntax.
internal sealed class LauncherVersion(string[] release, string[]? prerelease) : IComparable<LauncherVersion>
{
    internal static bool TryParse(string? text, out LauncherVersion? version)
    {
        version = null;
        if (text is null || text.Length < 5) return false;
        var metadata = text.Split('+');
        if (metadata.Length > 2 || metadata.Length == 2 && !Identifiers(metadata[1], false)) return false;
        var separator = metadata[0].IndexOf('-');
        var core = separator < 0 ? metadata[0] : metadata[0][..separator];
        var suffix = separator < 0 ? null : metadata[0][(separator + 1)..];
        var parts = core.Split('.');
        if (parts.Length != 3 || parts.Any(p => !Numeric(p) || p.Length > 1 && p[0] == '0') ||
            suffix is not null && !Identifiers(suffix, true)) return false;
        version = new(parts, suffix?.Split('.'));
        return true;
    }

    public int CompareTo(LauncherVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++)
        {
            var comparison = Number(release[i], other.Release[i]);
            if (comparison != 0) return comparison;
        }
        if (prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        for (var i = 0; i < Math.Min(prerelease.Length, other.Prerelease.Length); i++)
        {
            var left = prerelease[i]; var right = other.Prerelease[i];
            var leftNumeric = Numeric(left); var rightNumeric = Numeric(right);
            var comparison = leftNumeric && rightNumeric ? Number(left, right) :
                leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    private string[] Release => release;
    private string[]? Prerelease => prerelease;
    private static bool Numeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
    private static int Number(string left, string right) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
    private static bool Identifiers(string text, bool rejectLeadingZeros) => text.Split('.').All(p =>
        p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        (!rejectLeadingZeros || !Numeric(p) || p.Length == 1 || p[0] != '0'));
}
