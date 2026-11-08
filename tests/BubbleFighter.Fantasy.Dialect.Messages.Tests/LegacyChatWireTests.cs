using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the waiting-room and party chat bodies every legacy build reads with the same field order: C2S
/// gcpRoomSendChatMsg (kr-812 sub_8456D0, cn-717 sub_8CFCB0), S2C gspRoomReceiveChatMsg (kr-812 sub_80AD80, cn-717
/// sub_88F450), C2S gcpSendPartyChatMsg (kr-812 sub_5C8FD0) and S2C gspReceivePartyChatMsg (kr-812 sub_92AB70), and
/// that each build's own opcode table names both halves of each pair.
/// </summary>
public sealed class LegacyChatWireTests
{
    /// <summary>The legacy builds whose readers were audited for the four chat bodies.</summary>
    public static TheoryData<string> Builds => new()
    {
        "kr-812", "cn-717", "cn-324", "cn-279", "cn-406", "cn-404", "cn-408", "cn-705", "cn-706", "id-400", "id-1809",
    };

    [Theory]
    [MemberData(nameof(Builds))]
    public void TheRoomChatReplyCarriesSlotTeamFlagTextAndUnsetColour(string build)
    {
        var body = PlayRoomCodec.WriteSessionChat(TestBuilds.Packets, build, new RoomSessionChatReceived(3, 2, true, "AB"));

        // u16 slot, bool32 team-only, empty wstr, wstr "AB", colour -1, bool32 false: 26 bytes.
        Assert.Equal("0200" + "01000000" + "00000000" + "0200000041004200" + "FFFFFFFF" + "00000000", Convert.ToHexString(body));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void TheRoomChatRequestReadsTheTeamFlagThenTheText(string build)
    {
        var request = PlayRoomCodec.ReadSessionChat(TestBuilds.Packets, build, Convert.FromHexString("000000000100000043"+"00"));

        Assert.Equal(new RoomSessionChatSent(false, "C"), request);
        Assert.Throws<PacketFormatException>(() =>
            PlayRoomCodec.ReadSessionChat(TestBuilds.Packets, build, Convert.FromHexString("00000000010000004300FF")));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void ThePartyChatBodiesKeepTheReaderOrder(string build)
    {
        var request = PacketCodec.ReadScalars(TestBuilds.Packets, build, "PartyChatRequest", Convert.FromHexString("0100000044"+"00"));
        Assert.Equal("D", request["text"]);

        var message = PacketCodec.Write(TestBuilds.Packets, build, "PartyChatMessage",
            new Dictionary<string, object?> { ["user_number"] = 77, ["text"] = "AB", ["system"] = false });

        // u32 user number, empty wstr, wstr "AB", colour -1, bool32 false: 24 bytes.
        Assert.Equal("4D000000" + "00000000" + "0200000041004200" + "FFFFFFFF" + "00000000", Convert.ToHexString(message));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void EveryAuditedBuildNamesBothHalvesOfEachChatPair(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);

        Assert.Equal((ushort)501, table.C2S("gcpRoomSendChatMsg"));
        Assert.Equal((ushort)508, table.S2C("gspRoomReceiveChatMsg"));
        Assert.Equal((ushort)304, table.C2S("gcpSendPartyChatMsg"));
        Assert.Equal((ushort)305, table.S2C("gspReceivePartyChatMsg"));
    }
}
