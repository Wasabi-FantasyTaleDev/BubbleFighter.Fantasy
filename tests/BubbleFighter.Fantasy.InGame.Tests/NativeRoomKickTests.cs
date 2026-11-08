using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoomCommandsBindingTests
{
    /// <summary>The native context-menu request reaches shared master authority and announces removal before vacating the seat.</summary>
    [Theory]
    [InlineData("kr-1256", "unknown_4")]
    [InlineData("cn-774", "unknown_4")]
    [InlineData("tw-454", "unknown_0")]
    public async Task NativeKickRemovesTheGuestThroughSharedRoomFlow(string build, string modeField)
    {
        using var harness = await Harness.CreateAsync(build: build, roomKicking: true);
        var table = KickTable(build);
        await JoinKickRoom(harness, table, build, modeField);
        var room = harness.Room;
        var hostCount = harness.Host.Opcodes.Count();
        var guestCount = harness.Guest.Opcodes.Count();
        var request = Convert.FromHexString("0500000067007500650073007400");
        Assert.Equal(14, request.Length);
        Assert.Equal(request, PacketCodec.Write(Tables, build, "RoomKickRequest", new Dictionary<string, object?> { ["nickname"] = "guest" }));
        await harness.Dispatch(harness.Host, table.C2S("gcpReqKickFromSession"), request);

        Assert.Equal(harness.Host.Id, Assert.Single(room.Seats).Game.Id);
        Assert.Null(harness.Registry.TryGet(harness.Guest.Id));
        Assert.Equal(SessionPhase.Plaza, harness.Guest.Features.Get<IPlayerSession>().Phase);
        var expected = Convert.FromHexString("0400000068006F0073007400050000006700750065007300740000000000");
        Assert.Equal(30, expected.Length);
        var reply = table.S2C("gspRepKickFromSession");
        Assert.Equal(expected, Assert.Single(harness.Host.Bodies(reply)));
        Assert.Equal(expected, Assert.Single(harness.Guest.Bodies(reply)));
        Assert.Equal(reply, harness.Host.Opcodes.Skip(hostCount).First());
        Assert.Equal(reply, harness.Guest.Opcodes.Skip(guestCount).First());
        Assert.Contains(table.S2C("gspRoomLeaveSlot"), harness.Host.Opcodes.Skip(hostCount + 1));
    }

    /// <summary>Malformed, guest, self, absent-target and started-room requests cannot remove a waiting-room member.</summary>
    [Theory]
    [InlineData("kr-1256", "unknown_4")]
    [InlineData("cn-774", "unknown_4")]
    [InlineData("tw-454", "unknown_0")]
    public async Task NativeKickRejectsMalformedAndUnauthorizedRequests(string build, string modeField)
    {
        using var harness = await Harness.CreateAsync(build: build, roomKicking: true);
        var table = KickTable(build);
        await JoinKickRoom(harness, table, build, modeField);
        var room = harness.Room;
        var opcode = table.C2S("gcpReqKickFromSession");
        byte[] RequestName(string name) => PacketCodec.Write(Tables, build, "RoomKickRequest", new Dictionary<string, object?> { ["nickname"] = name });
        var request = RequestName("guest");
        foreach (var bad in new[] { request[..^1], request.Concat(new byte[] { 0 }).ToArray(), Convert.FromHexString("FFFFFFFF") })
            await Assert.ThrowsAsync<PacketFormatException>(() => harness.Dispatch(harness.Host, opcode, bad));
        await harness.Dispatch(harness.Guest, opcode, RequestName("host"));
        await harness.Dispatch(harness.Host, opcode, RequestName("host"));
        await harness.Dispatch(harness.Host, opcode, RequestName("missing"));
        lock (room.Gate) room.MarkStarting();
        await harness.Dispatch(harness.Host, opcode, request);
        Assert.Equal(2, room.Seats.Count);
        Assert.Same(room, harness.Registry.TryGet(harness.Guest.Id));
        Assert.Empty(harness.Host.Bodies(table.S2C("gspRepKickFromSession")));
        Assert.Empty(harness.Guest.Bodies(table.S2C("gspRepKickFromSession")));
    }

    /// <summary>Opt-in routes resolve names after remapping and release the native request and both typed registrations on unload.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task NativeKickBindingUsesNamedOpcodesAndUnwinds(string build, bool remap)
    {
        var native = KickTable(build);
        Assert.Equal((ushort)510, native.C2S("gcpReqKickFromSession"));
        Assert.Equal((ushort)513, native.S2C("gspRepKickFromSession"));
        var table = remap ? new OpcodeTable(native.Profile, native.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) })) : native;
        var services = new ServiceRegistry();
        var routes = new KickRoutes();
        var messages = new KickMessages();
        using var router = services.Register<IPacketRouter>(routes);
        using var messageRouter = services.Register<IMessageRouter>(messages);
        var context = KickContext(services);
        try
        {
            new RoomCommandsBinding(build + "/game", table, Tables, build, NullLogger.Instance, roomKicking: true).Mount(context);
            Assert.Single(services.GetAll<IInboundMessageBinding<RoomKickRequested>>());
            var sender = Assert.Single(services.GetAll<IMessageSender<RoomMemberKicked>>());
            var session = new RecordingSession(1, table.Profile);
            await routes.DispatchAsync(build + "/game", session,
                new(table.C2S("gcpReqKickFromSession"), Convert.FromHexString("0200000041004200")), default);
            Assert.Equal(new RoomKickRequested("AB"), Assert.Single(messages.Received));
            await sender.SendAsync(session, new RoomMemberKicked(1, "A", "B"), default);
            Assert.Equal("01000000410001000000420000000000", Convert.ToHexString(session.Body(table.S2C("gspRepKickFromSession"))));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoomKickRequested>>());
        Assert.Empty(services.GetAll<IMessageSender<RoomMemberKicked>>());
    }

    /// <summary>Every shipped modern profile keeps kicking disabled until native acceptance and the opt-in patch changes only that setting.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task NativeKickIsExplicitlyOptIn(string build)
    {
        var composer = new ProfileComposer(Path.Combine(Root, "profiles"), name => name == "BFF_MYSQL_PASSWORD" ? "unused" : null);
        var rows = composer.Compose(build, []);
        Assert.False(new PluginConfig(rows.Single(row => row.Id == "dialect.rooms")).Bind<RoomsOptions>().RoomKicking);
        var patched = composer.Compose(build, [Path.Combine(Root, "profiles", "bundles", "modern-room-kicking.patch.yaml")]);
        Assert.True(new PluginConfig(patched.Single(row => row.Id == "dialect.rooms")).Bind<RoomsOptions>().RoomKicking);
        var services = new ServiceRegistry();
        var routes = new KickRoutes();
        using var router = services.Register<IPacketRouter>(routes);
        using var messageRouter = services.Register<IMessageRouter>(new KickMessages());
        var context = KickContext(services);
        var table = KickTable(build);
        try
        {
            new RoomCommandsBinding(build + "/game", table, Tables, build, NullLogger.Instance).Mount(context);
            Assert.False(routes.Handlers.ContainsKey((build + "/game", table.C2S("gcpReqKickFromSession"))));
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoomKickRequested>>());
            Assert.Empty(services.GetAll<IMessageSender<RoomMemberKicked>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A missing request or reply name fails before routes or kick services can be published.</summary>
    [Theory]
    [InlineData("kr-1256", "gcpReqKickFromSession")]
    [InlineData("kr-1256", "gspRepKickFromSession")]
    [InlineData("cn-774", "gcpReqKickFromSession")]
    [InlineData("cn-774", "gspRepKickFromSession")]
    [InlineData("tw-454", "gcpReqKickFromSession")]
    [InlineData("tw-454", "gspRepKickFromSession")]
    public async Task NativeKickFailsPreflightForMissingNamedOpcodes(string build, string missing)
    {
        var native = KickTable(build);
        var table = new OpcodeTable(native.Profile, native.All.Where(entry => entry.Name != missing));
        var services = new ServiceRegistry();
        var routes = new KickRoutes();
        using var router = services.Register<IPacketRouter>(routes);
        using var messageRouter = services.Register<IMessageRouter>(new KickMessages());
        var context = KickContext(services);
        try
        {
            var error = Assert.Throws<ConfigException>(() => new RoomCommandsBinding(build + "/game", table, Tables, build,
                NullLogger.Instance, roomKicking: true).Mount(context));
            Assert.Contains(missing, error.Message, StringComparison.Ordinal);
            Assert.Empty(routes.Handlers);
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoomKickRequested>>());
            Assert.Empty(services.GetAll<IMessageSender<RoomMemberKicked>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The modern opt-in cannot silently apply to legacy or plaza-only rows.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task NativeKickRejectsIncompatibleRoomOptions(bool modern, bool plazaOnly)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow
        {
            Id = "rooms", Plugin = "dialect.rooms", Source = "test", ClientBuild = new("kr", 1256, 1304),
            Config = new Dictionary<string, object?> { ["modern_rooms"] = modern, ["plaza_only"] = plazaOnly, ["room_kicking"] = true },
        }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() => new RoomsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("room_kicking", error.Message, StringComparison.Ordinal);
        }
        finally { await context.UnwindAsync(); }
    }

    private static IOpcodeTable KickTable(string build) => OpcodeTableLoader.Load(
        Path.Combine(Root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);

    private static async Task JoinKickRoom(Harness harness, IOpcodeTable table, string build, string modeField)
    {
        await harness.Dispatch(harness.Host, table.C2S("gcpReqCreateSession"), NativeCreateBody(build, modeField));
        await harness.Dispatch(harness.Guest, table.C2S("gcpReqJoinSession"), PacketCodec.Write(Tables, build, "RoomJoinRequest",
            new Dictionary<string, object?> { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
    }

    private static PluginContext KickContext(ServiceRegistry services) => new(
        new PluginRow { Id = "kick-binding", Plugin = "test", Source = "test" }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);

    private sealed class KickMessages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        { Received.Add(message); return ValueTask.FromResult(true); }
    }

    private sealed class KickRoutes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new KickRemoval(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            Handlers[(protocolName, packet.Opcode)](session, packet, ct);
        private sealed class KickRemoval(Action remove) : IDisposable { public void Dispose() => remove(); }
    }
}
