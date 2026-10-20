using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the creation bodies and readers of the surviving <c>CharacterCreationBinding</c> across slice S8,
/// which moved the packets onto it: S2C 12 <c>gspNotifyCreateUserInfo</c> (kr-1256) /
/// <c>gspNotifyAutoTutorial_</c> (legacy), S2C 13 <c>gspRepCheckCreateUserName</c> and S2C 14
/// <c>gspRepCreateUserInfo</c>, plus the C2S 31/32/33 readers, for kr-1256 and the kr-812 baseline. The open
/// body is empty on both families because neither client reads a field (kr-1256 sub_9D2FF0 case 12,
/// kr-812 sub_837170 case 12); before the move the binding resolved the legacy family's names only, so it
/// could not mount kr-1256. The C2S 32 old-parser mismatch recorded here is why the shared reader consumes
/// the client's five-piece body; the hand-written parser was deleted with the Fighters creation path.
/// </summary>
public sealed class CreationBindingPinsBeforeMove
{
    /// <summary>The accepted S2C 13 echo for <c>"ab"</c>: result 0, then one prefixed wstring.</summary>
    private const string NameReplyAb = "000000000200000061006200";

    /// <summary>The S2C 13 echo for an empty name: result 0 and a zero-length wstring.</summary>
    private const string NameReplyEmpty = "0000000000000000";

    /// <summary>
    /// The client's real C2S 32 body for <c>"ab"</c> (kr-1256 sub_9A4E90 tail): the prefixed name, the two
    /// words from the selected record (+748, +1336), an empty wstring and one zero word; 24 bytes.
    /// </summary>
    private const string ClientCreateBodyAb = "0200000061006200" + "01000000" + "04000000" + "00000000" + "00000000";

    /// <summary>
    /// The creation-opened frame is empty on both families and keeps each family's packet name at 12: the legacy
    /// family writes gspNotifyAutoTutorial_ (kr-812 sub_837170 case 12) and kr-1256 writes gspNotifyCreateUserInfo
    /// (kr-1256 sub_9D2FF0 case 12); neither reader takes a field.
    /// </summary>
    [Fact]
    public async Task CreationOpenedIsEmptyOnEachFamilysName()
    {
        // Legacy baseline: the legacy name and no body.
        var legacy = Binding("kr-812", out var legacyTable);
        Assert.Equal((ushort)12, legacyTable.S2C("gspNotifyAutoTutorial_"));
        var legacySession = new RecordingSession(Profile("kr-812"));
        await legacy.SendAsync(legacySession, new CharacterCreationOpened(), default);
        var legacyFrame = Assert.Single(legacySession.Sent);
        Assert.Equal((ushort)12, legacyFrame.Opcode);
        Assert.Empty(legacyFrame.Payload);

        // kr-1256: the modern name, the same number and the table-stated empty body.
        var modern = Binding("kr-1256", out var modernTable);
        Assert.Equal((ushort)12, modernTable.S2C("gspNotifyCreateUserInfo"));
        Assert.Null(modernTable.Find(PacketDirection.ServerToClient, "gspNotifyAutoTutorial_"));
        var modernSession = new RecordingSession(Profile("kr-1256"));
        await modern.SendAsync(modernSession, new CharacterCreationOpened(), default);
        var modernFrame = Assert.Single(modernSession.Sent);
        Assert.Equal((ushort)12, modernFrame.Opcode);
        Assert.Empty(modernFrame.Payload);
    }

