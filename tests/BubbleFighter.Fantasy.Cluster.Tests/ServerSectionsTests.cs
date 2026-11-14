using BubbleFighter.Fantasy.Contracts.Cluster;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class ServerSectionsTests
{
    [Fact]
    public void SectionsAreOptionalAndAbsentFromUngroupedDirectories()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options);
        Assert.Empty(directory.Sections);
        Assert.All(directory.ListServers("kr-1256"), entry => Assert.Null(entry.Section));
        var json = System.Text.Json.JsonSerializer.Serialize(directory.ListServers("kr-1256"));
        Assert.DoesNotContain("\"section\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectorsCarryTheirSectionAndOnlySeparateSectionsNameAnApi()
    {
        var options = Grouped();
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options);
        Assert.Equal(["main", "test"], directory.Sections.Select(section => section.Id));
        Assert.Null(directory.Sections[0].ApiUrl);
        Assert.Equal("https://test.example/", directory.Sections[1].ApiUrl);
        var entries = directory.ListServers("kr-1256");
        Assert.Equal(["main", "main", "test"], entries.Select(entry => entry.Section));
        var json = System.Text.Json.JsonSerializer.Serialize(directory.Sections);
        Assert.Equal(1, json.Split("api_url").Length - 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void GroupedDirectoriesRequireEverySelectorToNameAKnownSection(string section)
    {
        var options = Grouped();
        options.Selectors[2].Section = section;
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void UngroupedDirectoriesRejectSelectorSections()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        options.Selectors[1].Section = "main";
        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData("ftp://test.example/")]
    [InlineData("https://user@test.example/")]
    [InlineData("relative")]
    public void SectionApisMustBePlainHttpOrigins(string api)
    {
        var options = Grouped();
        options.Sections[1].ApiUrl = api;
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void MaintenanceIsPublishedOnlyWhileInEffectAndKeepsAutomaticPlacementAway()
    {
        var node = ServerDirectoryTests.Node("a", "formal", []);
        node.Maintenance = new() { Start = "1970-01-01T00:01:00Z", End = "1970-01-01T00:03:00+00:00", Description = "Weekly update" };
        var options = ServerDirectoryTests.Options(node);
        Assert.Empty(options.Validate());
        var time = new ManualClock();
        var directory = new ServerDirectory(options, time);
        Report(directory, "a", time);
        Assert.Null(directory.ListServers("kr-1256")[1].Targets.Single().Maintenance);
        Assert.Single(directory.ListServers("kr-1256")[0].Targets);

        time.Advance(90);
        Report(directory, "a", time);
        var maintenance = directory.ListServers("kr-1256")[1].Targets.Single().Maintenance;
        Assert.NotNull(maintenance);
        Assert.Equal("Weekly update", maintenance.Description);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(3), maintenance.End);
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        Assert.Single(directory.ListSelectionServers("kr-1256")[0].Targets);

        time.Advance(120);
        Report(directory, "a", time);
        Assert.Null(directory.ListServers("kr-1256")[1].Targets.Single().Maintenance);
        Assert.Single(directory.ListServers("kr-1256")[0].Targets);
    }

    [Theory]
    [InlineData("2026-11-13T05:00", null)]
    [InlineData("2026-11-13 05:00:00Z", null)]
    [InlineData("2026-11-13T08:00:00Z", "2026-11-13T05:00:00Z")]
    public void MaintenanceRejectsUnzonedOrInvertedTimes(string start, string? end)
    {
        var node = ServerDirectoryTests.Node("a", "formal", []);
        node.Maintenance = new() { Start = start, End = end, Description = "" };
        Assert.NotEmpty(ServerDirectoryTests.Options(node).Validate());
    }

    private static DirectoryOptions Grouped()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "", []));
        options.Sections = [new() { Id = "main", Name = "Main" }, new() { Id = "test", Name = "Test Server", ApiUrl = "https://test.example/" }];
        options.Selectors[0].Section = "main";
        options.Selectors[1].Section = "main";
        options.Selectors[2].Section = "test";
        return options;
    }

    private static void Report(ServerDirectory directory, string node, ManualClock time)
    {
        var grant = Assert.IsType<LeaseGrant>(directory.Register(node, Guid.NewGuid().ToString("N")));
        Assert.True(directory.Observe(node, new(grant.Token, grant.Epoch, 1, false, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", [new("kr-1256", true, 1)], [])));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
