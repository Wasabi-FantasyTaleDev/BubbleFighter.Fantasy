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

/// <summary>Checks that native map round counts mount only with their declared client-data and racing exchange dependencies.</summary>
public sealed class ExtremeRacingMapRoundsActivationTests
{
    /// <summary>The production binder keeps native map counts opt-in under the documented snake_case key.</summary>
    [Fact]
    public void MapRoundCountsBindExplicitlyAndDefaultOff()
    {
        Assert.False(Bind(new()).ExtremeRacingMapRounds);
        Assert.True(Bind(new() { ["extreme_racing_map_rounds"] = true }).ExtremeRacingMapRounds);
    }

    /// <summary>A waiting-room-only build or a missing racing exchange must reject the enabled option during activation.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EnabledMapRoundsRequireLoadAndRacingBindings(bool load, bool goal)
    {
        var error = await Assert.ThrowsAsync<ConfigException>(() => ActivateAsync(true, load, goal, true));
        Assert.Contains("extreme_racing_map_rounds", error.Message, StringComparison.Ordinal);
        Assert.Contains("round loading and racing goal bindings", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The map-count policy cannot activate without the resource store that supplies the selected map.</summary>
    [Fact]
    public async Task EnabledMapRoundsRequireClientData()
    {
        var error = await Assert.ThrowsAsync<ConfigException>(() => ActivateAsync(true, true, true, false));
        Assert.Contains("extreme_racing_map_rounds", error.Message, StringComparison.Ordinal);
        Assert.Contains("client-data", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The full racing exchange remains mandatory when native map counts are enabled.</summary>
    [Theory]
    [InlineData(nameof(RoundExtremeRacingTimerChanged))]
    [InlineData(nameof(RoundExtremeRacingRoundChanged))]
    [InlineData(nameof(RoundExtremeRacingRecordsUpdated))]
    public async Task EnabledMapRoundsRequireEveryRacingSender(string omitted)
    {
        var error = await Assert.ThrowsAsync<ConfigException>(() => ActivateAsync(true, true, true, true, omitted));
        Assert.Contains(omitted, error.Message, StringComparison.Ordinal);
    }

    /// <summary>The plugin declaration permits the complete map-count dependency set and all publications unwind.</summary>
    [Fact]
    public async Task EnabledMapRoundsActivateWithDeclaredDependencies() =>
        await ActivateAsync(true, true, true, true);

    /// <summary>Profiles that leave the option disabled can still mount a waiting-room-only surface without client data.</summary>
    [Fact]
    public async Task DisabledMapRoundsNeedNoRacingDependencies() =>
        await ActivateAsync(false, false, false, false);

    private static InGameOptions Bind(Dictionary<string, object?> config) =>
        new PluginConfig(new PluginRow { Id = "ingame", Plugin = "ingame", Source = "test", Config = config }).Bind<InGameOptions>();

    private static async Task ActivateAsync(bool enabled, bool load, bool goal, bool clientData, string? omitted = null)
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
            if (clientData) root.Effect(services.Register<IClientDataStore>(new Store()));
            context = Context("ingame", new()
            {
                ["game_protocol"] = protocol, ["relay_protocol"] = "test/relay",
                ["extreme_racing_map_rounds"] = enabled,
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
            if (typeof(T).Name != omitted)
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
