using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins independently audited room notifications without activating any new request route.</summary>
public sealed class RoomDetailTableTests
{
    /// <summary>The round-selection request preserves its index and two strings independently of S2C 514.</summary>
    [Theory]
    [InlineData("kr-812", "round_index", "unknown_8", "unknown_36")]
    [InlineData("kr-1256", "round_index", "unknown_4", "unknown_28")]
    [InlineData("cn-774", "round_index", "unknown_4", "unknown_28")]
    [InlineData("tw-454", "round_index", "unknown_4", "unknown_28")]
    public void RoundSettingChangePinsWordAndTwoStrings(string build, string word, string first, string second)
    {
        var body = Convert.FromHexString("7856341201000000410001000000A903");
        Assert.Equal(16, body.Length);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomRoundSettingChangeRequest", body);
        Assert.Equal(3, fields.Count);
        Assert.Equal(0x12345678, fields[word]);
        Assert.Equal("A", fields[first]);
        Assert.Equal("\u03A9", fields[second]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "RoomRoundSettingChangeRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        AssertRejectsIncompleteOrTrailing(build, "RoomRoundSettingChangeRequest", body);
    }

    /// <summary>Modern settings names resolve in the game request space without creating relay routes.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void SettingsRequestNamesHaveIndependentConnectionAndDirection(string build)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var tables = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var game = tables.ForConnection(OpcodeConnection.Game);
        var relay = tables.ForConnection(OpcodeConnection.Relay);
        Assert.Equal(513, game.C2S("gcpRoomChangeGameSetting"));
        Assert.Equal(514, game.C2S("gcpRoomChangeRoundSetting"));
        Assert.Null(relay.Find(PacketDirection.ClientToServer, "gcpRoomChangeGameSetting"));
        Assert.Null(relay.Find(PacketDirection.ClientToServer, "gcpRoomChangeRoundSetting"));
        Assert.Null(game.Find(PacketDirection.ServerToClient, "gcpRoomChangeRoundSetting"));
    }

    /// <summary>Modern dialects keep S2C 506 in their own game opcode space.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernInventoryNotificationHasItsOwnDirectionRow(string build)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var tables = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);

