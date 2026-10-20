using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;

namespace BubbleFighter.Fantasy.Tests;

/// <summary>
/// Builds a profile's wire dialect from the repository's profile data, the way its dialect row does:
/// <c>client_build</c> for the identity, the <c>WIRE_FRAME</c> and <c>WIRE_CIPHER_TABLE_*</c> vars for
/// the stream, and the eight-byte frame for a profile on the kr-1256 bundle.
/// </summary>
internal static partial class TestWireDialects
{
    private static readonly Dictionary<string, WireDialect> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the configuration of a <c>dialect.modern.messages</c> row: <paramref name="extra"/> plus the repository's
    /// packet tables, which its room codecs read their layouts from.
    /// </summary>
    /// <param name="extra">Further settings of the row, or null.</param>
    /// <returns>The row configuration.</returns>
    public static Dictionary<string, object?> ModernMessagesConfig(Dictionary<string, object?>? extra = null)
    {
        var config = extra is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(extra);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
            {
                config["packets_directory"] = candidate;
                return config;
            }
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }

    /// <summary>Gets the dialect of one profile.</summary>
    /// <param name="profileId">The profile folder name, for example <c>kr-812</c>.</param>
    /// <returns>The dialect.</returns>
    public static WireDialect Get(string profileId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(profileId, out var known))
                return known;
            var (text, frame, tableA, tableB) = Stream(profileId);
            var build = ClientBuild.Parse(Value(text, "client_build") ?? throw new InvalidOperationException($"{profileId} has no client_build."));
            var dialect = WireDialect.Create(new ClientProfile(profileId, build.Region, build.Major, 0, build.Resource, profileId),
                WireStream.Parse(frame, tableA, tableB));
            Cache[profileId] = dialect;
            return dialect;
        }
    }

    /// <summary>Gets the config a profile's dialect row carries, for test hosts that mount the row by hand.</summary>
    /// <param name="profileId">The profile folder name, for example <c>kr-812</c>.</param>
    /// <returns>A new config map with the profile id, frame and cipher tables.</returns>
    public static Dictionary<string, object?> RowConfig(string profileId)
    {
        var (_, frame, tableA, tableB) = Stream(profileId);
        return new()
        {
            ["profile_id"] = profileId, ["frame"] = frame, ["cipher_table_a"] = tableA ?? "", ["cipher_table_b"] = tableB ?? "",
        };
    }

    private static (string Text, string Frame, string? TableA, string? TableB) Stream(string profileId)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "profiles", profileId, "profile.yaml"));
        var frame = Value(text, "WIRE_FRAME") ?? (BundleList().Match(text).Value.Contains("kr-1256", StringComparison.Ordinal) ? "modern_8" : "legacy_5");
        return (text, frame, Value(text, "WIRE_CIPHER_TABLE_A"), Value(text, "WIRE_CIPHER_TABLE_B"));
    }

    private static string? Value(string text, string key)
    {
        var match = Regex.Match(text, $@"^\s*{key}:\s*""?([^""\r\n#]+?)""?\s*(?:#.*)?$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found above the test output.");
    }

    [GeneratedRegex(@"^bundles:(?:\s*\[[^\]]*\]|(?:\r?\n\s*-\s*[^\r\n]+)+)", RegexOptions.Multiline)]
    private static partial Regex BundleList();
}
