using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed partial class LauncherPresentationTests
{
    [Fact]
    public async Task AggregateCatalogAndRosterUseTheSameExplicitGroupWithoutChangingAdmissionCapacity()
    {
        var options = Options(); options.PresenceGroup = "formal"; options.PlayerLimit = 100;
        var provider = new Population();
        await using var server = await Start(options, new Registry([]), presenceDirectory: provider);
        using var http = Client(server);
        var presentation = (await http.GetFromJsonAsync<JsonElement>("v1/catalog")).GetProperty("presentation");
        Assert.Equal(2, presentation.GetProperty("onlinePlayers").GetInt32());
        Assert.Equal(7, presentation.GetProperty("peakOnlinePlayers").GetInt32());
        Assert.Equal(100, presentation.GetProperty("playerLimit").GetInt32());
        Assert.Equal("formal", presentation.GetProperty("presenceGroup").GetString());
        Assert.False(presentation.GetProperty("presenceComplete").GetBoolean());
        Assert.Equal(new[] { false }, provider.Disclosures);
        var roster = await http.GetFromJsonAsync<JsonElement>("v1/players");
        Assert.False(roster.GetProperty("complete").GetBoolean());
        var card = Assert.Single(roster.GetProperty("players").EnumerateArray());
        Assert.Equal("cn-406", card.GetProperty("buildId").GetString());
        Assert.Equal(3, card.EnumerateObject().Count());
        Assert.Equal(new[] { false, true }, provider.Disclosures);
    }

    [Fact]
    public async Task AggregatePrivacySwitchesSkipTheAuthorityAndTheLocalRegistry()
    {
        var options = Options(); options.PresenceGroup = "formal";
        options.RosterEnabled = false; options.ShowOnlinePlayers = false;
        var provider = new Population { Throw = true };
        await using var server = await Start(options, new Registry([]) { ThrowOnRead = true }, presenceDirectory: provider);
        using var http = Client(server);
        var catalog = (await http.GetFromJsonAsync<JsonElement>("v1/catalog")).GetProperty("presentation");
        Assert.Equal(-1, catalog.GetProperty("onlinePlayers").GetInt32());
        Assert.False(catalog.TryGetProperty("peakOnlinePlayers", out _));
        Assert.False((await http.GetFromJsonAsync<JsonElement>("v1/players")).GetProperty("enabled").GetBoolean());
        Assert.Empty(provider.Disclosures);
    }

    [Fact]
    public async Task ConfiguredAggregateFailureIsExplicitAndDoesNotMasqueradeAsLocalPopulation()
    {
        var options = Options(); options.PresenceGroup = "formal";
        await using var server = await Start(options, new Registry([]), presenceDirectory: new Population { Throw = true });
        using var http = Client(server);
        await AssertError(await http.GetAsync("v1/catalog"), HttpStatusCode.ServiceUnavailable, "player_state_unavailable");
        await AssertError(await http.GetAsync("v1/players"), HttpStatusCode.ServiceUnavailable, "player_state_unavailable");
    }

    [Fact]
    public async Task AggregateConfigurationRequiresItsTypedProvider()
    {
        var options = Options(); options.PresenceGroup = "formal";
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => Start(options));
    }

    [Fact]
    public async Task PrivateLocalReportKeepsBuildAndDeduplicatesOwnersWhileHonoringRosterPrivacy()
    {
        var options = Options(); options.Instances = [];
        var sessions = new Registry([
            new Session(1, new PlayerIdentity(1, "secret", "Old", 2)),
            new Session(2, new PlayerIdentity(1, "secret", "Current", 3))
                { Profile = new("cn-406", "cn", 406, 0, 0, "test") },
            new Session(3, new PlayerIdentity(1, "secret", "Relay", 3), loginConnectionId: 2),
        ]);
        await using var server = await Start(options, sessions);
        var owner = Assert.Single(server.LocalPresence.Capture(true));
        Assert.Equal("cn-406", owner.BuildId);
        Assert.Equal("Current", owner.Name);
        Assert.Null(Assert.Single(server.LocalPresence.Capture(false)).Name);
        options.RosterEnabled = false;
        owner = Assert.Single(server.LocalPresence.Capture(true));
        Assert.Null(owner.Name); Assert.Null(owner.Level);
        Assert.Equal(1, owner.UserNum);
    }

    [Fact]
    public async Task PrivateReportsIncludeOtherBuildsWithoutBroadeningTheStandalonePublicRoster()
    {
        var options = Options();
        var registry = new Registry([
            new Session(1, new(1, "one", "Korean", 2)),
            new Session(2, new(2, "two", "Chinese", 3)) { Profile = new("cn-406", "cn", 406, 0, 0, "test") },
        ]);
        await using var server = await Start(options, registry);
        Assert.Equal(2, server.LocalPresence.Capture(false).Count);
        using var http = Client(server);
        var roster = await http.GetFromJsonAsync<JsonElement>("v1/players");
        Assert.Equal("kr-812", Assert.Single(roster.GetProperty("players").EnumerateArray()).GetProperty("buildId").GetString());
    }

    private sealed class Population : ILauncherPresenceDirectory
    {
        public IReadOnlyList<string> Groups => new[] { "formal" };
        internal bool Throw { get; init; }
        internal List<bool> Disclosures { get; } = [];
        public ValueTask<LauncherPresenceSnapshot> ReadAsync(string group, bool includeRoster, CancellationToken ct)
        {
            Assert.Equal("formal", group);
            if (Throw) throw new HttpRequestException("unavailable");
            Disclosures.Add(includeRoster);
            return ValueTask.FromResult(new LauncherPresenceSnapshot(2, 7, false, false,
                includeRoster ? [new("Visible", 4, "cn-406")] : []));
        }
    }
}
