namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>The repository's packet tables, which the legacy dialect rows read.</summary>
internal static class RepositoryPackets
{
    /// <summary>Finds the <c>packets</c> directory above the test output folder.</summary>
    public static string Directory()
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
