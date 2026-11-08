using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the intermediate clients' stage transition and three-word round loading controls.</summary>
public sealed class TransitionalRoundTableTests
{
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void SharedEntryWriterSetsTheNamedStateWithoutTheOldEntrySelector(string build)
    {
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, build, 0, "A", unknown4: 3);
        Assert.Equal(Convert.FromHexString("010000004100030000000000000000000000000000"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomEnterGame", body);
        Assert.Equal(3, fields["room_state"]);
    }

    [Theory]
    [InlineData("cn-735", "unknown_40", "unknown_48", "unknown_52", "unknown_56", "unknown_60")]
    [InlineData("cn-738", "unknown_36", "unknown_44", "unknown_48", "unknown_52", "unknown_56")]
    [InlineData("cn-742", "unknown_36", "unknown_44", "unknown_48", "unknown_52", "unknown_56")]
    public void StageEntryHasOneStringAndTheCompleteNativeSuffix(string build, string firstWord,
        string firstByte, string secondByte, string lastWord, string lastByte)
    {
        // cn-735 sub_9F4C00, cn-738 sub_A22B50 and cn-742 sub_975AB0 read these widths in order.
        var body = Convert.FromHexString("0100000041000300000044332211A5B588776655C5");
        Assert.Equal(21, body.Length);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomEnterGame", body);
        Assert.Equal(7, fields.Count);
        Assert.Equal("A", fields["map_key"]);
        Assert.Equal(3, fields["room_state"]);
        Assert.Equal(0x11223344, fields[firstWord]);
        Assert.Equal((byte)0xA5, fields[firstByte]);
        Assert.Equal((byte)0xB5, fields[secondByte]);
        Assert.Equal(0x55667788, fields[lastWord]);
        Assert.Equal((byte)0xC5, fields[lastByte]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "RoomEnterGame",
            fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
                "RoomEnterGame", body[..length]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
            "RoomEnterGame", [.. body, 0]));
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void LoadingCountdownAndStartRetainAllNativeControlWords(string build)
    {
        Pin("TutorialMasterSlot", "07000000", new() { ["master_slot"] = 7 });
        Pin("RoundMapLoadingComplete", "07000000", new() { ["seat"] = 7 });
        Pin("RoundEverybodyLoadingComplete", "010000000000000001000000",
            new() { ["unknown_4"] = true, ["unknown_5"] = false, ["unknown_6"] = true });
        Pin("RoundCountdown", "B80B0000", new() { ["milliseconds"] = 3000 });
        Pin("RoundStartGame", "E09304004433221188776655",
            new() { ["unknown_4"] = 300000, ["unknown_8"] = 0x11223344, ["unknown_12"] = 0x55667788 });

        void Pin(string structure, string hex, Dictionary<string, object?> values)
        {
            var body = Convert.FromHexString(hex);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, values));
            var decoded = PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, body);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure,
                decoded.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
            for (var length = 0; length < body.Length; length++)
                Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
                    structure, body[..length]));
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
                structure, [.. body, 0]));
        }
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void RoomControlsUseTheGameDirectionWithoutInventingRelayControls(string build)
    {
        var game = TestBuilds.Opcodes(build);
        foreach (var (name, opcode) in new (string, ushort)[] {
            ("gspRoomSetSession", 501), ("gspRoomSetMasterSlotIdx", 502), ("gspRoomSetSlot", 503),
            ("gspRoomChangeGameSetting", 505), ("gspRoomEnterGame", 510), ("gspRoomLeaveSlot", 511),
            ("gspGameMapLoadingComplete", 701),
            ("gspGameEverybodyLoadingComplete", 702), ("gspGameCountDown", 703), ("gspGameStartGame", 704),
            ("gspGameClockUnbounded_", 740),
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
    public void UnlimitedClockIsEmptyAndDoesNotReuseThePeerPortNotification(string build)
    {
        var game = TestBuilds.Opcodes(build);
        Assert.Equal((ushort)740, game.S2C("gspGameClockUnbounded_"));
        Assert.Equal((ushort)609, game.S2C("gspNotifyUdpToServerReceived"));
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, "RoundClockUnbounded", new Dictionary<string, object?>()));
        Assert.Empty(PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoundClockUnbounded", []));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build,
            "RoundClockUnbounded", [0]));
    }
}
