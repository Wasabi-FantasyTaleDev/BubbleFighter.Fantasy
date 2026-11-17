using System.Reflection;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks map-state activation dependencies and symmetric sender configuration with declarations enforced.</summary>
public sealed class ExtremeRacingMapStateActivationTests
{
    /// <summary>Map-state publication fails at load time when a required native binding, store or sender is missing.</summary>
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task EnabledMapStateRequiresAllDependencies(bool load, bool goal, bool data, bool sender)
    {
        var error = await Assert.ThrowsAsync<ConfigException>(() => ActivateAsync(true, load, goal, data, sender));
        Assert.Contains("extreme_racing_map_state", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A published sender cannot silently remain unused by the in-game row.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledMapStateRejectsAnEnabledSender(bool load)
    {
        var error = await Assert.ThrowsAsync<ConfigException>(() => ActivateAsync(false, load, true, true, true));
        Assert.Contains("extreme_racing_map_state", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The complete dependency set activates under the production plugin declaration and unloads all effects.</summary>
    [Fact]
    public async Task CompleteMapStateDependenciesActivate() => await ActivateAsync(true, true, true, true, true);

    private static async Task ActivateAsync(bool enabled, bool load, bool goal, bool clientData, bool sender)
    {
        const string protocol = "test/game";
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var root = Context("root", new());
        PluginContext? context = null;
        try
        {
            await new PacketRouterPlugin().ActivateAsync(root, default);
            await new MessageRouterPlugin().ActivateAsync(root, default);
            RegisterBinding<RoomCreateRequested>();
            RegisterSender<RoomEntered>(); RegisterSender<RoomSeatUpdated>();
            RegisterSender<RoomSeatVacated>(); RegisterSender<RoomMasterChanged>();
            if (load)
            {
                RegisterBinding<RoundLoadReported>();
                RegisterSender<RoundPlayerLoaded>(); RegisterSender<RoundCountdownStarted>(); RegisterSender<RoundStarted>();
            }
            if (goal)
            {
                RegisterBinding<RoundExtremeRacingGoalReported>();
                RegisterSender<RoundExtremeRacingTimerChanged>(); RegisterSender<RoundExtremeRacingRoundChanged>();
                RegisterSender<RoundExtremeRacingRecordsUpdated>();
            }
            if (sender) RegisterSender<RoundExtremeRacingMapState>();
            if (clientData) root.Effect(services.Register<IClientDataStore>(new Store()));
            context = Context("ingame", new()
            {
                ["game_protocol"] = protocol, ["relay_protocol"] = "test/relay",
                ["extreme_racing_map_state"] = enabled,
            });
            context.BindServices(typeof(InGamePlugin).GetCustomAttribute<PluginAttribute>()!);
            await new InGamePlugin().ActivateAsync(context, default);
            Assert.Equal("test/relay", Assert.Single(services.GetAll<IMatchCapabilityDirectory>()).Protocol);
        }
        finally
        {
            if (context is not null) await context.UnwindAsync();
            await root.UnwindAsync();
            Assert.Empty(services.GetAll<IMatchCapabilityDirectory>());
        }

        PluginContext Context(string id, Dictionary<string, object?> config) => new(
            new PluginRow { Id = id, Plugin = id, Source = "test", Config = config }, services, events, NullLogger.Instance);
        void RegisterBinding<T>() where T : IMessage =>
            root.Effect(services.RegisterMany<IInboundMessageBinding<T>>(new Binding<T>(protocol)));
        void RegisterSender<T>() where T : IMessage
        {
            root.Effect(services.RegisterMany<IMessageSender<T>>(new Sender<T>(protocol)));
        }
    }

    private sealed class Binding<T>(string protocol) : IInboundMessageBinding<T> where T : IMessage
    {
        public string Protocol => protocol;
    }

    private sealed class Sender<T>(string protocol) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => protocol;
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Store : IClientDataStore
    {
        public bool Exists(string path) => path == "ServerShared/MapMeta.xml";
        public byte[] Read(string path) => Exists(path) ? Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"><Worksheet><Table>
              <Row><Cell><Data>[Field]</Data></Cell><Cell><Data>Type</Data></Cell><Cell><Data>map</Data></Cell></Row>
              <Row><Cell><Data>Directory</Data></Cell><Cell><Data>string</Data></Cell><Cell><Data>map</Data></Cell></Row>
            </Table></Worksheet></Workbook>
            """) : throw new FileNotFoundException(path);
    }
}
