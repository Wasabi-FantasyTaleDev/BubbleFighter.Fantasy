using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Tests;

/// <summary>Rows of the shared message adapter (<c>dialect.messages</c>) as a build's bundle mounts them.</summary>
internal static class SharedDialectRows
{
    /// <summary>The kr-1256 row: the login burst, the main-menu probe and the wear and removal requests are bound here.</summary>
    /// <param name="config">Further options laid over the bundle's.</param>
    /// <returns>The row.</returns>
    public static PluginRow Kr1256(Dictionary<string, object?>? config = null)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["protocol"] = "kr-1256/game",
            ["login"] = true,
            ["main_menu_init"] = true,
            // The real bundle mounts the bag row; a caller that has no bag tables overrides this to false.
            ["inventory"] = true,
            ["equipment"] = true,
            ["equipment_view"] = true,
            ["packets_directory"] = PacketsDirectory(),
        };
        foreach (var (key, value) in config ?? [])
            merged[key] = value;
        return new PluginRow
        {
            Id = "dialect.messages", Plugin = "dialect.messages", Source = "test",
            ClientBuild = ClientBuild.Parse("kr:1256:1304"), Lineage = ["kr-1256", "kr-812"], Config = merged,
        };
    }

    /// <summary>Finds the repository's <c>packets</c> directory from the test output folder.</summary>
    /// <returns>The directory.</returns>
    public static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }
}
