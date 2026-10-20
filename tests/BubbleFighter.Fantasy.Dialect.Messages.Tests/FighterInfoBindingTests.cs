using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the legacy player menu's "view info" packets: the two-word request, the reply's length in every build that has
/// the class, the success and failure results, and the routes each build mounts.
/// </summary>
public sealed class FighterInfoBindingTests
{
    // build, C2S, S2C, FighterInfo bytes with an empty nickname. The FighterInfo sizes are the readers': the UserStat
    // tests pin the same nested type (kr-812 380 - 40, id-400 340 - 40, id-1809 344 - 40, cn-324 256 - 40) and
    // cn-406 sub_713260 reads 228.
    public static readonly TheoryData<string, int, int, int> Builds = new()
    {
        { "kr-812", 115, 124, 340 }, // kr-812 sub_6C2EB0 stamps 115, sub_837170 case 124 calls sub_82C960
        { "cn-717", 113, 121, 340 }, // cn-717 sub_7248A0 stamps 113, case 0x79 calls sub_8B41D0
        { "cn-324", 112, 121, 216 }, // cn-324 sub_5DB160 stamps 112, case 0x79 calls sub_710CC0
        { "id-400", 115, 124, 300 }, // id-400 sub_645930, case 0x7C calls sub_7B6A00
        { "id-1809", 115, 124, 304 }, // id-1809 sub_6753C0, case 0x7C calls sub_7D9DF0
        { "cn-706", 115, 124, 300 }, // cn-706 sub_655A70, case 0x7C calls sub_7C02D0
        { "cn-404", 115, 124, 300 }, // cn-404 sub_675170, case 0x7C calls sub_7E4590
        { "cn-408", 115, 124, 300 }, // cn-408 sub_6788B0, case 0x7C calls sub_7E7C10
        { "cn-406", 115, 124, 228 }, // cn-406 sub_5CD2A0, case 0x7C calls sub_731830
    };

    /// <summary>The request is the clicked account id and the word after it; a short or long body is refused.</summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public void RequestCarriesTheClickedAccountIdAndOneMoreWord(string build, int request, int reply, int fighter)
    {
        _ = (reply, fighter);
        var binding = Binding(build, out var table);
        Assert.Equal((ushort)request, table.C2S("gcpReqFighterInfo"));
        var body = Bytes(b => { Word(b, 4242); Word(b, unchecked((int)0xA1B2C3D4)); });
        Assert.Equal(new FighterInfoRequested(4242), binding.Read(body));
        Assert.Throws<PacketFormatException>(() => binding.Read(body[..7]));
        Assert.Throws<PacketFormatException>(() => binding.Read([.. body, 0]));
    }

    /// <summary>
    /// A found player is result 1, the player's block and constructor-default pieces; the length is the reader's: a
    /// word, two FighterInfo and two empty strings, plus the characters of the nickname.
    /// </summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public void FoundReplyCarriesTheNicknameAndLevelInTheFirstBlock(string build, int request, int reply, int fighter)
    {
        _ = request;
        var binding = Binding(build, out var table);
        Assert.Equal((ushort)reply, table.S2C("gspRepFighterInfo"));
        var body = binding.Write(new FighterInfoReplied(new PlayerIdentity(77, "u77", "Ab", 12)));
        Assert.Equal(4 + (fighter + 4) + fighter + 8, body.Length);
        Assert.Equal(1, Int(body, 0));
        // FighterInfo: a word, the nickname, two words of which the second is the level.
        Assert.Equal(2, Int(body, 8));
        Assert.Equal("Ab", Encoding.Unicode.GetString(body, 12, 4));
        Assert.Equal(12, Int(body, 20));
        // The partner block is not the player's: its nickname is empty and its level zero.
        var partner = 4 + fighter + 4;
        Assert.Equal(0, Int(body, partner + 4));
        Assert.Equal(0, Int(body, partner + 12));
    }

    /// <summary>
    /// A player who is not logged in gets the build's failure: result 0 and a whole body of constructor defaults, which
    /// carries nobody's nickname or level.
    /// </summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public void UnknownPlayerGetsResultZeroAndAWholeDefaultBody(string build, int request, int reply, int fighter)
    {
        _ = (request, reply);
        var binding = Binding(build, out _);
        var body = binding.Write(new FighterInfoReplied(null));
        Assert.Equal(4 + fighter + fighter + 8, body.Length);
        Assert.Equal(0, Int(body, 0));
        foreach (var block in new[] { 4, 4 + fighter })
        {
            Assert.Equal(0, Int(body, block + 4));
            Assert.Equal(0, Int(body, block + 12));
        }
        Assert.Equal(new byte[8], body[^8..]);
    }

