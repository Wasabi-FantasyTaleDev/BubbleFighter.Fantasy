using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// The all-builds profile runs several clan builds in one host: each build's creation cards must stay inside that
/// build's subtree, while the clan connections are one shared row.
/// </summary>
public sealed class ClanAllBuildsCompositionTests
{
    private const string Cards = "BubbleFighter.Fantasy.Contracts.Clans.IClanCreationCards";

    /// <summary>
    /// Every clan.creation-cards row has an ancestor that isolates IClanCreationCards; without it the second build's
    /// row fails to register the service and the all-builds host does not start.
    /// </summary>
    [Fact]
    public void EveryBuildsCreationCardsAreIsolatedAndTheConnectionsAreShared()
    {
        var root = RepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), $"clan-all-{Guid.NewGuid():N}");
        var profiles = Path.Combine(temporary, "profiles");
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "profiles"), "*.yaml", SearchOption.AllDirectories))
            {
                // Developer and server overrides are not part of the shipped profiles.
                if (file.EndsWith("local.patch.yaml", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith("deployment.patch.yaml", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(profiles, Path.GetRelativePath(Path.Combine(root, "profiles"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var rows = new ProfileComposer(profiles, _ => null, unsetReadsEmpty: true).Compose("all", [])
                .ToDictionary(row => row.Id);
            var cards = rows.Values.Where(row => row.Plugin == "clan.creation-cards").ToArray();
            Assert.True(cards.Length >= 2, $"expected several clan builds, found {cards.Length}");
            Assert.All(cards, row => Assert.True(IsolatedAbove(rows, row),
                $"no ancestor of '{row.Id}' isolates {Cards}"));
            Assert.Single(rows.Values, row => row.Plugin == "clan.connections");
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static bool IsolatedAbove(Dictionary<string, PluginRow> rows, PluginRow row)
    {
        for (var parent = row.ParentId; parent is not null; parent = rows[parent].ParentId)
        {
            if (rows[parent].IsolatedServices.Contains(Cards)) return true;
        }
        return false;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        }
        throw new InvalidOperationException("repository root not found");
    }
}
