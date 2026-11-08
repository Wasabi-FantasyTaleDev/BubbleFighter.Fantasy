using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins audited mission winner widths and the presence of the native broadcast timer flag.</summary>
public sealed class BombNativeWidthTests
{
    /// <summary>The complete native bodies keep operator references intact across winner byte and word layouts.</summary>
    [Theory]
    [InlineData("kr-812", 12, 24)]
    [InlineData("cn-717", 12, 24)]
    [InlineData("id-400", 12, 24)]
    [InlineData("cn-404", 12, 24)]
    [InlineData("cn-408", 12, 24)]
    [InlineData("cn-706", 12, 24)]
    [InlineData("id-1809", 12, 24)]
    [InlineData("kr-1256", 9, 20)]
    [InlineData("cn-774", 9, 20)]
    [InlineData("tw-454", 9, 24)]
    public void NativeMissionAndBroadcastWidths(string build, int missionLength, int broadcastLength)
    {
        var tables = TestBuilds.Packets;
        BombModeBinding.RequireLayouts(tables, build);
        var mission = PacketCodec.Write(tables, build, "gcpBombMissionOver", new Dictionary<string, object?>
        {
            ["winning_team"] = 2, ["planter"] = 0x12345678, ["defuser"] = -1,
        });
        Assert.Equal(missionLength, mission.Length);
        var expectedMission = missionLength == 9 ? "0278563412FFFFFFFF" : "0200000078563412FFFFFFFF";
        Assert.Equal(expectedMission, Convert.ToHexString(mission));
        Assert.Equal(new RoundBombMissionOverReported(2, 0x12345678, -1),
            BombModeBinding.ReadMissionOver(tables, build, mission));
        Assert.Throws<PacketFormatException>(() => BombModeBinding.ReadMissionOver(tables, build, mission[..^1]));
        Assert.Throws<PacketFormatException>(() => BombModeBinding.ReadMissionOver(tables, build, [.. mission, 0]));

        var change = new RoundBombStateChanged(RoundBombState.Countdown, 0x12345678, 1, 2, 3) { TimerAligned = true };
        var broadcast = BombModeBinding.WriteState(tables, build, change);
        Assert.Equal(broadcastLength, broadcast.Length);
        Assert.Equal("78563412020000000000803F0000004000004040" + (broadcastLength == 24 ? "01000000" : ""),
            Convert.ToHexString(broadcast));
        var state = PacketCodec.Write(tables, build, "gcpBombState", new Dictionary<string, object?>
        {
            ["operator"] = change.Operator, ["state"] = (int)change.State,
            ["position_x"] = change.PositionX, ["position_y"] = change.PositionY, ["position_z"] = change.PositionZ,
        });
        Assert.Equal(20, state.Length);
        Assert.Equal(new RoundBombStateReported(change.State, change.Operator, 1, 2, 3),
            BombModeBinding.ReadState(tables, build, state));
    }
}
