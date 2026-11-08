using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native entry requirements of the adjacent China 2014 clients and September controlled seats.</summary>
public sealed class Cn706RoomEntryTests
{
    /// <summary>Both adjacent clients dispatch automatic start on their independently verified identifier.</summary>
    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void AutomaticStartUsesItsNativeDispatcherIdentifier(string build)
    {
        Assert.Equal((ushort)520, TestBuilds.Opcodes(build).S2C("gspRepAutoStart"));
    }

    /// <summary>The native controller byte must name the seat before the stage creates its local actor.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void StartingSeatCarriesItsControllerAndClearsWaitingUiFlags(int seat)
    {
        // cn-706 sub_61B950 reads this prefix; cn-706 sub_4C0B80 compares the controller with the local seat.
        var schema = TestBuilds.Of("cn-706").Schema;
        var player = new MainMenuRoomOpened(42, "A", 300001, [300001, 201001]);
        var body = RoomCodec.WritePlaySlot(player, TestBuilds.Packets, "cn-706", schema, seat, 2);
        Assert.Equal(198, body.Length);
        Assert.Equal(seat, BitConverter.ToInt32(body));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, (byte)seat }, body[4..17]);
        var fields = PacketCodec.Read(TestBuilds.Packets, "cn-706", "RoomSeatUpdate", body);
        var user = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fields["user"]);
        Assert.Equal((byte)seat, user["controller_slot"]);
        Assert.Equal(42, user["user_number"]);
        Assert.Equal((byte)2, user["team"]);
        Assert.False(Assert.IsType<bool>(user["unknown_126"]));
        Assert.False(Assert.IsType<bool>(user["unknown_127"]));
    }

    /// <summary>The tutorial enters through the audited immediate stage transition, without a ready countdown.</summary>
    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void TutorialUsesImmediateEntryAndOwnAutomaticStartOpcode(string build)
    {
        Assert.Equal(TutorialMatchStart.RoomEnterGame, TestBuilds.Of(build).Play.MatchStart);
        var opcodes = TestBuilds.Opcodes(build);
        Assert.Equal((ushort)520, opcodes.S2C("gspRepAutoStart"));
        // cn-705 sub_787030 and cn-706 sub_788610 read u32, wstr, three u32 values and one bool32.
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, build, 0, "A", unknown4: 3);
        Assert.Equal(26, body.Length);
        Assert.Equal("0000000001000000410000000000000000000300000000000000", Convert.ToHexString(body));
    }
}
