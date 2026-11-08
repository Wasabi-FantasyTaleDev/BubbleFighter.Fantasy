using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class FighterInfoRegistrationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BattleInspectionRequiresBothTheRouteAndACapableStore(bool fighterInfo, bool hasStore)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var table = new MemoryPlayerTable(4);
        using var inventory = services.Register<IInventoryRepository>(new MemoryInventoryRepository(table));
        using var records = hasStore
            ? services.Register<IPlayerBattleRecordsRepository>(new MemoryPlayerProgressRepository(table)) : null;
        using var login = services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new Wire<LoginRequested>());
        using var success = services.RegisterMany<IMessageSender<LoginSucceeded>>(new Wire<LoginSucceeded>());
        using var rejected = services.RegisterMany<IMessageSender<LoginRejected>>(new Wire<LoginRejected>());
        using var request = services.RegisterMany<IInboundMessageBinding<FighterInfoRequested>>(new Wire<FighterInfoRequested>());
        using var reply = services.RegisterMany<IMessageSender<FighterInfoReplied>>(new Wire<FighterInfoReplied>());
        var router = Context("core.message-router");
        var lobby = Context("lobby", new()
        {
            ["protocol"] = "fighter-records/game",
            ["online_players"] = true,
            ["fighter_info"] = fighterInfo,
            ["fighter_battle_statistics"] = true,
        });
        try
        {
            await new MessageRouterPlugin().ActivateAsync(router, default);
            if (fighterInfo && hasStore)
            {
                await new LobbyPlugin().ActivateAsync(lobby, default);
                Assert.Single(services.Get<IMessageRouter>().Handlers, row => row.MessageType == typeof(FighterInfoRequested));
            }
            else
            {
                var failure = await Assert.ThrowsAsync<ConfigException>(async () => await new LobbyPlugin().ActivateAsync(lobby, default));
                Assert.Contains("fighter_battle_statistics", failure.Message);
            }
            await lobby.UnwindAsync();
            Assert.DoesNotContain(services.Get<IMessageRouter>().Handlers, row => row.MessageType == typeof(FighterInfoRequested));
        }
        finally
        {
            await lobby.UnwindAsync();
            await router.UnwindAsync();
        }

        PluginContext Context(string id, Dictionary<string, object?>? config = null) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "fighter-records-test", Config = config },
                services, events, NullLogger.Instance);
    }

    private sealed class Wire<T> : IInboundMessageBinding<T>, IMessageSender<T> where T : IMessage
    {
        public string Protocol => "fighter-records/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) =>
            throw new InvalidOperationException("The registration check does not send packets.");
    }
}
