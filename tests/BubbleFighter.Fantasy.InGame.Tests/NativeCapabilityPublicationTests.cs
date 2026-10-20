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
                    ["typed_only"] = true,
                    ["game_protocol"] = protocol,
                    ["relay_protocol"] = build + "/relay",
                    ["version_major"] = int.Parse(build[3..]),
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
