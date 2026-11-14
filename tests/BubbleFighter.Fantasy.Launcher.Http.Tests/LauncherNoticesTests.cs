using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherNoticesTests
{
    [Fact]
    public async Task NoticesArePublishedNewestFirstForTheirSectionsOnly()
    {
        var options = Options();
        options.Notices =
        [
            Notice("older", "2026-01-01T00:00:00Z"),
            Notice("newer", "2026-02-01T00:00:00+08:00"),
            Notice("test-only", "2026-01-15T00:00:00Z", "test"),
            Notice("future", "2999-01-01T00:00:00Z"),
        ];
        Assert.Empty(options.Validate());
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(4), new LauncherSessions(options, new MemoryLauncherSessions(4)));
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };

        var main = await http.GetFromJsonAsync<JsonElement>("/v1/notices?section=main");
        Assert.Equal(["newer", "older"], Ids(main));
        var test = await http.GetFromJsonAsync<JsonElement>("/v1/notices?section=test");
        Assert.Equal(["newer", "test-only", "older"], Ids(test));
        var ungrouped = await http.GetFromJsonAsync<JsonElement>("/v1/notices");
        Assert.Equal(["newer", "older"], Ids(ungrouped));
        var first = main.GetProperty("notices")[0];
        Assert.Equal("**Bold** body", first.GetProperty("body").GetString());
        Assert.Equal("info", first.GetProperty("level").GetString());
        Assert.Equal(new DateTimeOffset(2026, 1, 31, 16, 0, 0, TimeSpan.Zero), first.GetProperty("publishedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("", "info", "2026-01-01T00:00:00Z")]
    [InlineData("id", "urgent", "2026-01-01T00:00:00Z")]
    [InlineData("id", "info", "2026-01-01T00:00")]
    [InlineData("id", "info", null)]
    public void InvalidNoticesAreRejected(string id, string level, string? published)
    {
        var options = Options();
        options.Notices = [new() { Id = id, Title = "Title", Body = "Body", Level = level, PublishedAt = published }];
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void DuplicateNoticeIdentitiesAreRejected()
    {
        var options = Options();
        options.Notices = [Notice("same", "2026-01-01T00:00:00Z"), Notice("same", "2026-01-02T00:00:00Z")];
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public async Task ServerListPublishesTheDirectorySections()
    {
        var options = Options();
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(4),
            new LauncherSessions(options, new MemoryLauncherSessions(4)), players: null, directory: new Directory());
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        var result = await http.GetFromJsonAsync<JsonElement>("/v1/servers/kr-1256");
        var sections = result.GetProperty("sections");
        Assert.Equal("main", sections[0].GetProperty("id").GetString());
        Assert.False(sections[0].TryGetProperty("api_url", out _));
        Assert.Equal("https://oversea.example/", sections[1].GetProperty("api_url").GetString());
        var entry = result.GetProperty("servers")[0];
        Assert.Equal("oversea", entry.GetProperty("section").GetString());
        var maintenance = entry.GetProperty("targets")[0].GetProperty("maintenance");
        Assert.Equal("Weekly update", maintenance.GetProperty("description").GetString());
    }

    private static string[] Ids(JsonElement result) => result.GetProperty("notices").EnumerateArray().Select(notice => notice.GetProperty("id").GetString()!).ToArray();

    private static LauncherNoticeOptions Notice(string id, string published, params string[] sections) =>
        new() { Id = id, Title = id, Body = "**Bold** body", Level = "info", PublishedAt = published, Sections = sections };

    private static LauncherOptions Options() => new()
    {
        Listen = "127.0.0.1:0", PasswordIterations = 100000,
        Instances = [new() { Id = "kr-1256", Build = "kr-1256", Port = 7100, Name = [new() { Language = "en", Text = "Client build" }] }],
    };

    private sealed class Directory : IServerDirectory
    {
        public IReadOnlyList<ServerSection> Sections => [new("main", "Main", null), new("oversea", "Oversea Server", "https://oversea.example/")];
        public IReadOnlyList<ServerSelectorEntry> ListServers(string clientBuild) =>
        [
            new(4, "Oversea", false, [new("o", "oversea.example", 7100, "", "", true, false, 0, 8)
            { Maintenance = new(DateTimeOffset.UnixEpoch, null, "Weekly update") }]) { Section = "oversea" },
        ];
        public IReadOnlyList<FederatedRoom> ListRooms(string requestingNodeId, string clientBuild) => throw new NotSupportedException();
    }
}
