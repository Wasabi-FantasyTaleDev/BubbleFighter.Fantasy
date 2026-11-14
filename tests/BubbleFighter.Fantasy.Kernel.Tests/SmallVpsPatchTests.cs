using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Checks that the small-server patch applies to single-build and all-build profiles and keeps administration.</summary>
public sealed class SmallVpsPatchTests
{
    private static readonly string[] OffRows =
        ["webui", "distribution.sources", "distribution.plans", "distribution.packages", "distribution.jobs"];

    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("all")]
    public void TurnsOffTheWebPageAndDistributionButKeepsTheAdministrationApi(string profile)
    {
        var root = RepositoryRoot();
        var composer = new ProfileComposer(Path.Combine(root, "profiles"), _ => null, unsetReadsEmpty: true);
        var defaults = composer.Compose(profile, []).ToDictionary(row => row.Id);
        var rows = composer.Compose(profile, [Path.Combine(root, "profiles", "bundles", "small-vps.patch.yaml")])
            .ToDictionary(row => row.Id);

        foreach (var id in OffRows)
        {
            Assert.False(defaults[id].Disabled, id);
            Assert.True(rows[id].Disabled, id);
        }
        Assert.False(rows["admin.http"].Disabled);
        Assert.Equal(defaults.Keys.Order(StringComparer.Ordinal), rows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(defaults.Values.Count(row => !row.Disabled) - OffRows.Length, rows.Values.Count(row => !row.Disabled));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
