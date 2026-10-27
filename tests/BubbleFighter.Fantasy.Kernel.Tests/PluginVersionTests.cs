using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PluginVersionTests
{
    [Fact]
    public void SemVerPrecedenceIncludesNumericPrereleaseAndIgnoresMetadata()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        for (var i = 1; i < ordered.Length; i++)
        {
            Assert.True(PluginVersion.Parse(ordered[i - 1]).CompareTo(PluginVersion.Parse(ordered[i])) < 0);
            Assert.True(PluginVersion.Parse(ordered[i]).CompareTo(PluginVersion.Parse(ordered[i - 1])) > 0);
        }
        Assert.Equal(0, PluginVersion.Parse("1.2.3+build.001").CompareTo(PluginVersion.Parse("1.2.3+other")));
        Assert.True(PluginVersion.Parse("1.0.0-999999999999999999999").CompareTo(PluginVersion.Parse("1.0.0-1000000000000000000000")) < 0);
    }

    [Theory]
    [InlineData("1.2.3", ">=1.2 <2", true)]
    [InlineData("2.0.0", ">=1.2 <2", false)]
    [InlineData("1.2.0", "1.2", true)]
    [InlineData("1.2.1", "=1.2", false)]
    [InlineData("1.2.0-rc.1", ">=1.2", false)]
    [InlineData("1.2.0-rc.1", ">=1.2.0-beta <1.2", true)]
    [InlineData("1.2.3", ">1 <=1.2.3", true)]
    public void ConstraintsUseExplicitAndComparisons(string version, string constraint, bool expected) =>
        Assert.Equal(expected, PluginVersion.Parse(version).Satisfies(constraint));

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3\n")]
    [InlineData("1.2.3+")]
    [InlineData("2147483648.0.0")]
    public void InvalidReleasesFail(string version) => Assert.Throws<ConfigException>(() => PluginVersion.Parse(version));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("^1.2")]
    [InlineData("~1.2")]
    [InlineData("1.*")]
    [InlineData(">=1 || <2")]
    [InlineData(">= 1")]
    [InlineData("<0 invalid")]
    public void UnsupportedConstraintsFailEvenAfterAnUnsatisfiedTerm(string constraint) =>
        Assert.Throws<ConfigException>(() => PluginVersion.Parse("1.0.0").Satisfies(constraint));

    [Fact]
    public void InputLengthsAndComparisonCountAreBounded()
    {
        Assert.Throws<ConfigException>(() => PluginVersion.Parse("1.0.0+" + new string('a', 251)));
        var version = PluginVersion.Parse("1.0.0");
        Assert.Throws<ConfigException>(() => version.Satisfies(new string(' ', 1025)));
        Assert.Throws<ConfigException>(() => version.Satisfies(string.Join(' ', Enumerable.Repeat(">=1", 17))));
    }

    [Fact]
    public void ClientRegionAndExecutableRangeAreSeparateFromResources()
    {
        var range = BuildRange.Parse("kr:1100-1256");
        Assert.True(range.Contains(ClientBuild.Parse("kr:1100:0")));
        Assert.True(range.Contains(ClientBuild.Parse("kr:1256:1304")));
        Assert.False(range.Contains(ClientBuild.Parse("kr:1257:1304")));
        Assert.False(range.Contains(ClientBuild.Parse("id:1256:1304")));
        Assert.True(BuildRange.Parse("kr:1256").Contains(new("kr", 1256, int.MaxValue)));
        Assert.False(default(BuildRange).Contains(default));
        Assert.Equal("kr:1256:1304", ClientBuild.Parse("kr:1256:1304").ToString());
    }

    [Theory]
    [InlineData("KR:1256:1304")]
    [InlineData("kr:0:1304")]
    [InlineData("kr:1256:-1")]
    [InlineData("kr:1256")]
    [InlineData("kr:1256:1304:1")]
    [InlineData("kr:2147483648:1")]
    public void MalformedBuildsFail(string value) => Assert.NotNull(Record.Exception(() => ClientBuild.Parse(value)));

    [Theory]
    [InlineData("kr:1256-1100")]
    [InlineData("kr:0")]
    [InlineData("kr:1256-")]
    [InlineData("kr:1-2-3")]
    public void MalformedBuildRangesFail(string value) => Assert.NotNull(Record.Exception(() => BuildRange.Parse(value)));

    [Fact]
    public void ContractCheckRejectsOldAndFutureRequirementsAfterCurrentWasResolved()
    {
        var current = typeof(IPlugin).Assembly;
        Assert.Same(current, PluginLoadContext.ResolveSharedContract(current.GetName()));
        var version = current.GetName().Version!;
        Assert.Same(current, PluginLoadContext.ResolveSharedContract(new AssemblyName($"BubbleFighter.Fantasy.Abstractions, Version={version.Major}.0.0.0")));
        var nextMinor = new Version(version.Major, version.Minor + 1, 0, 0).ToString();
        foreach (var value in new[] { "1.0.0.0", "2.0.0.0", "3.0.0.0", nextMinor, $"{version.Major + 1}.0.0.0" })
        {
            var reference = new AssemblyName($"BubbleFighter.Fantasy.Abstractions, Version={value}");
            var error = Assert.Throws<ConfigException>(() => PluginLoadContext.ResolveSharedContract(reference));
            Assert.Contains(value, error.Message);
            Assert.Contains("Rebuild", error.Message);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Activated = new();

    [Plugin("version.build-specific", SupportedBuilds = ["kr:1100-1256"])]
    public sealed class BuildSpecific : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Activated[context.Row.Id] = true;
            context.Effect(() => Activated.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("version.modern-family", SupportedFamilies = ["modern"])]
    public sealed class ModernFamily : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Activated[context.Row.Id] = true;
            context.Effect(() => Activated.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(null, "requires a profile with a client build")]
    [InlineData("legacy", "serves only modern")]
    public async Task FamilyMismatchFailsBeforePluginActivation(string? family, string expected)
    {
        var row = new PluginRow { Id = Guid.NewGuid().ToString("N"), Plugin = "version.modern-family", Source = "test", Family = family };
        var host = Host(row);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.Contains(expected, error.Message);
        Assert.False(Activated.ContainsKey(row.Id));
    }

    [Fact]
    public async Task ModernFamilyRowActivatesForAnyBuildOfThatFamily()
    {
        var row = new PluginRow
        {
            Id = Guid.NewGuid().ToString("N"), Plugin = "version.modern-family", Source = "test",
            ClientBuild = ClientBuild.Parse("tw:454:454"), Family = "modern",
        };
        var host = Host(row);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.True(Activated.ContainsKey(row.Id));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(null, null, "client_build")]
    [InlineData("kr:1304:1304", null, "does not support")]
    [InlineData("id:1256:1304", null, "does not support")]
    [InlineData("kr:1256:1304", ">=99", "constraint")]
    public async Task CompatibilityFailsBeforePluginActivation(string? build, string? constraint, string expected)
    {
        var row = Row(build, constraint);
        var host = Host(row);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.Contains(expected, error.Message);
        Assert.False(Activated.ContainsKey(row.Id));
        Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
    }

    [Fact]
    public async Task InstanceMetadataSurvivesReloadAndUnload()
    {
        var row = Row("kr:1256:1304", ">=1 <2");
        var host = Host(row);
        try
        {
            foreach (var action in new Func<Task>[] {
                () => host.StartAsync(CancellationToken.None).AsTask(),
                async () => { await host.ReloadAsync(row.Id, CancellationToken.None); },
                () => host.UnloadAsync(row.Id, CancellationToken.None).AsTask() })
            {
                await action();
                var info = Assert.Single(host.Instances);
                Assert.StartsWith("1.0.0", info.Version);
                Assert.Equal("8.16.0.0", info.ContractVersions["BubbleFighter.Fantasy.Abstractions"]);
                Assert.Equal(["kr:1100-1256"], info.SupportedBuilds);
            }
            Assert.False(Activated.ContainsKey(row.Id));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static PluginRow Row(string? build, string? constraint) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Plugin = "version.build-specific", Source = "test",
        ClientBuild = build is null ? null : ClientBuild.Parse(build), Version = constraint,
    };

    private static PluginHost Host(PluginRow row) => new([row],
        PluginCatalog.ScanAssemblies([typeof(PluginVersionTests).Assembly]), NullLoggerFactory.Instance);
}
