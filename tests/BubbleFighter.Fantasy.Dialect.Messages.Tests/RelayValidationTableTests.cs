using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native relay validation trees and the earlier direct-success capability boundary.</summary>
public sealed class RelayValidationTableTests
{
    private const string Prefix = "4433221101000000410088776655";
    private const string Equipment = "01000000020000000300000002000000040000000500000002000000AABB"
        + "0100000006000000070000000200000008000000090000000A000000010000004100"
        + "010000000B000000000000000000F83F";
    private const string Presets = "01000000010000000400000001000000010100000002";
    private const string LaterTail = "0100000042000304000500060007000802000000090A";
    private const string LaterBody = Prefix + Equipment + Presets + LaterTail;
    private const string MiddleBody = Prefix + "01000000020000000300000002000000040000000500000002000000AABB"
        + "010000004200030400";

    /// <summary>Provides fixed wire fixtures with nonempty native nested vectors and build-specific tails.</summary>
    public static TheoryData<string, string, int> Bodies => new()
    {
        { "kr-812", LaterBody, 138 },
        { "cn-717", LaterBody + "01000000", 142 },
        { "cn-406", LaterBody, 138 },
        { "id-400", LaterBody, 138 },
        { "id-1809", LaterBody, 138 },
        { "cn-706", LaterBody, 138 },
        { "cn-324", MiddleBody, 53 },
    };

    [Theory, MemberData(nameof(Bodies))]
    public void CompleteReportKeepsEveryNativeVectorAndUnknownScalar(string build, string hex, int length)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(length, bytes.Length);
        var tables = TestBuilds.Packets;
        var decoded = RelayValidationWire.ReadReport(tables, build, bytes);
        Assert.Equal(0x11223344, decoded["entity_id"]);
        Assert.Equal("A", decoded["nickname"]);
        Assert.Equal(0x55667788, decoded[build == "cn-324" ? "unknown_12" : "unknown_36"]);
        var equipment = Assert.IsType<Dictionary<string, object?>>(decoded["equipment"]);
        Assert.Equal(1, equipment["user_number"]);
        if (build == "cn-324")
        {
            Assert.False(decoded.ContainsKey("presets"));
            Assert.False(decoded.ContainsKey("signature"));
            Assert.Equal("B", decoded["unknown_56"]);
            Assert.Equal((byte)3, decoded["unknown_60"]);
            Assert.Equal((ushort)4, decoded["unknown_62"]);
        }
        else
        {
            var signature = Assert.IsType<Dictionary<string, object?>>(decoded["signature"]);
            Assert.Equal((ushort)7, signature["unknown_4"]);
            Assert.Equal((byte)8, signature["unknown_6"]);
            Assert.Equal(new object?[] { (byte)9, (byte)10 }, signature["unknown_8"]);
            var presets = Assert.IsType<Dictionary<string, object?>>(decoded["presets"]);
            Assert.Equal(1, presets["user_number"]);
            Assert.Equal(new object?[] { 4 }, presets["item_ids"]);
            Assert.Equal(build == "cn-717", decoded.ContainsKey("unknown_236"));
        }
        Assert.Equal(bytes, PacketCodec.Write(tables, build, "RelayValidationReport", decoded));
        for (var i = 0; i < bytes.Length; i++)
            Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadReport(tables, build, bytes[..i]));
        Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadReport(tables, build, [.. bytes, 0]));
    }

    [Theory, MemberData(nameof(Bodies))]
    public void SceneProjectionUsesActorIdentityAndStillValidatesTheCompleteReport(string build, string hex, int length)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(length, bytes.Length);
        var identity = RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, bytes);
        // Equipment/preset user_number is 1; the report's actor identity is independently 0x11223344.
        Assert.Equal(0x11223344, identity.EntityId);
        Assert.Equal("A", identity.Nickname);
        for (var i = 0; i < bytes.Length; i++)
            Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, bytes[..i]));
        Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, [.. bytes, 0]));
        Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, bytes,
            new PacketReadLimits(MaxCollectionElements: 1)));
    }

    [Fact]
    public void EarlierDirectSuccessPathCannotAdmitAnEmptyReport()
    {
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-279", "relay_validation_report"));
        Assert.Throws<ConfigException>(() => RelayValidationWire.ReadReport(TestBuilds.Packets, "cn-279", []));
        Assert.Throws<ConfigException>(() => RelayValidationWire.ReadReport(TestBuilds.Packets, "cn-279",
            Convert.FromHexString(MiddleBody)));
        Assert.Throws<ConfigException>(() => RelayValidationWire.ReadIdentity(TestBuilds.Packets, "cn-279",
            Convert.FromHexString(MiddleBody)));
    }

    [Fact]
    public void NestedReportLimitsApplyBeforeIdentityProjection()
    {
        var bytes = Convert.FromHexString(LaterBody);
        foreach (var limits in new[]
        {
            new PacketReadLimits(MaxStringCharacters: 0),
            new PacketReadLimits(MaxCollectionElements: 1),
            new PacketReadLimits(MaxValues: 5),
            new PacketReadLimits(MaxDepth: 2),
        })
            Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadReport(TestBuilds.Packets, "kr-812", bytes, limits));
    }
}