    /// <summary>The kr-812 found body is exactly what the reader kr-812 sub_83DD00 consumes: pinned at its boundaries.</summary>
    [Fact]
    public void Kr812FoundBodyIsPinnedToTheReader()
    {
        var binding = Binding("kr-812", out _);
        var body = binding.Write(new FighterInfoReplied(new PlayerIdentity(7, "u", "N", 3)));
        Assert.Equal(4 + 342 + 340 + 8, body.Length);
        Assert.Equal(1, Int(body, 0));  // result at +4 of the object
        Assert.Equal(1, Int(body, 8));  // nickname length, FighterInfo +8
        Assert.Equal("N", Encoding.Unicode.GetString(body, 12, 2));
        Assert.Equal(3, Int(body, 18)); // FighterInfo +40, shown as the level by kr-812 sub_87DD90
        Assert.Equal(new byte[8], body[^8..]); // both strings empty
    }

    /// <summary>Every build with the class mounts exactly one route, on its own request number, and unwinds it.</summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public async Task EveryBuildMountsItsRequestRoute(string build, int request, int reply, int fighter)
    {
        _ = (reply, fighter);
        var binding = Binding(build, out _);
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var messages = new Messages();
        var context = new PluginContext(new PluginRow { Id = "fighter-info-binding", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            binding.Bind(context, messages);
            var handler = Assert.Single(routes.Handlers);
            Assert.Equal((build + "/game", (ushort)request), handler.Key);
            Assert.Single(services.GetAll<IMessageSender<FighterInfoReplied>>());
            Assert.Single(services.GetAll<IInboundMessageBinding<FighterInfoRequested>>());

            var body = Bytes(b => { Word(b, 31); Word(b, 0); });
            await handler.Value(new Session(), new InboundPacket((ushort)request, body), default);
            Assert.Equal(new FighterInfoRequested(31), Assert.Single(messages.Dispatched));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<FighterInfoReplied>>());
    }

    /// <summary>The reply goes out under the build's S2C number.</summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public async Task ReplyIsSentUnderTheBuildsNumber(string build, int request, int reply, int fighter)
    {
        _ = (request, fighter);
        var binding = Binding(build, out _);
        var session = new Session();
        await binding.SendAsync(session, new FighterInfoReplied(null), default);
        Assert.Equal((ushort)reply, Assert.Single(session.Sent).Opcode);
    }

    /// <summary>
    /// A layout that keeps the field names but changes the type the binding casts or sizes is refused when the row
    /// binds, not on the first packet: a string account id would fail its integer cast, and a byte result would send
    /// a reply the reader (kr-1256 sub_99A430) mis-sizes.
    /// </summary>
    [Theory]
    [InlineData("kr-812", "FighterInfoRequest", "target_user_number", "u32", "wstr", "target_user_number")]
    [InlineData("kr-1256", "FighterInfoReply@kr-1256", "result", "u32", "u8", "result")]
    public async Task AFieldWithTheWrongTypeIsRefusedWhenTheRowBinds(string build, string structure, string field,
        string fromType, string toType, string name)
    {
        var directory = PacketTableEdits.WithFieldType(structure, field, fromType, toType);
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), profile);
        var binding = new FighterInfoBinding(build + "/game", table, PacketTables.Load(directory.FullName), build);
        var services = new ServiceRegistry();
        using var router = services.Register<IPacketRouter>(new Routes());
        var context = new PluginContext(new PluginRow { Id = "fighter-info-binding", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var error = Assert.Throws<ConfigException>(() => binding.Bind(context, new Messages()));
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
            Assert.Empty(services.GetAll<IMessageSender<FighterInfoReplied>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>cn-279 reads another request and reply, so it has no capability and no reply table.</summary>
    [Fact]
    public void Cn279HasNoCapabilityAndNoReplyTable()
    {
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-279", "fighter_info"));
        Assert.True(TestBuilds.Packets.CapabilityOf("kr-812", "fighter_info"));
        Assert.Empty(TestBuilds.Packets.Resolve("cn-279", "FighterInfoReply"));
        Assert.Single(TestBuilds.Packets.Resolve("cn-279", "FighterInfoRequest"));
    }

    private static int Int(byte[] body, int at) => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));

    private static byte[] Bytes(Action<List<byte>> write)
    {
        var bytes = new List<byte>();
        write(bytes);
        return [.. bytes];
    }

    private static FighterInfoBinding Binding(string build, out OpcodeTable table)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), profile);
        return new FighterInfoBinding(build + "/game", table, TestBuilds.Packets, build);
    }

    private static void Word(List<byte> bytes, int value)
    {
        var word = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, value);
        bytes.AddRange(word);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    private sealed class Session : ISession
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public long Id => 1;
        public ClientProfile Profile => new("kr-812", "kr", 812, 0, 0, "kr-812");
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Routes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) => throw new NotSupportedException();
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Dispatched { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Dispatched.Add(message);
            return ValueTask.FromResult(true);
        }
    }
}
