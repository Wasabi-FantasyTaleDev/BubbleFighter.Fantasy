using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherCompositionTests
{
    // The ticket provider must activate before lobby and match services. Request-only HTTP tests
    // bypass these declarations, so exercise the real launcher row in both composition orders.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TicketsStartBeforeTheirLobbyAndMatchConsumers(bool reverse)
    {
        var rows = new List<PluginRow>
        {
            Row("store", "persistence.memory"),
            Row("launcher", "launcher.http", new() { ["listen"] = "127.0.0.1:0", ["password_iterations"] = 100000 }),
            Row("lobby", "test.launcher-lobby"),
            Row("match", "test.launcher-match"),
        };
        if (reverse) rows.Reverse();
        var catalog = PluginCatalog.ScanAssemblies([
            typeof(LauncherHttpPlugin).Assembly, typeof(MemoryPersistencePlugin).Assembly, typeof(LauncherCompositionTests).Assembly]);
        var host = new PluginHost(rows, catalog, NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            var reloaded = await host.ReloadAsync("launcher", default);
            Assert.Equal(new[] { "launcher", "lobby", "match" }, reloaded.ReloadedRowIds.Order(StringComparer.Ordinal));
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(default); }
        Assert.All(host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
    }

    private static PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
        new() { Id = id, Plugin = plugin, Source = "test", Config = config ?? [] };

    [Plugin("test.launcher-lobby", Requires = [typeof(ILauncherTicketService)], Provides = [typeof(IOnlinePlayers)])]
    public sealed class LobbyConsumer : IPlugin, IOnlinePlayers
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            _ = context.Services.Get<ILauncherTicketService>();
            context.Effect(context.Services.Register<IOnlinePlayers>(this));
            return ValueTask.CompletedTask;
        }

        public ISession? FindByUserNum(int userNum) => null;
        public ISession? FindByNickname(string nickname) => null;
    }

    [Plugin("test.launcher-match", Requires = [typeof(IOnlinePlayers)], Provides = [typeof(IMatchConnectionTracker)])]
    public sealed class MatchConsumer : IPlugin, IMatchConnectionTracker
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            _ = context.Services.Get<IOnlinePlayers>();
            context.Effect(context.Services.Register<IMatchConnectionTracker>(this));
            return ValueTask.CompletedTask;
        }

        public bool IsMatchBound(ISession session) => false;
    }
}
