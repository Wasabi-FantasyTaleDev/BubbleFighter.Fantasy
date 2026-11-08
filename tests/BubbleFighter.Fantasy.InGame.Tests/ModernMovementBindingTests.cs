using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks that each modern profile mounts its native full and optimized movement names.</summary>
public sealed class ModernMovementBindingTests
{
    /// <summary>Pins each audited modern UDP body, including the opaque eight-byte identity prefix.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativeUdpRelayMovementLayoutsArePinned(string build)
    {
        var root = FindRoot();
        var packets = PacketTables.Load(Path.Combine(root, "packets"));
        var full = PacketCodec.Write(packets, build, "UdpRelayMovement", new Dictionary<string, object?>
        {
            ["unknown_0"] = 0x11223344u, ["unknown_4"] = 0x55667788u,
            ["object_id"] = 1000000, ["x"] = 1.25f, ["y"] = -2.5f, ["z"] = 3.75f,
            ["yaw"] = (byte)4, ["pitch"] = (byte)5, ["state"] = (byte)6, ["tick"] = (ushort)7,
        });
        Assert.Equal(29, full.Length);
        Assert.Equal("443322118877665540420F000000A03F000020C0000070400405060700", Convert.ToHexString(full));

        var optimized = PacketCodec.Write(packets, build, "UdpRelayMovementOptimize", new Dictionary<string, object?>
        {
            ["unknown_0"] = 0x11223344u, ["unknown_4"] = 0x55667788u,
            ["actor"] = 1000000, ["tick"] = (ushort)8, ["mask"] = (byte)0x21,
            ["x"] = 9.5f, ["state"] = (byte)10,
        });
        Assert.Equal(20, optimized.Length);
        Assert.Equal("443322118877665540420F00080021000018410A", Convert.ToHexString(optimized));

        var probe = PacketCodec.Write(packets, build, "UdpRelayProbeReply", new Dictionary<string, object?>
        {
            ["unknown_0"] = 0x0123456789ABCDEFul,
        });
        Assert.Equal(8, probe.Length);
        Assert.Equal("EFCDAB8967452301", Convert.ToHexString(probe));
    }

    /// <summary>The native movement opcodes resolve and decode through the shared typed binding for CN and TW.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task NativeModernMovementMountsAndDecodes(string build)
    {
        var root = FindRoot();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id) {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test" },
                services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        var protocol = build + "/game";
        var relayProtocol = build + "/relay";
        try
        {
            await new PacketRouterPlugin().ActivateAsync(Context("router"), default);
            await new MessageRouterPlugin().ActivateAsync(Context("messages"), default);
            var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"),
                TestWireDialects.Get(build).Profile);
            var packets = PacketTables.Load(Path.Combine(root, "packets"));
            var received = new List<RoundMovementReported>();
            var subscriber = Context("subscriber");
            var messageRouter = services.Get<IMessageRouter>().For(subscriber);
            subscriber.Effect(messageRouter.On<RoundMovementReported>(protocol, (_, message, _) =>
            {
                received.Add(message);
                return ValueTask.CompletedTask;
            }));
            new RoundMovementBinding(protocol, relayProtocol, table, table.ForConnection(OpcodeConnection.Relay), packets,
                build, NullLogger.Instance, string.Empty).Mount(Context("movement"));

            var session = new RecordingSession(7, TestWireDialects.Get(build).Profile);
            var full = PacketCodec.Write(packets, build, "MovementReport", new Dictionary<string, object?>
            {
                ["object_id"] = 1000000, ["x"] = 1.25f, ["y"] = -2.5f, ["z"] = 3.75f,
                ["yaw"] = (byte)4, ["pitch"] = (byte)5, ["state"] = (byte)6, ["tick"] = (ushort)7,
            });
            await services.Get<IPacketRouter>().DispatchAsync(protocol, session,
                new(table.C2S("gppWanderLean"), full), default);
            var report = Assert.Single(received);
            Assert.Equal(new RoundMovementReported(1000000, PlayerMovementState.FullMask, 1.25f, -2.5f, 3.75f, 4, 5, 6, 7), report);

            var optimized = PacketCodec.Write(packets, build, "MovementSample", new Dictionary<string, object?>
            {
                ["actor"] = 1000000, ["tick"] = (ushort)8, ["mask"] = (byte)0x21,
                ["x"] = 9.5f, ["state"] = (byte)10,
            });
            await services.Get<IPacketRouter>().DispatchAsync(protocol, session,
                new(table.C2S("gppWanderLeanOptimize"), optimized), default);
            Assert.Equal(new RoundMovementReported(1000000, 0x21, 9.5f, 0, 0, 0, 0, 10, 8), received[^1]);
        }
        finally
        {
            for (var index = contexts.Count - 1; index >= 0; index--)
                contexts[index].Unwind();
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
