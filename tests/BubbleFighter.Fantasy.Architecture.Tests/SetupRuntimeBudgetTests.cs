using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

/// <summary>
/// The first-run server starts with only the setup rows, and the kernel fixes its entry and service-metadata budget
/// from them at startup. Every installation is admitted later inside that budget, so the setup rows must reserve
/// enough for the largest installation: every selectable build, held twice while a reconfiguration replaces it.
/// </summary>
public sealed class SetupRuntimeBudgetTests
{
    [Fact]
    public void SetupReservesCoverAReconfigurationOfEverySelectableBuild()
    {
        var profiles = Path.Combine(RepoRoot(), "profiles");
        // Values only shape rows; the budget depends on row counts and lifecycle options, not on configuration text.
        var composer = new ProfileComposer(profiles, _ => null, unsetReadsEmpty: true);
        var shell = composer.Compose("setup", []);
        var builds = composer.ListProfiles().Where(p => p.IsClientBuild && p.SetupEnabled && p.ActivationEnabled)
            .Select(p => p.Id).ToArray();
        Assert.NotEmpty(builds);
        var shellIds = shell.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var installed = composer.ComposeBuilds("management", builds, []).Where(row => !shellIds.Contains(row.Id)).ToArray();

        // Mirrors PluginRuntimeBudget: capacity is 3 plus each startup row's 1 + effect capacity + reserve,
        // and a transition charges 1 + effect capacity for every candidate and retiring row.
        long capacity = 3 + shell.Sum(row => 1L + row.Lifecycle.EffectCapacity + row.Lifecycle.ServiceMetadataReserve);
        long required = 3 + shell.Sum(row => 1L + row.Lifecycle.EffectCapacity) + 2 * installed.Sum(row => 1L + row.Lifecycle.EffectCapacity);
        Assert.True(required <= capacity, $"Setup service metadata capacity {capacity} is below the {required} a full reconfiguration needs.");

        long entries = shell.Count + shell.Sum(row => (long)row.Lifecycle.EntryReserve);
        long neededEntries = shell.Count + 2L * installed.Length;
        Assert.True(neededEntries <= entries, $"Setup entry capacity {entries} is below the {neededEntries} a full reconfiguration needs.");
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
