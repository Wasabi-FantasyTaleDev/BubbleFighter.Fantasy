using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using System.Net;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native relay bodies independently of whether a build mounts a relay connection.</summary>
public sealed class RelayConnectionTableTests
{
    private const string FullKey = "4433221188776655CCBBAA99EEDD5713E0AC6824";
    private const string EarlyKey = "4433221188776655CCBBAA99";
    private const string MiddleKey = "4433221188776655CCBBAA99EEDDE0AC6824";

    private static Dictionary<string, object?> KeyValues() => new()
    {
        ["unknown_4"] = 0x11223344,
        ["unknown_8"] = 0x55667788,
        ["unknown_12"] = 0x99AABBCCu,
        ["unknown_16"] = 0xDDEE,
        ["unknown_18"] = 0x1357,
        ["unknown_20"] = 0x2468ACE0,
    };

    /// <summary>Provides literal native key bytes and join tails for every independently audited build.</summary>
    public static TheoryData<string, string, string> Bodies => new()
    {
        { "kr-812", FullKey, "6A40302010" },
        { "cn-717", FullKey, "6A40302010" },
        { "cn-324", MiddleKey, "6A000000" },
        { "cn-279", EarlyKey, "40302010" },
        { "cn-406", FullKey, "6A000000" },
        { "id-400", FullKey, "6A000000" },
        { "id-1809", FullKey, "6A40302010" },
        { "cn-706", FullKey, "6A40302010" },
    };

    /// <summary>Pins keys, offers and joins against each build's independent native field sequence.</summary>
    [Theory, MemberData(nameof(Bodies))]
    public void ConnectionBodiesMatchNativeBytes(string build, string keyHex, string tailHex)
    {
        var bytes = PacketCodec.Write(TestBuilds.Packets, build, "RelaySessionKey", KeyValues());
        Assert.Equal(Convert.FromHexString(keyHex), bytes);
        var decoded = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RelaySessionKey", bytes);
        Assert.Equal(unchecked((int)0x99AABBCCu), decoded["unknown_12"]);
        Assert.Equal(keyHex.Length / 2, bytes.Length);
        OfferMatchesNativeBytes(build, keyHex);
        JoinMatchesNativeBytes(build, keyHex, tailHex);
    }

    private static void OfferMatchesNativeBytes(string build, string keyHex)
    {
        var bytes = PacketCodec.Write(TestBuilds.Packets, build, "RelayConnectionOffer", new Dictionary<string, object?>
        {
            ["endpoint"] = new Dictionary<string, object?> { ["ipv4_word"] = 0x04030201, ["port"] = 0x1BD1 },
            ["key"] = KeyValues(),
        });
        Assert.Equal(Convert.FromHexString("01020304D11B" + keyHex), bytes);
        Assert.Equal(6 + keyHex.Length / 2, bytes.Length);
    }

    private static void JoinMatchesNativeBytes(string build, string keyHex, string tailHex)
    {
        var bytes = PacketCodec.Write(TestBuilds.Packets, build, "RelayJoinRequest", new Dictionary<string, object?>
        {
            ["key"] = KeyValues(),
            ["unknown_28"] = 0x6A,
            ["unknown_32"] = 0x10203040,
            ["unknown_20"] = 0x10203040,
        });
        Assert.Equal(Convert.FromHexString(keyHex + tailHex), bytes);
        Assert.Equal((keyHex.Length + tailHex.Length) / 2, bytes.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "RelayJoinRequest", bytes);
        var key = Assert.IsType<Dictionary<string, object?>>(decoded["key"]);
        Assert.Equal(0x11223344, key["unknown_4"]);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "RelayJoinRequest", decoded));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "RelayJoinRequest", bytes[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "RelayJoinRequest", [.. bytes, 0]));
    }

    [Theory, MemberData(nameof(Bodies))]
    public void OpaqueJoinTicketRoundTripsIntoTheNativeOfferWithoutModernIdentity(string build, string keyHex, string tailHex)
    {
        var bytes = Convert.FromHexString(keyHex + tailHex);
        var key = RelayConnectionWire.ReadJoinTicket(TestBuilds.Packets, build, bytes);
        Assert.Equal(Convert.FromHexString(keyHex), key);
        // Each key shape retains its own widths; no conversion to a modern 20-byte account-bearing request.
        Assert.Equal(Convert.FromHexString("01020304D11B" + keyHex),
            RelayConnectionWire.WriteOffer(TestBuilds.Packets, build, IPAddress.Parse("1.2.3.4"), 7121, key));
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<PacketFormatException>(() => RelayConnectionWire.ReadJoinTicket(
                TestBuilds.Packets, build, bytes[..length]));
        Assert.Throws<PacketFormatException>(() => RelayConnectionWire.ReadJoinTicket(
            TestBuilds.Packets, build, [.. bytes, 0]));
        Assert.Throws<PacketFormatException>(() => RelayConnectionWire.ReadJoinTicket(
            TestBuilds.Packets, build, bytes, new PacketReadLimits(MaxDepth: 1)));
        Assert.Throws<PacketFormatException>(() => RelayConnectionWire.WriteOffer(
            TestBuilds.Packets, build, IPAddress.Loopback, 7121, key[..^1]));
        Assert.Throws<PacketFormatException>(() => RelayConnectionWire.WriteOffer(
            TestBuilds.Packets, build, IPAddress.Loopback, 7121, [.. key, 0]));
    }

    [Fact]
    public void NativeOfferRejectsAnUnrepresentableEndpoint()
    {
        var key = Convert.FromHexString(FullKey);
        Assert.Throws<ArgumentException>(() => RelayConnectionWire.WriteOffer(
            TestBuilds.Packets, "kr-812", IPAddress.IPv6Loopback, 7121, key));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelayConnectionWire.WriteOffer(
            TestBuilds.Packets, "kr-812", IPAddress.Loopback, 0, key));
    }

    /// <summary>Retains ID2014's additional bool32 without leaking it into its newer descendants.</summary>
    [Theory]
    [InlineData("kr-812", "01000000")]
    [InlineData("cn-717", "01000000")]
    [InlineData("cn-324", "01000000")]
    [InlineData("cn-279", "01000000")]
    [InlineData("cn-406", "01000000")]
    [InlineData("id-400", "0100000001000000")]
    [InlineData("id-1809", "01000000")]
    [InlineData("cn-706", "01000000")]
    public void ConnectionReportMatchesNativeBytesAndRejectsWrongLengths(string build, string expectedHex)
    {
        var tables = TestBuilds.Packets;
        var bytes = PacketCodec.Write(tables, build, "RelayConnectionReport", new Dictionary<string, object?>
        {
            ["unknown_4"] = true,
            ["unknown_5"] = true,
        });
        Assert.Equal(Convert.FromHexString(expectedHex), bytes);
        var decoded = PacketCodec.ReadScalars(tables, build, "RelayConnectionReport", bytes);
        Assert.True((bool)decoded["unknown_4"]);
        Assert.Equal(build == "id-400", decoded.ContainsKey("unknown_5"));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build,
            "RelayConnectionReport", bytes[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build,
            "RelayConnectionReport", [.. bytes, 0]));
    }
}
