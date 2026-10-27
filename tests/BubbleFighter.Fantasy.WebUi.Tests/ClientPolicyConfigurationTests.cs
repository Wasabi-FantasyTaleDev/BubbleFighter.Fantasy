using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Host;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Launcher.Http;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed class ClientPolicyConfigurationTests
{
    private static readonly ClientPolicy Sample = new(
        [new("official", "http", "https://store.example.invalid", "catalog.toml")],
        [new("sample.plugin", "Label", null, [new("off", "disable", ["build-a"]), new("cap", "max_version", ["build-b"], "2.0.0"),
            new("offer", "recommend", ["build-b"], Sources: ["official"])])]);

    [Fact]
    public void PolicyProjectionPreservesOtherLauncherSettingsAndCompleteDesiredRows()
    {
        var original = new Dictionary<string, object?> { ["plugins"] = new List<object?>() };
        var instances = new object[] { new Dictionary<string, object?> { ["id"] = "x" } };
        var launcher = new PluginRow { Id = "scoped.launcher", Plugin = "launcher.http", Source = "native", ParentId = "owner", Disabled = true,
            Config = new Dictionary<string, object?> { ["ticket_seconds"] = "${BFF_TEST_SECONDS:-600}", ["instances"] = instances, ["client_policy"] = original } };
        var other = new PluginRow { Id = "other", Plugin = "other", Source = "native" };
        var changed = SetupClientPolicyConfiguration.Apply([launcher, other], Sample);
        Assert.Same(other, changed[1]);
        Assert.Equal(launcher with { Config = changed[0].Config }, changed[0]);
        Assert.NotSame(launcher.Config, changed[0].Config);
        Assert.Equal("${BFF_TEST_SECONDS:-600}", changed[0].Config!["ticket_seconds"]);
        Assert.Same(instances, changed[0].Config!["instances"]);
        Assert.Same(original, launcher.Config!["client_policy"]);
    }

    [Fact]
    public void TheProjectedRowBindsBackToTheSamePolicy()
    {
        PluginRow[] rows = [new() { Id = "launcher", Plugin = "launcher.http", Source = "native" }];
        var row = SetupClientPolicyConfiguration.Apply(rows, Sample)[0];
        var bound = new PluginConfig(row).Bind<LauncherOptions>().ClientPolicy.ToContract();
        Assert.Equal(Sample.Sources, bound.Sources);
        var plugin = Assert.Single(bound.Plugins);
        Assert.Equal(("sample.plugin", "Label", (string?)null), (plugin.Id, plugin.Name, plugin.Description));
        Assert.Equal(["off", "cap", "offer"], plugin.Rules.Select(rule => rule.Id));
        Assert.Equal("2.0.0", plugin.Rules[1].Version);
        Assert.Null(plugin.Rules[1].Sources);
        Assert.Equal(["official"], plugin.Rules[2].Sources!);
        Assert.Equal(InstallationRevision(Sample), InstallationRevision(bound));
    }

    [Fact]
    public void NullRestoresComposedPolicyAndAnEmptyPolicyClearsIt()
    {
        PluginRow[] rows = [new() { Id = "launcher", Plugin = "launcher.http", Source = "native",
            Config = new Dictionary<string, object?> { ["client_policy"] = new Dictionary<string, object?>() } }];
        Assert.Same(rows, SetupClientPolicyConfiguration.Apply(rows, null));
        var cleared = new PluginConfig(SetupClientPolicyConfiguration.Apply(rows, ClientPolicy.Empty)[0]).Bind<LauncherOptions>();
        Assert.Empty(cleared.ClientPolicy.Plugins);
        Assert.Throws<ConfigException>(() => SetupClientPolicyConfiguration.Apply([], ClientPolicy.Empty));
        Assert.Throws<ClientPolicyException>(() => SetupClientPolicyConfiguration.Apply(rows,
            new ClientPolicy([], [new("p", null, null, [new("a", "teleport", [])])])));
    }

    private static string InstallationRevision(ClientPolicy policy) =>
        BubbleFighter.Fantasy.Contracts.Installation.InstallationClientPolicySettings.Revision(policy);
}
