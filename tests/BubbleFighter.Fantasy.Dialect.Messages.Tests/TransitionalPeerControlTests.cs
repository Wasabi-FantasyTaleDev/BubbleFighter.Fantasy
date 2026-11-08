using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the intermediate clients' peer endpoint records separately from their round clock.</summary>
public sealed class TransitionalPeerControlTests
{
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void PeerControlsUseTheirNativeGameOpcodeSpace(string build)
    {
        var game = TestBuilds.Opcodes(build);
        foreach (var (name, opcode) in new (string, ushort)[]
        {
            ("gspTryHolePunching", 607), ("gspStopUdpDiscovery_", 608),
            ("gspNotifyUdpToServerReceived", 609), ("gspTableSetHolePunching", 610),
            ("gspUdpPeerTableEnd_", 611), ("gspUdpPeerPunchStart_", 612), ("gspUdpPeerPunchFinish_", 613),
        })
        {
            Assert.Equal(opcode, game.S2C(name));
            Assert.Null(game.ForConnection(OpcodeConnection.Relay).Find(PacketDirection.ServerToClient, name));
        }
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void EndpointFieldsKeepBothLocalAddressesAndDistinctPortWidths(string build)
    {
        Pin("UdpPeerDiscoveryOffer", "B01DEFCDAB89A5", new()
        {
            ["server_port"] = (ushort)7600, ["probe_identity"] = 0x89ABCDEFu, ["unknown_6"] = (byte)0xA5,
        });
        Pin("UdpPeerObservedPort", "03000000B11D4433", new()
        {
            ["slot"] = 3, ["outer_port"] = 0x33441DB1,
        });
        Pin("UdpPeerTableRow", "03000000EFCDAB8902000000C0A801230A010203B01DCB007111B11D", new()
        {
            ["slot"] = 3, ["peer_identity"] = 0x89ABCDEFu,
            ["local_ipv4"] = new object?[] { 0x2301A8C0, 0x0302010A },
            ["local_port"] = (ushort)7600, ["outer_ipv4"] = 0x117100CB, ["outer_port"] = (ushort)7601,
        });

        void Pin(string structure, string hex, Dictionary<string, object?> values)
        {
            var body = Convert.FromHexString(hex);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, values));
            var read = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure,
                read.ToDictionary(pair => pair.Key, pair => pair.Value)));
            for (var length = 0; length < body.Length; length++)
                Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, body[..length]));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
        }
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void NativeAcknowledgementAndPunchReportHaveDistinctBodies(string build)
    {
        var game = TestBuilds.Opcodes(build);
        Assert.Equal((ushort)607, game.C2S("gcpReportTableSetHolePunchingEnd"));
        Assert.Equal((ushort)603, game.C2S("gcpReportHolePunchingEnd"));
        Assert.Empty(PacketCodec.ReadScalars(TestBuilds.Packets, build, "UdpPeerTableAcknowledged", []));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
            "UdpPeerTableAcknowledged", [0]));
        var body = Convert.FromHexString("0200000001000000");
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "UdpPeerPunchReported", body);
        Assert.Equal(2, fields["occupied_seats"]);
        Assert.Equal(1, fields["connected_peers"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "UdpPeerPunchReported",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
                "UdpPeerPunchReported", body[..length]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
            "UdpPeerPunchReported", [.. body, 0]));
    }
}
