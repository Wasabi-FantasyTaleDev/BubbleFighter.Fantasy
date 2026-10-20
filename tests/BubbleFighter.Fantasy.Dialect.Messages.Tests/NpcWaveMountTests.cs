using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises actual room-plugin mounting so ordinary NPC waves do not depend on the tutorial path.</summary>
public sealed class NpcWaveMountTests
{
    [Theory]
    [InlineData("kr-812", true, false)]
    [InlineData("cn-717", true, false)]
    [InlineData("cn-324", true, false)]
    [InlineData("cn-279", true, false)]
    [InlineData("id-400", true, false)]
    [InlineData("id-1809", true, false)]
    [InlineData("cn-406", false, true)]
    public async Task NativeNpcRoutesAndSenderAreMountedIndependentlyAndUnloadTogether(
        string build, bool oneSeatWaves, bool browserOnly)
    {
        var (_, play) = TestBuilds.Of(build);
        Assert.Equal(oneSeatWaves, play.OneSeatWaves);
        Assert.Equal(!browserOnly, play.NpcWaves);
        var parts = build.Split('-');
        var profile = new ClientProfile(build, parts[0], int.Parse(parts[1]), 0, 0, build);
        var path = Path.Combine(Path.GetDirectoryName(TestBuilds.PacketsDirectory())!, "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        var services = new ServiceRegistry();
        var packets = new RecordingRoutes();
        using var packetRegistration = services.Register<IPacketRouter>(packets);
        using var tablesRegistration = services.Register<IOpcodeTableRegistry>(new Tables(table));
        using var messagesRegistration = services.Register<IMessageRouter>(new UnusedMessages());
        var context = new PluginContext(new PluginRow
        {
            Id = "room", Plugin = "dialect.rooms", Source = "npc-wave-mount-test",
            ClientBuild = new(parts[0], int.Parse(parts[1]), 0),
            Config = new Dictionary<string, object?>()
            {
                ["protocol"] = build + "/game", ["packets_directory"] = TestBuilds.PacketsDirectory(),
                ["browser_only"] = browserOnly,
            },
        }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            foreach (var name in new[] { "gcpReqStartNewWave", "gcpReqWaveAllSpawnMobs",
                         "gcpReqEndAllWave", "gppCreateMobsFromSpawnGroup", "gppCreateAIObject",
                         "gppCreateSpawnGroupMemberMob" })
            {
                if (browserOnly)
                    Assert.DoesNotContain(packets.Routes.Values, route => route.Method.Name.Contains("Wave", StringComparison.Ordinal));
                else
                    Assert.Contains((build + "/game", table.C2S(name)), packets.Routes.Keys);
            }
            foreach (var name in new[] { "gcpReqEndWave", "gppLifePoint" })
                if (table.Find(PacketDirection.ClientToServer, name) is { } opcode)
                    Assert.Equal(!browserOnly, packets.Routes.ContainsKey((build + "/game", opcode)));
            Assert.Equal(browserOnly ? 0 : 1, services.GetAll<IMessageSender<RoundWaveChanged>>().Count);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(packets.Routes);
        Assert.Empty(services.GetAll<IMessageSender<RoundWaveChanged>>());
    }

    private sealed class RecordingRoutes : IPacketRouter
    {
        public Dictionary<(string Protocol, ushort Opcode), PacketHandler> Routes { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            var key = (protocolName, opcode);
            Routes.Add(key, handler);
            return new RemoveRoute(() => Routes.Remove(key));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            Routes[(protocolName, packet.Opcode)](session, packet, ct);
    }

    private sealed class RemoveRoute(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class UnusedMessages : IMessageRouter
    {
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct)
            where T : IMessage => throw new NotSupportedException();
    }
}
