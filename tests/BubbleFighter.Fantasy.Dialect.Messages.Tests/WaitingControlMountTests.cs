using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises shared waiting-room controls with native bodies and the CN2013 round gate.</summary>
public sealed class WaitingControlMountTests
{
    [Theory]
    [InlineData("kr-812", 4)]
    [InlineData("cn-717", 4)]
    [InlineData("cn-324", 0)]
    [InlineData("cn-279", 0)]
    [InlineData("id-400", 2)]
    [InlineData("id-1809", 4)]
    [InlineData("cn-406", 0)]
    [InlineData("cn-406", 0, false, true)]
    [InlineData("kr-812", 4, true)]
    public async Task NativeRequestsShareTypedBindingsAndPreserveTutorialInterception(string build, int tailWidth, bool remap = false, bool preparedRoundMount = false)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        IOpcodeTable table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        if (build == "cn-279")
        {
            Assert.Equal((ushort)403, table.C2S("gcpReqJoinSession"));
            Assert.Equal((ushort)111, table.C2S("gcpReqMatch"));
            Assert.Equal((ushort)504, table.C2S("gcpRoomReqReadyStatus"));
            Assert.Equal((ushort)508, table.C2S("gcpRoomReqOpenCloseSlot"));
            Assert.Equal((ushort)509, table.C2S("gcpReqKickFromSession"));
            Assert.Equal((ushort)512, table.C2S("gcpRoomChangeGameSetting"));
            Assert.Equal((ushort)513, table.C2S("gcpRoomChangeTeam"));
        }
        if (remap)
            table = new OpcodeTable(profile, table.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) }));
        var services = new ServiceRegistry();
        var packets = new Routes();
        var messages = new Messages();
        using var packetRegistration = services.Register<IPacketRouter>(packets);
        using var messageRegistration = services.Register<IMessageRouter>(messages);
        var context = new PluginContext(new PluginRow { Id = "waiting-controls", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of(build);
        var entering = 0;
        var interceptReady = false;
        var codec = new PlayRoomCodec(build + "/game", build, schema, play, new Tables(table), NullLogger.Instance, TestBuilds.Packets)
        {
            MultiplayerRoomEntering = (_, _) => { entering++; return ValueTask.CompletedTask; },
            ReadyRequestFirst = (_, _, _) => ValueTask.FromResult(interceptReady),
        };
        var session = new Session(profile);
        var protocol = build + "/game";
        async Task Request(string name, byte[] body) =>
            await packets.DispatchAsync(protocol, session, new(table.C2S(name), body), default);
        try
        {
            if (build == "cn-406" && !preparedRoundMount) codec.MountBrowser(context);
            else codec.Mount(context);
            var join = new ArrayBufferWriter<byte>();
            var writer = new PacketWriter(join);
            writer.WriteInt32(7);
            writer.WriteInt32(-1);
            writer.WriteInt32(0);
            writer.WriteWideStringPrefixed("AB");
            if (tailWidth == 2) writer.WriteUInt16(0);
            if (tailWidth == 4) writer.WriteInt32(0);
            Assert.Equal(20 + tailWidth, join.WrittenCount);
            await Request("gcpReqJoinSession", join.WrittenSpan.ToArray());
            Assert.Equal(new RoomJoinRequested(7, "AB"), Assert.Single(messages.Received));
            Assert.Equal(1, entering);

            await Request("gcpRoomReqReadyStatus", Convert.FromHexString("010000000000000000000000"));
            Assert.Equal(new RoomReadyRequested(true), messages.Received[^1]);
            interceptReady = true;
            await Request("gcpRoomReqReadyStatus", new byte[12]);
            Assert.Equal(2, messages.Received.Count);
            interceptReady = false;
            await Request("gcpRoomChangeTeam", []);
            Assert.IsType<RoomTeamChangeRequested>(messages.Received[^1]);
            await Request("gcpRoomSendChatModuleMsg", Convert.FromHexString("010000000200000041004200"));
            Assert.Equal(new RoomChatSent(1, "AB"), messages.Received[^1]);
            await Request("gcpRoomReqOpenCloseSlot", Convert.FromHexString("0300000001000000"));
            Assert.Equal(new RoomSeatLockRequested(3, true), messages.Received[^1]);
            await Request("gcpReqKickFromSession", Convert.FromHexString("0200000041004200"));
            Assert.Equal(new RoomKickRequested("AB"), messages.Received[^1]);

            var settings = PacketCodec.Write(TestBuilds.Packets, build, "RoomGameSettingChangeRequest",
                new Dictionary<string, object?> { ["map_key"] = "Random", ["mode"] = 1 });
            await Request("gcpRoomChangeGameSetting", settings);
            var change = Assert.IsType<RoomSettingsChangeRequested>(messages.Received[^1]);
            Assert.Equal("Random", change.MapKey);
            Assert.Equal(1, change.ModeId);
            Assert.Equal(settings, change.BuildSettings!.Change.ToArray());
            Assert.Equal(7, messages.Received.Count);

            await Request("gcpReqJoinSession", [.. join.WrittenSpan, 0]);
            Assert.Equal("malformed gcpReqJoinSession", session.Transport.CloseReason);
            Assert.Equal(7, messages.Received.Count);
            Assert.Equal(1, entering);
            if (build == "cn-406" && !preparedRoundMount)
            {
                foreach (var name in new[] { "gcpRoomReqEnterGame", "gcpGameReportMapLoadingComplete", "gcpRespawn", "gcpRequestRespawnIdx", "gcpGameReqRespawn", "gcpReqEndStage" })
                    Assert.False(packets.Handlers.ContainsKey((protocol, table.C2S(name))));
                Assert.Empty(services.GetAll<IMessageSender<RoundStarted>>());
                Assert.Empty(services.GetAll<IMessageSender<RoundScoreboard>>());
                Assert.Empty(services.GetAll<IMessageSender<RoundPlayerRespawned>>());
                Assert.Empty(services.GetAll<IMessageSender<ResultStageAdvanced>>());
            }
            if (preparedRoundMount)
            {
                foreach (var name in new[] { "gcpRoomReqEnterGame", "gcpGameReportMapLoadingComplete", "gcpRespawn", "gcpRequestRespawnIdx", "gcpGameReqRespawn", "gcpReqEndStage" })
                    Assert.True(packets.Handlers.ContainsKey((protocol, table.C2S(name))));
                Assert.Single(services.GetAll<IMessageSender<RoundStarted>>());
                Assert.Single(services.GetAll<IMessageSender<RoundPlayerRespawned>>());
                Assert.Single(services.GetAll<IMessageSender<RoundScoreboard>>());
                Assert.Single(services.GetAll<IMessageSender<ResultStageAdvanced>>());
            }
            if (build is "cn-279" or "cn-406")
            {
                await codec.SendAsync(session, new RoomChatReceived(7, 1, "A", 1, "B"), default);
                Assert.Equal((ushort)509, session.Sent[0].Opcode);
                Assert.Equal("01000100000041000100000000000000010000004200FFFFFFFF00000000", Convert.ToHexString(session.Sent[0].Body));
                await codec.SendAsync(session, new RoomSeatLockChanged(7, 3, true), default);
                Assert.Equal((ushort)512, session.Sent[1].Opcode);
                Assert.Equal("0300000001000000", Convert.ToHexString(session.Sent[1].Body));
                await codec.SendAsync(session, new RoomMemberKicked(7, "A", "B"), default);
                Assert.Equal((ushort)513, session.Sent[2].Opcode);
                Assert.Equal("01000000410001000000420000000000", Convert.ToHexString(session.Sent[2].Body));
            }
            if (build == "cn-279")
            {
                await codec.SendAsync(session, new RoomLeft(7, new RoomMemberView(1, 42, "A", 1, 2, false, 300001, [300001])), default);
                // cn-279's table names the whole plaza family, so the move to the lobby and the own slot
                // travel in the plaza burst rather than after the leave.
                Assert.Equal(new ushort[] { 511, 501 }, session.Sent.Skip(3).Select(packet => packet.Opcode));
                Assert.Equal(114, session.Sent[4].Body.Length);
            }
            if (preparedRoundMount)
            {
                await codec.SendAsync(session, new RoundSpawnOffered(1000000, 5) { IndexRequested = true }, default);
                await codec.SendAsync(session, new RoundPlayerRespawned(7, 0, 1000000, 5), default);
                Assert.Equal((ushort)708, session.Sent[^2].Opcode);
                Assert.Equal((ushort)707, session.Sent[^1].Opcode);
                Assert.Equal("40420F000500000001000000", Convert.ToHexString(session.Sent[^2].Body));
                Assert.Equal(session.Sent[^2].Body, session.Sent[^1].Body);
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(packets.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoomJoinRequested>>());
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoomReadyRequested>>());
        Assert.Empty(services.GetAll<IMessageSender<RoomChatReceived>>());
        Assert.Empty(services.GetAll<IMessageSender<RoomMemberKicked>>());
    }

    private sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        { Received.Add(message); return ValueTask.FromResult(true); }
    }

    private sealed class Routes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            Handlers[(protocolName, packet.Opcode)](session, packet, ct);
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        internal Connection Transport { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public long Id => 1;
        public IConnection Connection => Transport;
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Connection : IConnection
    {
        internal string? CloseReason { get; private set; }
        public long Id => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => CloseReason is not null;
        public ValueTask CloseAsync(string reason) { CloseReason = reason; return ValueTask.CompletedTask; }
    }
}
