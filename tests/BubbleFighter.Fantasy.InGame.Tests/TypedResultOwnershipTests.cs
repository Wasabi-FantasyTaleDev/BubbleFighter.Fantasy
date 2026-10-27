using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks result ownership through the shared plugin's actual activation and message routes.</summary>
public sealed class TypedResultOwnershipTests
{
    /// <summary>Concurrent end reports use one typed sequence with no KR final raw opcode registration.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public async Task TypedActivationOwnsOneResultSequenceAndNeverMountsRawResultRoutes(string build)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var protocol = build + "/game";
        var root = Context("root", new());
        var row = Context(build, new()
        {
            ["game_protocol"] = protocol, ["relay_protocol"] = build + "/relay",
            ["result_reward_delay_ms"] = 10, ["result_close_grace_ms"] = 10,
        });
        try
        {
            root.Effect(services.Register<IPacketRouter>(new NoRawRoutes()));
            await new MessageRouterPlugin().ActivateAsync(root, default);
            RegisterBinding<RoomCreateRequested>();
            RegisterBinding<RoundLoadReported>();
            RegisterBinding<RoundEndRequested>();
            var entered = RegisterSender<RoomEntered>();
            RegisterSender<RoomSeatUpdated>();
            RegisterSender<RoomSeatVacated>();
            RegisterSender<RoomMasterChanged>();
            RegisterSender<RoundPlayerLoaded>();
            RegisterSender<RoundCountdownStarted>();
            RegisterSender<RoundStarted>();
            RegisterSender<RoundScoreUpdated>();
            var ended = RegisterSender<RoundEnded>();
            var scoreboard = RegisterSender<RoundScoreboard>();
            var stage = RegisterSender<ResultStageAdvanced>();
            await new InGamePlugin().ActivateAsync(row, default);
            var messages = services.Get<IMessageRouter>();
            Assert.Single(messages.Handlers, handler => handler.Protocol == protocol
                && handler.MessageType == typeof(RoundEndRequested));
            var registry = Assert.IsType<RoomRegistry>(Assert.Single(services.GetAll<IMatchCapabilityDirectory>()));
            var player = new RecordingSession(1);
            var (room, _) = registry.CreateForMode(player, 4, "map", "room", 1, "player", listed: true);
            room.MarkStarting();
            room.MarkPlaying();
            var request = new RoundEndRequested();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                Assert.True(await messages.DispatchAsync(protocol, player, request, default))));
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(ended.Sent);
            Assert.Equal(2, scoreboard.Sent.Count);
            Assert.False(scoreboard.Sent.First().WithRewards);
            Assert.True(scoreboard.Sent.Last().WithRewards);
            Assert.Equal(ResultStage.Reward, Assert.Single(stage.Sent).Stage);
            Assert.Single(entered.Sent);
            Assert.Empty(player.Opcodes);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            await row.UnwindAsync();
            Assert.DoesNotContain(messages.Handlers, handler => handler.Protocol == protocol);
        }
        finally
        {
            await row.UnwindAsync();
            await root.UnwindAsync();
        }

        PluginContext Context(string id, Dictionary<string, object?> config) => new(
            new PluginRow { Id = id, Plugin = "ingame", Source = "test", Config = config }, services, events, NullLogger.Instance);
        void RegisterBinding<T>() where T : IMessage =>
            root.Effect(services.RegisterMany<IInboundMessageBinding<T>>(new Binding<T>(protocol)));
        Sender<T> RegisterSender<T>() where T : IMessage
        {
            var sender = new Sender<T>(protocol);
            root.Effect(services.RegisterMany<IMessageSender<T>>(sender));
            return sender;
        }
    }

    /// <summary>Partial result families fail, while an unaudited result surface can still use the bare end path.</summary>
    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ResultSurfaceRequiresCompleteFamilyButAllowsBareEnd(int missing)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "round", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        const string protocol = "kr-812/game";
        try
        {
            if (missing >= 0 && missing != 0) Register<RoundScoreUpdated>();
            if (missing >= 0 && missing != 1) Register<RoundScoreboard>();
            if (missing != 2 && missing != -2) Register<RoundEnded>();
            if (missing >= 0 && missing != 3) Register<ResultStageAdvanced>();
            var options = new InGameOptions { GameProtocol = protocol };
            var registry = new RoomRegistry(options);
            var senders = new Handlers.RoundSenders(new Sender<RoundPlayerLoaded>(protocol),
                new Sender<RoundCountdownStarted>(protocol), new Sender<RoundStarted>(protocol));
            Handlers.RoundFlow Create() => new(registry, options,
                senders, NullLogger.Instance, default, (_, _) => ValueTask.CompletedTask, context);
            if (missing < 0)
            {
                Assert.NotNull(Create());
                return;
            }
            var error = Assert.Throws<ConfigException>(() => Create());
            Assert.Contains(protocol, error.Message);
            Assert.Contains("incomplete result surface", error.Message);
        }
        finally { context.Unwind(); }

        void Register<T>() where T : IMessage =>
            context.Effect(services.RegisterMany<IMessageSender<T>>(new Sender<T>(protocol)));
    }

    private sealed class Binding<T>(string protocol) : IInboundMessageBinding<T> where T : IMessage
    {
        public string Protocol => protocol;
    }

    private sealed class Sender<T>(string protocol) : IMessageSender<T> where T : IMessage
    {
        internal ConcurrentQueue<T> Sent { get; } = new();
        public string Protocol => protocol;
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoRawRoutes : IPacketRouter
    {
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler) =>
            throw new InvalidOperationException($"Typed activation attempted to register raw opcode {opcode} on {protocolName}.");
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
