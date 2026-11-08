using System.Collections.Concurrent;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins early clients' native discovery, peer rows and distinct datagram header lengths.</summary>
public sealed class UdpPeerWireTests
{
    [Theory]
    [InlineData("cn-279", true)]
    [InlineData("cn-324", true)]
    [InlineData("cn-406", true)]
    [InlineData("id-400", true)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", false)]
    [InlineData("cn-705", false)]
    [InlineData("cn-706", false)]
    public void DiscoverySchedulingMatchesTheNativeScenePump(string build, bool waitingRoomOnly)
    {
        var wire = new UdpPeerWire(TestBuilds.Packets, build, build + "/game", TestBuilds.Opcodes(build));
        Assert.Equal(waitingRoomOnly, wire.DiscoveryInWaitingRoom);
    }

    [Theory]
    [InlineData("cn-279", "0200000000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-324", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-406", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("id-400", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-404", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-408", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-705", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-706", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-735", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-738", "020000000000EFCDAB89C0A80123B01D")]
    [InlineData("cn-742", "020000000000EFCDAB89C0A80123B01D")]
    public async Task DiscoveryAndPeerTableKeepNativeOrderAndNetworkAddressBytes(string build, string discoveryHex)
    {
        var table = TestBuilds.Opcodes(build);
        var wire = new UdpPeerWire(TestBuilds.Packets, build, build + "/game", table);
        var session = new Session(table.Profile);
        var datagram = Convert.FromHexString(discoveryHex);
        var discovery = Assert.IsType<UdpPeerDiscovery>(wire.ReadDiscovery(datagram));
        Assert.Equal(0x89ABCDEFu, discovery.ProbeIdentity);
        Assert.Equal(IPAddress.Parse("192.168.1.35"), discovery.LocalAddress);
        Assert.Equal((ushort)7600, discovery.LocalPort);
        Assert.Throws<PacketFormatException>(() => wire.ReadDiscovery(datagram[..^1]));
        Assert.Throws<PacketFormatException>(() => wire.ReadDiscovery(datagram.Concat(new byte[] { 0 }).ToArray()));
        datagram[0] = 3;
        Assert.Null(wire.ReadDiscovery(datagram));

        await wire.OfferAsync(session, 7600, 0x89ABCDEF, default);
        Assert.Equal((ushort)607, session.Sent[0].Code);
        Assert.Equal("B01DEFCDAB8900", Convert.ToHexString(session.Sent[0].Body));
        await wire.SendCandidateAsync(session, new(3, 0x89ABCDEF, [IPAddress.Parse("192.168.1.35")], 7600,
            new(IPAddress.Parse("203.0.113.17"), 7601)), default);
        Assert.Equal((ushort)610, session.Sent[1].Code);
        Assert.Equal("03000000EFCDAB8901000000C0A80123B01DCB007111B11D", Convert.ToHexString(session.Sent[1].Body));
        await wire.StopDiscoveryAsync(session, default);
        await wire.EndTableAsync(session, default);
        await wire.StartPunchAsync(session, default);
        await wire.FinishPunchAsync(session, default);
        Assert.Equal(new ushort[] { 608, 611, 612, 613 }, session.Sent.Skip(2).Select(packet => packet.Code));
        Assert.All(session.Sent.Skip(2), packet => Assert.Empty(packet.Body));
        Assert.Equal("0200000001000000", Convert.ToHexString(PacketCodec.Write(TestBuilds.Packets, build,
            "UdpPeerPunchReported", new Dictionary<string, object?> { ["occupied_seats"] = 2, ["connected_peers"] = 1 })));
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, "UdpPeerTableAcknowledged", new Dictionary<string, object?>()));
    }

    [Fact]
    public void EarliestHeaderKeepsPeerIdentityAtOffsetFour()
    {
        var header = PacketCodec.Write(TestBuilds.Packets, "cn-279", "UdpRelayDatagramPrefix",
            new Dictionary<string, object?> { ["opcode"] = 32885, ["peer_identity"] = 0x89ABCDEFu });
        Assert.Equal("75800000EFCDAB89", Convert.ToHexString(header));
        Assert.Equal(6, PacketCodec.Write(TestBuilds.Packets, "cn-324", "UdpRelayDatagramPrefix",
            new Dictionary<string, object?>()).Length);
    }

    [Theory]
    [InlineData("cn-279", 21)]
    [InlineData("cn-324", 15)]
    [InlineData("cn-406", 15)]
    [InlineData("id-400", 15)]
    [InlineData("cn-404", 15)]
    [InlineData("cn-408", 15)]
    [InlineData("cn-705", 15)]
    [InlineData("cn-706", 15)]
    public void PeerMovementBodyUsesItsOwnNativeReader(string build, int length)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "MovementReport",
            new Dictionary<string, object?> { ["unknown_4"] = 1000000 });
        Assert.Equal(length, body.Length);
        Assert.Equal("40420F00", Convert.ToHexString(body.AsSpan(0, 4)));
        Assert.Equal(1000000, PacketCodec.Read(TestBuilds.Packets, build, "MovementReport", body)["unknown_4"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "MovementReport", body[..^1]));
    }

    [Theory]
    [InlineData("cn-404", 32923)]
    [InlineData("cn-408", 32923)]
    [InlineData("cn-705", 32922)]
    [InlineData("cn-706", 32922)]
    public void OptimizedPeerMovementUsesNativeOpcodeAndEveryMaskSelectedWidth(string build, ushort opcode)
    {
        var ops = TestBuilds.Opcodes(build);
        var wire = new UdpPeerWire(TestBuilds.Packets, build, build + "/game", ops);
        Assert.Equal(opcode, ops.ForConnection(OpcodeConnection.Relay).C2S("gppWanderLeanOptimize"));
        Assert.True(wire.IsPeerMovement(opcode));
        Assert.True(wire.IsPeerMovement(32885));
        Assert.False(wire.IsPeerMovement(32900));
        var fields = new Dictionary<string, object?>
        {
            ["unknown_4"] = 1000000, ["unknown_8"] = 0x1234, ["mask"] = 0x3F,
            ["unknown_18"] = 0x2345, ["unknown_20"] = 0x3456, ["unknown_22"] = 0x4567,
            ["unknown_24"] = 0x56, ["unknown_25"] = 0x67, ["unknown_26"] = 0x78,
        };
        var body = PacketCodec.Write(TestBuilds.Packets, build, "MovementSample", fields);
        Assert.Equal("40420F0034123F452356346745566778", Convert.ToHexString(body));
        Assert.Equal(16, body.Length);
        for (var mask = 0; mask < 64; mask++)
        {
            var sample = new Dictionary<string, object?>
            { ["unknown_4"] = 1000000, ["unknown_8"] = 0x1234, ["mask"] = mask };
            var selected = new[] { "unknown_18", "unknown_20", "unknown_22", "unknown_24", "unknown_25", "unknown_26" };
            for (var bit = 0; bit < selected.Length; bit++)
                if ((mask & (1 << bit)) != 0) sample[selected[bit]] = fields[selected[bit]];
            body = PacketCodec.Write(TestBuilds.Packets, build, "MovementSample", sample);
            var expected = 7 + 2 * System.Numerics.BitOperations.PopCount((uint)(mask & 7))
                + System.Numerics.BitOperations.PopCount((uint)(mask & 0x38));
            Assert.Equal(expected, body.Length);
            Assert.Equal(mask, Convert.ToInt32(PacketCodec.Read(TestBuilds.Packets, build, "MovementSample", body)["mask"]));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "MovementSample", body[..^1]));
        }
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public ClientProfile Profile => profile;
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Code, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort code, ReadOnlyMemory<byte> body, CancellationToken ct)
        { Sent.Add((code, body.ToArray())); return ValueTask.CompletedTask; }
    }
}