    /// <summary>S2C 13 pins result 0 and the echoed name: 8 bytes empty and 12 bytes for <c>"ab"</c>.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    public async Task NameReplyPinsTheCurrentBytes(string build)
    {
        var binding = Binding(build, out var table);
        Assert.Equal((ushort)13, table.S2C("gspRepCheckCreateUserName"));
        var session = new RecordingSession(Profile(build));

        await binding.SendAsync(session, new CharacterNameReplied(0, "ab"), default);
        var found = Assert.Single(session.Sent);
        Assert.Equal((ushort)13, found.Opcode);
        Assert.Equal(12, found.Payload.Length);
        Assert.Equal(NameReplyAb, Convert.ToHexString(found.Payload));
        var reader = new PacketReader(found.Payload);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal("ab", reader.ReadWideStringPrefixed());
        Assert.Equal(0, reader.Remaining);

        await binding.SendAsync(session, new CharacterNameReplied(0, string.Empty), default);
        var empty = session.Sent[1].Payload;
        Assert.Equal(8, empty.Length);
        Assert.Equal(NameReplyEmpty, Convert.ToHexString(empty));
    }

    /// <summary>S2C 14 is the leading result word only: four zero bytes for the accept value.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    public async Task CreateReplyPinsTheCurrentFourBytes(string build)
    {
        var binding = Binding(build, out var table);
        Assert.Equal((ushort)14, table.S2C("gspRepCreateUserInfo"));
        var session = new RecordingSession(Profile(build));

        await binding.SendAsync(session, new CharacterCreateReplied(0), default);
        var reply = Assert.Single(session.Sent);
        Assert.Equal((ushort)14, reply.Opcode);
        Assert.Equal(4, reply.Payload.Length);
        Assert.Equal("00000000", Convert.ToHexString(reply.Payload));
        var reader = new PacketReader(reply.Payload);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// The shared C2S 32 reader already consumes the client's five-piece body: a prefixed name, two words,
    /// an empty wstring and one word. 24 bytes for <c>"ab"</c> (Appendix A) and 20 bytes for an empty name.
    /// The deleted Fighters <c>Unknown32Request</c> accepted only the first 12+2N bytes and refused the
    /// client's trailing eight; this pin is the reader that replaced it.
    /// </summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    public void SharedCreateReaderConsumesTheClientsFivePieceBody(string build)
    {
        var binding = Binding(build, out _);
        var body = Convert.FromHexString(ClientCreateBodyAb);
        Assert.Equal(24, body.Length);
        var values = binding.Read("CharacterCreateRequest", body);
        Assert.Equal("ab", values["nickname"]);
        Assert.Equal(1, values["character_type"]);
        Assert.Equal(4, values["unknown_36"]);
        Assert.Equal(string.Empty, values["unknown_40"]);
        Assert.Equal(0, values["unknown_68"]);

        // The empty-name body is 20 zero bytes: a zero-length name plus the two words, the empty wstring
        // and the trailing zero word.
        var emptyName = new byte[20];
        Assert.Equal(20, emptyName.Length);
        var emptyValues = binding.Read("CharacterCreateRequest", emptyName);
        Assert.Equal(string.Empty, emptyValues["nickname"]);
        Assert.Equal(0, emptyValues["character_type"]);

        // The old parser's 12+2N prefix is short here, and a missing or extra byte is refused.
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterCreateRequest", body[..16]));
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterCreateRequest", body[..^1]));
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterCreateRequest", [.. body, 0]));
    }

    /// <summary>
    /// C2S 31 is one prefixed wstring in the shared reader: 4 bytes empty and 8 bytes for <c>"ab"</c>.
    /// The legacy family puts it on 15 and kr-1256 on 31.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 31)]
    [InlineData("kr-812", 15)]
    public void NameRequestReaderIsOnePrefixedString(string build, int request)
    {
        var binding = Binding(build, out var table);
        Assert.Equal((ushort)request, CharacterCreationBinding.ResolveNameRequestOpcode(table));

        var emptyName = Convert.FromHexString("00000000");
        Assert.Equal(4, emptyName.Length);
        Assert.Equal(string.Empty, binding.Read("CharacterNameRequest", emptyName)["nickname"]);

        var ab = Convert.FromHexString("0200000061006200");
        Assert.Equal(8, ab.Length);
        Assert.Equal("ab", binding.Read("CharacterNameRequest", ab)["nickname"]);
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterNameRequest", new byte[3]));
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterNameRequest", [.. ab, 0]));
    }

    /// <summary>
    /// The C2S 33 route is strict about the empty body: an empty frame reaches the typed handler and any
    /// byte is refused before dispatch. The legacy baseline mounts it today; the kr-1256 mount failure is
    /// recorded in <see cref="ModernCreationCannotMountBeforeTheMove"/>.
    /// </summary>
    [Fact]
    public async Task LoginAfterCreateRouteRejectsANonEmptyBody()
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = Profile("kr-812");
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var packets = new RecordingPackets();
        using var registration = services.Register<IPacketRouter>(packets);
        var context = new PluginContext(new PluginRow { Id = "creation", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var messages = new RecordingMessages();
        var binding = new CharacterCreationBinding("kr-812/game", table, TestBuilds.Packets, "kr-812");
        var session = new RecordingSession(profile);
        try
        {
            binding.Bind(context, messages);
            await packets.DispatchAsync("kr-812/game", session,
                new(table.C2S("gcpLoginAfterCreateUserInfo"), ReadOnlyMemory<byte>.Empty), default);
            Assert.IsType<CharacterLoginRequested>(Assert.Single(messages.Received));

            await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync("kr-812/game", session,
                new(table.C2S("gcpLoginAfterCreateUserInfo"), new byte[1]), default).AsTask());
            Assert.Single(messages.Received);
        }
        finally { context.Unwind(); }
    }

    /// <summary>
    /// The move resolves the two family-specific names from the table: kr-1256 states only
    /// gspNotifyCreateUserInfo and Unknown33, the legacy family only gspNotifyAutoTutorial_ and
    /// gcpLoginAfterCreateUserInfo, and both mount with their three routes and three senders.
    /// </summary>
    [Fact]
    public void ModernCreationMountsAfterTheMove()
    {
        var binding = Binding("kr-1256", out var table);
        Assert.Equal((ushort)31, CharacterCreationBinding.ResolveNameRequestOpcode(table));
        Assert.Equal((ushort)32, table.C2S("gcpReqCreateUserInfo"));
        Assert.Equal((ushort)33, table.C2S("Unknown33"));
        Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpLoginAfterCreateUserInfo"));

        var services = new ServiceRegistry();
        var packets = new RecordingPackets();
        using var registration = services.Register<IPacketRouter>(packets);
        var context = new PluginContext(new PluginRow { Id = "creation", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            binding.Bind(context, new RecordingMessages());
            Assert.Equal(3, packets.Handlers.Count);
            Assert.Single(services.GetAll<IMessageSender<CharacterCreationOpened>>());
            Assert.Single(services.GetAll<IMessageSender<CharacterNameReplied>>());
            Assert.Single(services.GetAll<IMessageSender<CharacterCreateReplied>>());
        }
        finally { context.Unwind(); }
    }

    /// <summary>
    /// The six builds the move enables, plus kr-1256; the modern children state false so the capability is not
    /// inherited by accident.
    /// </summary>
    [Fact]
    public void CreationCapabilityIsStatedPerBuild()
    {
        foreach (var build in new[] { "kr-812", "cn-717", "id-400", "id-1809", "cn-324", "cn-279", "kr-1256" })
            Assert.True(TestBuilds.Packets.CapabilityOf(build, "character_creation"), build);
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-774", "character_creation"));
        Assert.False(TestBuilds.Packets.CapabilityOf("tw-454", "character_creation"));
    }

    private static CharacterCreationBinding Binding(string build, out IOpcodeTable table)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = Profile(build);
        table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        return new(build + "/game", table, TestBuilds.Packets, build);
    }

    private static ClientProfile Profile(string build) =>
        new(build, build[..2], int.Parse(build[3..]), 0, 0, build);

    private sealed class RecordingMessages : IMessageRouter
    {
        public List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Received.Add(message);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingPackets : IPacketRouter
    {
        public Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }

        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            Handlers[(protocolName, packet.Opcode)](session, packet, ct);

        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class RecordingSession(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }
}
