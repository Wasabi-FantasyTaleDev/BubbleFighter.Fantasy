using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Binds every build profile's composed ingame row, so an option rule a profile breaks fails here and not when the
/// all-builds server starts. Binding needs no client data, so optional builds without data in the repository
/// (kr-186) are covered too.
/// </summary>
public sealed class EveryBuildInGameOptionsTests
{
    /// <summary>Lists every build profile folder: a folder under profiles/ with its own opcode table.</summary>
    public static IEnumerable<object[]> Builds() =>
        Directory.GetDirectories(RepositoryRoot("profiles"))
            .Where(directory => File.Exists(Path.Combine(directory, "opcodes.yaml")) && File.Exists(Path.Combine(directory, "profile.yaml")))
            .Select(directory => new object[] { Path.GetFileName(directory) })
            .OrderBy(row => (string)row[0], StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Builds))]
    public void ComposedInGameOptionsBindAndValidate(string build)
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);
        foreach (var row in rows.Where(row => row.Plugin == "ingame"))
            _ = new PluginConfig(row).Bind<InGameOptions>();
    }

    [Fact]
    public void ItemModeKindsOfKr186AreSupplyKinds()
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var options = new PluginConfig(Assert.Single(composer.Compose("kr-186", []), row => row.Plugin == "ingame")).Bind<InGameOptions>();
        Assert.Equal(16, options.ItemModeFallbackPointCount);
        Assert.All(options.ItemModeKinds, kind => Assert.True(
            options.RandomSupplyItemKinds.Contains(kind) || options.ImmediateSupplyItemKinds.Contains(kind)));
    }

    private static string RepositoryRoot(string leaf)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml")))
                return Path.Combine(directory.FullName, leaf);
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