        Assert.Equal(506, tables.S2C("gspRoomChangeInventory"));
        Assert.Null(tables.Find(PacketDirection.ClientToServer, "gspRoomChangeInventory"));
    }

    /// <summary>Delegation uses the game request space independently of the same-number chat reply.</summary>
    [Theory]
    [InlineData("kr-812", 508)]
    [InlineData("cn-279", 507)]
    [InlineData("cn-324", 507)]
    [InlineData("cn-406", 508)]
    [InlineData("cn-404", 508)]
    [InlineData("cn-408", 508)]
    [InlineData("cn-706", 508)]
    [InlineData("cn-717", 508)]
    [InlineData("id-400", 508)]
    [InlineData("id-1809", 508)]
    [InlineData("kr-1256", 508)]
    [InlineData("cn-774", 508)]
    [InlineData("tw-454", 508)]
    public void MasterRequestPinsDirectionAndNickname(string build, int requestOpcode)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var opcodes = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        Assert.Equal(requestOpcode, opcodes.C2S("gcpRoomReqChangeMaster"));
        Assert.Equal(507, opcodes.S2C("gspRoomRepChangeMaster"));
        var expected = Convert.FromHexString("020000004100A903");
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "RoomMasterChangeRequest", expected);
        Assert.Equal("A\u03A9", fields["nickname"]);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, build, "RoomMasterChangeRequest", fields));
        AssertRejectsIncompleteOrTrailing(build, "RoomMasterChangeRequest", expected);
    }

    /// <summary>The native master-change reply keeps its string before the four-byte result.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void MasterChangeReplyPinsStringAndStatus(string build)
    {
        var expected = Convert.FromHexString("020000004100A90303000000");
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "RoomMasterChangeReply", expected);
        Assert.Equal("A\u03A9", fields["nickname"]);
        Assert.Equal(3, Convert.ToInt32(fields["result"]));
        Assert.Equal(12, expected.Length);
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, build, "RoomMasterChangeReply", fields));
        AssertRejectsIncompleteOrTrailing(build, "RoomMasterChangeReply", expected);
    }

    /// <summary>The later CN and TW lineages restore the counted tail omitted by the modern baseline.</summary>
    [Theory]
    [InlineData("kr-812", "unknown_4", "0500000078563412EFCDAB90020000001111111122222222")]
    [InlineData("kr-1256", "slot", "0500000078563412")]
    [InlineData("cn-774", "slot", "0500000078563412EFCDAB90020000001111111122222222")]
    [InlineData("tw-454", "slot", "0500000078563412EFCDAB90020000001111111122222222")]
    public void InventoryNotificationPinsEachAuditedShape(string build, string firstField, string hex)
    {
        var expected = Convert.FromHexString(hex);
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "RoomInventoryChanged", expected);
        Assert.Equal(5, Convert.ToInt32(fields[firstField]));
        Assert.Equal(0x12345678, Convert.ToInt32(fields["unknown_8"]));
        if (expected.Length == 8)
        {
            Assert.Equal(2, fields.Count);
            Assert.False(fields.ContainsKey("unknown_12"));
            Assert.False(fields.ContainsKey("unknown_16"));
        }
        else
        {
            Assert.Equal(24, expected.Length);
            Assert.Equal(4, fields.Count);
            Assert.Equal(unchecked((int)0x90ABCDEF), Convert.ToInt32(fields["unknown_12"]));
            var values = Assert.IsAssignableFrom<IEnumerable<object?>>(fields["unknown_16"]);
            Assert.Equal([0x11111111, 0x22222222], values.Select(value => Convert.ToInt32(value)));
        }
        Assert.Equal(expected, PacketCodec.Write(TestBuilds.Packets, build, "RoomInventoryChanged", fields));
        AssertRejectsIncompleteOrTrailing(build, "RoomInventoryChanged", expected);
    }

    /// <summary>A declared vector larger than the available body is rejected before its elements are allocated.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void InventoryNotificationRejectsOversizedVector(string build)
    {
        var body = Convert.FromHexString("0500000078563412EFCDAB90FFFFFFFF");
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "RoomInventoryChanged", body));
    }

    /// <summary>The shared amplifier request retains both audited trailing words for every modern build.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void AmplifierRequestPinsFourFields(string build)
    {
        var body = Convert.FromHexString("650000000100000041004433221188776655");
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomAmplifierRequest", body);

        Assert.Equal(4, fields.Count);
        Assert.Equal(101, fields["kind"]);
        Assert.Equal("A", fields["message"]);
        Assert.Equal(unchecked((int)0x11223344), Convert.ToInt32(fields["unknown_28"]));
        Assert.Equal(unchecked((int)0x55667788), Convert.ToInt32(fields["unknown_32"]));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "RoomAmplifierRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        AssertRejectsIncompleteOrTrailing(build, "RoomAmplifierRequest", body);
    }

    /// <summary>The kr-812 recruitment request keeps its message and unproven byte in wire order.</summary>
    [Fact]
    public void Kr812RecruitRequestPinsMessageAndByte()
    {
        var body = Convert.FromHexString("020000004800690007");
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "RoomRecruitRequest", body);

        Assert.Equal("Hi", fields["message"]);
        Assert.Equal((byte)7, fields["unknown_32"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, "kr-812", "RoomRecruitRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        AssertRejectsIncompleteOrTrailing("kr-812", "RoomRecruitRequest", body);
    }

    /// <summary>The kr-812 recruitment reply preserves the member identifier before its message.</summary>
    [Fact]
    public void Kr812RecruitReplyPinsMemberAndMessage()
    {
        var body = Convert.FromHexString("3412010000004100");
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "RoomRecruitReply", body);

        Assert.Equal((ushort)0x1234, fields["member_id"]);
        Assert.Equal("A", fields["message"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, "kr-812", "RoomRecruitReply",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        AssertRejectsIncompleteOrTrailing("kr-812", "RoomRecruitReply", body);
    }

    /// <summary>The kr-812 observer-team request has the audited empty body.</summary>
    [Fact]
    public void Kr812ObserverTeamChangeIsEmpty()
    {
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "RoomObserverTeamChangeRequest", []);

        Assert.Empty(fields);
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, "kr-812", "RoomObserverTeamChangeRequest",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
    }

    private static void AssertRejectsIncompleteOrTrailing(string build, string structure, byte[] body)
    {
        for (var length = 0; length < body.Length; length++)
        {
            var prefix = body[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, prefix));
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
    }
}
