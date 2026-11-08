using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Verifies every shared legacy room instance publishes its own removable native capability directory.</summary>
public sealed class NativeCapabilityPublicationTests
{
    /// <summary>Room-only dialects cannot silently accept an exclusion whose shared round path will never mount.</summary>
    [Theory]
    [InlineData(false, "map-load")]
    [InlineData(true, "respawn request")]
    public async Task RespawnExclusionsRequireTheNativeRoundRequestPath(bool loadBound, string diagnostic)
    {
        const string protocol = "test/game";
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var root = Context("root", new());
        var context = Context("ingame", new() { ["game_protocol"] = protocol, ["respawn_disabled_mode_ids"] = new[] { 19 } });
        try
        {
            await new PacketRouterPlugin().ActivateAsync(root, default);
            await new MessageRouterPlugin().ActivateAsync(root, default);
            root.Effect(services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(new CreateBinding(protocol)));
            if (loadBound)
                root.Effect(services.RegisterMany<IInboundMessageBinding<RoundLoadReported>>(new LoadBinding(protocol)));
            RegisterSender<RoomEntered>(); RegisterSender<RoomSeatUpdated>();
            RegisterSender<RoomSeatVacated>(); RegisterSender<RoomMasterChanged>();
            var error = await Assert.ThrowsAsync<ConfigException>(() => new InGamePlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("respawn_disabled_mode_ids", error.Message, StringComparison.Ordinal);
            Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        }
        finally { await context.UnwindAsync(); await root.UnwindAsync(); }

        PluginContext Context(string id, Dictionary<string, object?> config) => new(
            new PluginRow { Id = id, Plugin = "ingame", Source = "test", Config = config }, services, events, NullLogger.Instance);
        void RegisterSender<T>() where T : IMessage => root.Effect(services.RegisterMany<IMessageSender<T>>(new Sender<T>(protocol)));
    }

    private sealed class LoadBinding(string protocol) : IInboundMessageBinding<RoundLoadReported>
    {
        public string Protocol => protocol;
    }

    [Fact]
    public async Task SevenTypedRoomInstancesPublishProtocolScopedDirectoriesAndUnloadIndependently()
    {
        var builds = new[] { "kr-812", "cn-717", "cn-324", "cn-279", "cn-406", "id-400", "id-1809" };
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var root = Context("root", new());
        var contexts = new List<PluginContext>();
        try
        {
            await new PacketRouterPlugin().ActivateAsync(root, default);
            await new MessageRouterPlugin().ActivateAsync(root, default);
            foreach (var build in builds)
            {
                var protocol = build + "/game";
                root.Effect(services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(new CreateBinding(protocol)));
                RegisterSender<RoomEntered>(protocol);
                RegisterSender<RoomSeatUpdated>(protocol);
                RegisterSender<RoomSeatVacated>(protocol);
                RegisterSender<RoomMasterChanged>(protocol);
                var context = Context(build, new()
                {
                    ["game_protocol"] = protocol,
                    ["relay_protocol"] = build + "/relay",
                });
                contexts.Add(context);
                await new InGamePlugin().ActivateAsync(context, default);
            }
            var published = services.GetAll<IMatchCapabilityDirectory>().ToArray();
            Assert.Equal(builds.Select(build => build + "/relay"), published.Select(directory => directory.Protocol));
            Assert.Equal(7, published.Distinct().Count());
            Assert.False(services.TryGet<IMatchRoomDirectory>(out _));
            await contexts[2].UnwindAsync();
            Assert.Equal(6, services.GetAll<IMatchCapabilityDirectory>().Count);
            Assert.DoesNotContain(services.GetAll<IMatchCapabilityDirectory>(), directory => directory.Protocol == "cn-324/relay");
            Assert.Contains(services.GetAll<IMatchCapabilityDirectory>(), directory => ReferenceEquals(directory, published[0]));
        }
        finally
        {
            foreach (var context in contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
            await root.UnwindAsync();
        }
        Assert.Empty(services.GetAll<IMatchCapabilityDirectory>());

        PluginContext Context(string id, Dictionary<string, object?> config) => new(
            new PluginRow { Id = id, Plugin = "ingame", Source = "test", Config = config }, services, events, NullLogger.Instance);
        void RegisterSender<T>(string protocol) where T : IMessage =>
            root.Effect(services.RegisterMany<IMessageSender<T>>(new Sender<T>(protocol)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationRoomAdmissionsAreOptInAndUnwindWithTheirOwningBuild(bool enabled)
    {
        const string protocol = "test/game";
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var root = Context("root", new());
        var context = Context("ingame", new()
        {
            ["game_protocol"] = protocol, ["relay_protocol"] = "test/relay",
            ["room_admission_reservations"] = enabled,
        });
        try
        {
            await new PacketRouterPlugin().ActivateAsync(root, default);
            await new MessageRouterPlugin().ActivateAsync(root, default);
            root.Effect(services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(new CreateBinding(protocol)));
            root.Effect(services.RegisterMany<IInboundMessageBinding<RoomJoinRequested>>(new JoinBinding(protocol)));
            RegisterSender<RoomEntered>(); RegisterSender<RoomSeatUpdated>(); RegisterSender<RoomSeatVacated>();
            RegisterSender<RoomMasterChanged>(); RegisterSender<RoomJoinRefused>();
            await new InGamePlugin().ActivateAsync(context, default);
            var admissions = services.GetAll<IRoomAdmissionDirectory>();
            if (enabled) Assert.Equal("test", Assert.Single(admissions).ClientBuild);
            else Assert.Empty(admissions);
            await context.UnwindAsync();
            Assert.Empty(services.GetAll<IRoomAdmissionDirectory>());
            Assert.Empty(services.GetAll<IPublishedRoomDirectory>());
        }
        finally { await context.UnwindAsync(); await root.UnwindAsync(); }

        PluginContext Context(string id, Dictionary<string, object?> config) => new(
            new PluginRow { Id = id, Plugin = "ingame", Source = "test", Config = config }, services, events, NullLogger.Instance);
        void RegisterSender<T>() where T : IMessage =>
            root.Effect(services.RegisterMany<IMessageSender<T>>(new Sender<T>(protocol)));
    }

    private sealed class JoinBinding(string protocol) : IInboundMessageBinding<RoomJoinRequested>
    {
        public string Protocol => protocol;
    }

    private sealed class CreateBinding(string protocol) : IInboundMessageBinding<RoomCreateRequested>
    {
        public string Protocol => protocol;
    }

    private sealed class Sender<T>(string protocol) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => protocol;
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) => throw new NotSupportedException();
    }
}
