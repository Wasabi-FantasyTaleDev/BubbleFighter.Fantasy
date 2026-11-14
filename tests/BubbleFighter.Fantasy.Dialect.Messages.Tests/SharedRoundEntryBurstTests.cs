using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete native roster, signature and stage bursts before sharing their delivery sequence.</summary>
public sealed class SharedRoundEntryBurstTests
{
    /// <summary>Bomb series settings arrive while the native waiting-room handler can consume them; later entries carry only the new index.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-400", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", false)]
    [InlineData("cn-706", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task BombSeriesFlagsPrecedeOnlyTheInitialEntry(string build, bool modern)
    {
        var table = TestBuilds.Opcodes(build);
        var session = new Session(table.Profile);
        var room = new RoomView(7, "Room", 19, "MapA", 8, 0, false, [],
            [new(0, 101, "Alpha", 10, 1, true, 300010, [300010])]);
        if (modern)
            room = room with { BuildSettings = new RoomBuildSettings(build,
                BubbleFighter.Fantasy.Packets.PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", new Dictionary<string, object?>()))
            {
                SessionPrefix = BubbleFighter.Fantasy.Packets.PacketCodec.Write(TestBuilds.Packets, build, "SessionSetting", new Dictionary<string, object?>()),
            } };
        var signature = table.Find(PacketDirection.ServerToClient, modern ? "gspSessionSignatureData" : "gspSetSessionSigData");
        var initialOpcodes = new List<ushort> { table.S2C("gspRoomChangeGameSetting"), table.S2C("gspRoomSetSlot") };
        if (signature is { } opcode) initialOpcodes.Add(opcode);
        initialOpcodes.Add(table.S2C("gspRoomEnterGame"));
        await SendAsync(table, build, modern, session,
            new(room, "MapA") { Series = new(0, 1) });
        Assert.Equal(initialOpcodes, session.Sent.Select(frame => frame.Opcode));
        var fields = RoundSeriesFields.Require(TestBuilds.Packets, build);
        var settings = BubbleFighter.Fantasy.Packets.PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", session.Sent[0].Body);
        Assert.Equal(1, Convert.ToInt32(settings[fields.SettingsFlags]) & 1);
        session.Sent.Clear();
        await SendAsync(table, build, modern, session,
            new(room, "MapA") { Series = new(1, 2) });
        Assert.Equal(initialOpcodes.Skip(1), session.Sent.Select(frame => frame.Opcode));
    }

    /// <summary>Preserves each native entry envelope, profile order and recipient-local opcode mapping.</summary>
    [Theory]
    [InlineData("kr-812", false, "250,248,7,36",
        "49F60DFD8B91446BBD061D786DF56F35ECD8ACC0198801E5EFB0ECC6A6705D44",
        "EEDB0954A809E6210D5A7E650EE695D14796A48658A1962F85CAD8E63C7CB3D6")]
    [InlineData("cn-279", false, "132,130,1442",
        "B61C383C92FB5DDCACDB9047EFAFF23FA7BEE66BAB7B59DEB92612EA2F406C04",
        "B61C383C92FB5DDCACDB9047EFAFF23FA7BEE66BAB7B59DEB92612EA2F406C04")]
    [InlineData("cn-324", false, "158,156,2030",
        "837D7A102DCD7FFC86242007A341179AEE22272A4C85CC20BEB885D62301F0AF",
        "837D7A102DCD7FFC86242007A341179AEE22272A4C85CC20BEB885D62301F0AF")]
    [InlineData("id-400", false, "186,184,7,2298",
        "7FEAC08FF03E39D451B3B15B04A55C6D4EBE4055228E711129BFFA095A40DACD",
        "05278DC5EB354B52573A1611F527E3E4746E62BD7E06DC89EFE665039D9A795E")]
    [InlineData("cn-717", false, "246,244,7,40",
        "2A6A970C31C37D81281516BB5C6D453C252BB484F8A93789DD566EEFD7B14B4D",
        "104F321A6787A0B9D442081C88358670B393D8DAEB7B72331CDC2535720E534E")]
    [InlineData("kr-1256", true, "316,318,7,67",
        "30DE6CFC8311CF026CBDE29F81CC80886E32B2F1EEB7A17D811481AAFCEEAAA2",
        "81C5EBDEE656B73D952A679FFDEAE9D0A7E70B93D52B87B8410C6FCDF7B0ADF2")]
    [InlineData("cn-774", true, "279,281,7,67",
        "3B42271108B98108298FCB8A431EC65C666CF1DFE8340E68B5A4DBC10E382990",
        "E348B192F200DA8A24184460CAFB24925CC0894C39259D2BC0B4F0CDE669FFA3")]
    [InlineData("tw-454", true, "254,256,7,67",
        "14176F27166474173020D98E1E67DE1743DC08E3DE85D494556A09F390A6655D",
        "D58698A88C7943A67EAD1F7B26D6649EFFBC119E03F19477B2553538A9EBCA6A")]
    public async Task RosterSignatureAndStageRetainTheirNativeBodies(string build, bool modern,
        string lengths, string ordinaryDigest, string missionDigest)
    {
        foreach (var mission in new[] { false, true })
        foreach (var remap in new[] { false, true })
        {
            var native = TestBuilds.Opcodes(build);
            IOpcodeTable table = remap ? new OpcodeTable(native.Profile,
                native.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) })) : native;
            var session = new Session(table.Profile);
            RoomMemberView[] seats = [new(3, 101, "Alpha", 10, 1, true, 300010, [300010, 200010]),
                new(1, 102, "Beta", 20, 2, false, 300010, [300010, 200011])];
            var room = new RoomView(7, "Room", mission ? 101 : 1, "Unselected", 8, 3, false, [], seats)
            {
                MemberOrder = [1, 3],
            };
            var message = new RoomMatchStarting(room, "MapA") { StageKey = "StageA", IsMission = mission };
            await SendAsync(table, build, modern, session, message);

            var signature = table.Find(PacketDirection.ServerToClient,
                modern ? "gspSessionSignatureData" : "gspSetSessionSigData");
            var expected = new List<ushort> { table.S2C("gspRoomSetSlot"), table.S2C("gspRoomSetSlot") };
            if (signature is { } code) expected.Add(code);
            expected.Add(table.S2C("gspRoomEnterGame"));
            Assert.Equal(expected, session.Sent.Select(frame => frame.Opcode));
            if (signature is not null)
                Assert.Equal(mission ? "65000000000000" : "01000000000000", Convert.ToHexString(session.Sent[^2].Body));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
                string.Join('/', session.Sent.Select(frame => Convert.ToHexString(frame.Body))))));
            Assert.Equal(lengths, string.Join(',', session.Sent.Select(frame => frame.Body.Length)));
            if (build == "cn-279")
            {
                // cn-279 sub_685920 consumes rank at +20; cn-279 sub_701BD0 consumes the separate outer IPv4 at +32.
                foreach (var frame in session.Sent.Take(2))
                {
                    var record = BubbleFighter.Fantasy.Packets.PacketCodec.Read(TestBuilds.Packets, build, "RoomSeatUpdate", frame.Body);
                    var user = Assert.IsType<Dictionary<string, object?>>(record["user"]);
                    var seat = Assert.Single(seats, seat => seat.UserNum == Convert.ToInt32(user["user_number"]));
                    Assert.Equal(seat.Level, Convert.ToInt32(user["level"]));
                    Assert.Equal(0u, Convert.ToUInt32(user["outer_ipv4"]));
                }
            }
            Assert.True(string.Equals(mission ? missionDigest : ordinaryDigest, digest, StringComparison.Ordinal),
                $"Native burst digest for {build}: {digest}");
        }
    }

    private static async Task SendAsync(IOpcodeTable table, string build, bool modern, Session session, RoomMatchStarting message)
    {
        if (!modern)
        {
            var (schema, play) = TestBuilds.Of(build);
            var codec = new PlayRoomCodec(build + "/game", build, schema, play,
                new Tables(table), NullLogger.Instance, TestBuilds.Packets);
            await codec.SendAsync(session, message, default);
            return;
        }
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var registration = services.Register<IPacketRouter>(routes);
        using var messages = services.Register<IMessageRouter>(new Messages());
        var context = new PluginContext(new PluginRow { Id = "round-entry-pin", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new RoundStartBinding(build + "/game", table, TestBuilds.Packets, build, [1, 0, 0], NullLogger.Instance)
        {
            MapReportBound = true,
        };
        try
        {
            binding.Mount(context);
            Assert.Single(services.GetAll<IMessageSender<RoomMatchStarting>>());
            await binding.SendAsync(session, message, default);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<RoomMatchStarting>>());
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class Messages : IMessageRouter
    {
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct)
            where T : IMessage => ValueTask.FromResult(true);
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
}
