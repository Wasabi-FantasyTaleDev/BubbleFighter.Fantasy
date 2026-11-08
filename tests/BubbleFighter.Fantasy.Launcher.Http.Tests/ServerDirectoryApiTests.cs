using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class ServerDirectoryApiTests
{
    [Theory]
    [InlineData(false, "native")]
    [InlineData(true, "native")]
    [InlineData(false, "gateway")]
    [InlineData(true, "gateway")]
    public async Task OptionalDirectoryUsesIndependentSelectorNamesAndExactScopeFields(bool enabled, string mode)
    {
        var options = Options();
        options.Center = new() { Enabled = enabled, Mode = mode };
        var directory = new Directory();
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(4),
            new LauncherSessions(options, new MemoryLauncherSessions(4)), players: null, directory: directory);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        var result = await http.GetFromJsonAsync<JsonElement>("/v1/servers/kr-1256");
        Assert.Equal(1, result.GetProperty("schema").GetInt32());
        Assert.Equal("kr-1256", result.GetProperty("build").GetString());
        Assert.Equal(enabled, result.GetProperty("center").GetProperty("enabled").GetBoolean());
        Assert.Equal(mode, result.GetProperty("center").GetProperty("mode").GetString());
        Assert.Equal(TimeSpan.Zero, result.GetProperty("generated_at_utc").GetDateTimeOffset().Offset);
        var entries = result.GetProperty("servers");
        Assert.Equal(3, entries.GetArrayLength());
        Assert.Equal(3, entries[2].GetProperty("id").GetInt32());
        Assert.Equal(0, entries[2].GetProperty("targets").GetArrayLength());
        Assert.Equal("Automatic", entries[0].GetProperty("name").GetString());
        Assert.True(entries[0].GetProperty("automatic").GetBoolean());
        Assert.NotEqual(entries[0].GetProperty("id").GetInt32(), entries[1].GetProperty("id").GetInt32());
        var target = entries[0].GetProperty("targets")[0];
        Assert.Equal("node.example", target.GetProperty("host").GetString());
        Assert.Equal("formal", target.GetProperty("dataScope").GetString());
        Assert.Equal("", target.GetProperty("joinScope").GetString());
        Assert.False(target.TryGetProperty("dataDomain", out _));
        Assert.Equal(DateTimeOffset.UnixEpoch, target.GetProperty("observed_at_utc").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(15), target.GetProperty("expires_at_utc").GetDateTimeOffset());
        Assert.Equal("observation-1", target.GetProperty("status_revision").GetString());
        var repeated = await http.GetFromJsonAsync<JsonElement>("/v1/servers/kr-1256");
        Assert.Equal(target.GetProperty("expires_at_utc").GetDateTimeOffset(),
            repeated.GetProperty("servers")[0].GetProperty("targets")[0].GetProperty("expires_at_utc").GetDateTimeOffset());
        var catalog = await http.GetFromJsonAsync<JsonElement>("/v1/catalog");
        Assert.Equal(1, catalog.GetProperty("instances").GetArrayLength());
        Assert.Equal(enabled, catalog.GetProperty("center").GetProperty("enabled").GetBoolean());
        Assert.Equal(mode, catalog.GetProperty("center").GetProperty("mode").GetString());
        using var absent = await http.GetAsync("/v1/servers/kr-999");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    [Fact]
    public async Task StandaloneApiHasNoDirectoryDependencyOrRoute()
    {
        var options = Options();
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(4), new LauncherSessions(options, new MemoryLauncherSessions(4)));
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        using var absent = await http.GetAsync("/v1/servers/kr-1256");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var catalog = await http.GetAsync("/v1/catalog");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var body = await catalog.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("center").GetProperty("enabled").GetBoolean());
        Assert.Equal("gateway", body.GetProperty("center").GetProperty("mode").GetString());
    }

    private static LauncherOptions Options() => new()
    {
        Listen = "127.0.0.1:0", PasswordIterations = 100000,
        Instances = [new() { Id = "kr-1256", Build = "kr-1256", Port = 7100, Name = [new() { Language = "en", Text = "Client build" }] }],
    };

    [Fact]
    public void UnconfiguredCenterIsDisabled()
    {
        var options = Options();
        Assert.False(options.Center.Enabled);
        Assert.Equal("gateway", options.Center.Mode);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Gateway")]
    [InlineData("automatic")]
    public void InvalidCenterModeIsRejected(string? mode)
    {
        var options = Options();
        options.Center.Mode = mode!;
        Assert.Contains(options.Validate(), error => error.ErrorMessage == "center requires enabled and a native or gateway mode.");
    }

    [Fact]
    public void ExplicitNullCenterIsRejected()
    {
        var options = Options();
        options.Center = null!;
        Assert.Contains(options.Validate(), error => error.ErrorMessage == "center requires enabled and a native or gateway mode.");
    }

    private sealed class Directory : IServerDirectory
    {
        public IReadOnlyList<ServerSelectorEntry> ListServers(string clientBuild) =>
        [
            new(1, "Automatic", true, [new("a", "node.example", 7100, "formal", "", true, false, 2, 8)
            {
                ObservedAtUtc = DateTimeOffset.UnixEpoch, ExpiresAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(15),
                StatusRevision = "observation-1",
            }]),
            new(2, "Manual", false, [new("a", "node.example", 7100, "formal", "", true, false, 2, 8)]),
            new(3, "Unavailable", false, []),
        ];
        public IReadOnlyList<FederatedRoom> ListRooms(string requestingNodeId, string clientBuild) => throw new NotSupportedException();
    }
}
