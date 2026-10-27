using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

/// <summary>
/// Logic plugins should handle typed messages only; opcode numbers, byte layouts and build
/// checks belong to the dialect and codec layer. Existing violations are counted per plugin in
/// <c>wire-ratchet.txt</c>: the counts may only go down. When a migration lowers one, rerun with
/// <c>BFF_RATCHET_WRITE=1</c> to record the new baseline in the same commit.
///
/// </summary>
public sealed class WireRatchetTests
{
    private static readonly string[] LogicPlugins =
    [
        "Lobby", "Shop", "Equipment", "InventoryView", "Fighters", "Clan", "InGame", "MatchConnection", "PlayerSettings",
    ];

    private static readonly (string Metric, Regex Pattern)[] Metrics =
    [
        // Raw packet handling (handlers keyed by opcode) instead of typed message bindings.
        ("raw_router", new Regex(@"\bIPacketRouter\b|\brouter\.Register\(", RegexOptions.Compiled)),
        // A build's opcode number referenced from logic.
        ("opcode_constant", new Regex(@"\b\w*Opcodes\.\w+", RegexOptions.Compiled)),
        // Byte layouts written or read in logic.
        ("wire_codec", new Regex(@"\bPacket(?:Writer|Reader)\b", RegexOptions.Compiled)),
        // Behaviour chosen by build number or profile id instead of lineage or capability. A BuildVariants key
        // (.Add("kr-1256",...)) is the sanctioned selection and is not counted.
        ("build_check", new Regex(@"\bVersionMajor\b|(?<!\.Add\()""(?:kr|cn|tw|id)-\d{3,4}(?:/\w+)?""", RegexOptions.Compiled)),
    ];

    [Fact]
    public void LogicPluginsNeverGainWireCoupling()
    {
        var root = RepoRoot();
        var baselinePath = Path.Combine(root, "tests", "BubbleFighter.Fantasy.Architecture.Tests", "wire-ratchet.txt");
        var current = Count(root);
        if (Environment.GetEnvironmentVariable("BFF_RATCHET_WRITE") == "1")
        {
            File.WriteAllText(baselinePath, Format(current));
            return;
        }
        Assert.True(File.Exists(baselinePath), $"Missing {baselinePath}; run the tests once with BFF_RATCHET_WRITE=1.");
        var baseline = Parse(File.ReadAllLines(baselinePath));
        var errors = new List<string>();
        foreach (var (key, count) in current)
        {
            var allowed = baseline.GetValueOrDefault(key);
            if (count > allowed)
                errors.Add($"{key} rose from {allowed} to {count}: route the new code through a typed message and a codec.");
            else if (count < allowed)
                errors.Add($"{key} fell from {allowed} to {count}: record the progress with BFF_RATCHET_WRITE=1.");
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static SortedDictionary<string, int> Count(string root)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var plugin in LogicPlugins)
        {
            var dir = Path.Combine(root, "src", "Plugins", "BubbleFighter.Fantasy." + plugin);
            Assert.True(Directory.Exists(dir), $"Logic plugin directory {dir} is missing; update {nameof(LogicPlugins)}.");
            var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
            var text = string.Join("\n", files.Select(File.ReadAllText));
            foreach (var (metric, pattern) in Metrics)
                counts[$"{plugin}.{metric}"] = pattern.Matches(text).Count;
        }
        return counts;
    }

    private static string Format(SortedDictionary<string, int> counts)
    {
        var builder = new StringBuilder("# Wire coupling left in logic plugins; counts may only fall. See WireRatchetTests.\n");
        foreach (var (key, count) in counts)
            builder.Append(key).Append(' ').Append(count).Append('\n');
        return builder.ToString();
    }

    private static Dictionary<string, int> Parse(IEnumerable<string> lines) =>
        lines.Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));

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
