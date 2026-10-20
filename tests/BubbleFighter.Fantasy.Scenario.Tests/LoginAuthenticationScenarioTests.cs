using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

public sealed class LoginAuthenticationScenarioTests
{
    [Fact]
    public async Task TicketAuthenticationWithoutTheLauncherRowFailsAtStartup()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ScenarioServer.StartAsync((game, relay) =>
            WithAuthentication(ScenarioServer.Rows(game, relay), "ticket")));
        Assert.Contains("authentication: ticket requires an active launcher.http row", Flatten(error));
    }

    [Fact]
    public async Task OpenAuthenticationStartsWithoutTheLauncherRow()
    {
        await using var server = await ScenarioServer.StartAsync((game, relay) =>
            WithAuthentication(ScenarioServer.Rows(game, relay), "open"));
        Assert.NotEqual(0, server.GamePort);
    }

    private static List<Abstractions.PluginRow> WithAuthentication(List<Abstractions.PluginRow> rows, string mode)
    {
        var index = rows.FindIndex(row => row.Id == "lobby");
        rows[index] = rows[index] with
        {
            Config = new Dictionary<string, object?>(rows[index].Config!) { ["authentication"] = mode },
        };
        return rows;
    }

    private static string Flatten(Exception error)
    {
        var text = "";
        for (Exception? current = error; current is not null; current = current.InnerException) text += current.Message + "\n";
        return text;
    }
}
