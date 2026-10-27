using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Host;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed class ClientPluginPolicyConfigurationTests
{
    [Fact]
    public void PolicyProjectionPreservesOtherLauncherSettingsAndCompleteDesiredRows()
    {
        var originalPolicy = new object[] { new Dictionary<string, object?> { ["id"] = "original.plugin" } };
        var launcher = new PluginRow { Id = "scoped.launcher", Plugin = "launcher.http", Source = "native", ParentId = "owner", Disabled = true,
            Config = new Dictionary<string, object?> { ["ticket_seconds"] = "${BFF_TEST_SECONDS:-600}", ["instances"] = originalPolicy,
                ["plugin_recommendations"] = originalPolicy } };
        var other = new PluginRow { Id = "other", Plugin = "other", Source = "native" };
        PluginRow[] rows = [launcher, other];
        ClientPluginPolicy[] policies = [new("sample.plugin", ["build-a"], Disabled: true)];
        var changed = SetupClientPluginConfiguration.Apply(rows, policies);
        Assert.Same(other, changed[1]);
        Assert.Equal(launcher with { Config = changed[0].Config }, changed[0]);
        Assert.NotSame(launcher.Config, changed[0].Config);
        Assert.Equal("${BFF_TEST_SECONDS:-600}", changed[0].Config!["ticket_seconds"]);
        Assert.Same(originalPolicy, changed[0].Config!["instances"]);
        Assert.Same(originalPolicy, launcher.Config!["plugin_recommendations"]);
        var entries = Assert.IsType<List<object?>>(changed[0].Config!["plugin_recommendations"]);
        var rule = Assert.IsType<Dictionary<string, object?>>(Assert.Single(entries));
        Assert.Equal("sample.plugin", rule["id"]);
        Assert.Equal(["build-a"], Assert.IsType<string[]>(rule["builds"]));
        Assert.Equal(false, rule["required"]);
        Assert.Equal(true, rule["disabled"]);
    }

    [Fact]
    public void NullRestoresComposedPolicyAndExplicitEmptyClearsIt()
    {
        PluginRow[] rows = [new() { Id = "launcher", Plugin = "launcher.http", Source = "native",
            Config = new Dictionary<string, object?> { ["plugin_recommendations"] = new[] { "original" } } }];
        Assert.Same(rows, SetupClientPluginConfiguration.Apply(rows, null));
        var cleared = SetupClientPluginConfiguration.Apply(rows, []);
        Assert.Empty(Assert.IsType<List<object?>>(cleared[0].Config!["plugin_recommendations"]));
        Assert.Throws<ConfigException>(() => SetupClientPluginConfiguration.Apply([], []));
    }
}
