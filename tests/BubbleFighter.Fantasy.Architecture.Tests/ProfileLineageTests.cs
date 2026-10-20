using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

/// <summary>
/// A profile's <c>parent:</c> selects which C# variants a build inherits, while <c>packets/builds.yaml</c>
/// <c>base:</c> selects which packet-table rows it inherits. The two chains must agree, or a build would
/// take its layouts from one ancestor and its behaviour from another.
/// </summary>
public sealed class ProfileLineageTests
{
    [Fact]
    public void EveryTabledBuildsProfileParentIsItsPacketTableBase()
    {
        var root = RepoRoot();
        var bases = TableBases(Path.Combine(root, "packets", "builds.yaml"));
        Assert.NotEmpty(bases);
        var mismatches = new List<string>();
        foreach (var (build, tableBase) in bases)
        {
            var profile = Path.Combine(root, "profiles", build, "profile.yaml");
            if (!File.Exists(profile))
                continue;
            var parent = TopLevelValue(profile, "parent");
            if (!string.Equals(parent, tableBase, StringComparison.Ordinal))
                mismatches.Add($"{build}: profile parent '{parent ?? "<none>"}', packet-table base '{tableBase ?? "<none>"}'");
        }
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// The family a profile inherits through <c>parent:</c> (the kernel's view, which picks family plugins) must be
    /// the family its packet-table base chain inherits (the tables' view, which picks the room writer).
    /// </summary>
    [Fact]
    public void EveryTabledBuildsProfileFamilyIsItsPacketTableFamily()
    {
        var root = RepoRoot();
        var tables = Path.Combine(root, "packets", "builds.yaml");
        var bases = TableBases(tables);
        var families = TableValues(tables, "family");
        Assert.Contains("modern", families.Values);
        var mismatches = new List<string>();
        foreach (var build in bases.Keys)
        {
            if (!File.Exists(Path.Combine(root, "profiles", build, "profile.yaml")))
                continue;
            var tableFamily = Inherited(build, id => families.GetValueOrDefault(id), id => bases.GetValueOrDefault(id));
            var profileFamily = Inherited(build,
                id => TopLevelValue(Path.Combine(root, "profiles", id, "profile.yaml"), "family"),
                id => TopLevelValue(Path.Combine(root, "profiles", id, "profile.yaml"), "parent"));
            if (!string.Equals(profileFamily, tableFamily, StringComparison.Ordinal))
                mismatches.Add($"{build}: profile family '{profileFamily}', packet-table family '{tableFamily}'");
        }
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    // The nearest declared family along a parent chain; a chain without one is legacy.
    private static string Inherited(string build, Func<string, string?> family, Func<string, string?> parent)
    {
        for (string? id = build; id is not null; id = parent(id))
            if (family(id) is { } declared)
                return declared;
        return "legacy";
    }

    // builds.yaml: a four-space scalar key under each two-space build key.
    private static Dictionary<string, string> TableValues(string path, string key)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = StripComment(raw);
            if (line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && line[2] != ' ' && line.TrimEnd().EndsWith(':'))
                current = line.Trim().TrimEnd(':');
            else if (current is not null && line.StartsWith($"    {key}:", StringComparison.Ordinal))
                values[current] = line[$"    {key}:".Length..].Trim();
        }
        return values;
    }

    // builds.yaml: "builds:" then two-space build keys, each with an optional four-space "base:".
    private static Dictionary<string, string?> TableBases(string path)
    {
        var bases = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? current = null;
        var inBuilds = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0)
                continue;
            if (!line.StartsWith(' '))
            {
                inBuilds = line.TrimEnd() == "builds:";
                current = null;
                continue;
            }
            if (!inBuilds)
                continue;
            if (line.StartsWith("  ", StringComparison.Ordinal) && line[2] != ' ' && line.TrimEnd().EndsWith(':'))
            {
                current = line.Trim().TrimEnd(':');
                bases[current] = null;
            }
            else if (current is not null && line.StartsWith("    base:", StringComparison.Ordinal))
                bases[current] = line["    base:".Length..].Trim();
        }
        return bases;
    }

    private static string? TopLevelValue(string path, string key) =>
        File.ReadLines(path).Select(StripComment)
            .Where(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(l => l[(key.Length + 1)..].Trim())
            .FirstOrDefault(v => v.Length > 0);

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? line : line[..hash];
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("The test does not run under the repository.");
    }
}
