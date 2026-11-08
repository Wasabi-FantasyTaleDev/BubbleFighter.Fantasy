using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class MatchActionRelayTests
{
    /// <summary>The mounted dialect and InGame row carry a real race through loading, checkpoint echo, goals and result.</summary>
    [Fact]
    public async Task MountedExtremeRacingExchangeReachesTheSharedScoreboard()
    {
        using var h = await Harness.CreateAsync(extremeRacing: true);
        var table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
        Assert.Equal(RoomPhase.Playing, h.Room.Phase);
        var timer = table.S2C("gspExtremeRacingTimer_");
        Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == timer);
        var checkpoint = table.ForConnection(OpcodeConnection.Relay).C2S("gppExtremeRacingCheckPoint_");
        var body = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(body, RoomRegistry.SceneEntity(h.HostSeat.Slot));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1);
        await h.DispatchAsync(checkpoint, body);
        Assert.Equal(body, Assert.Single(h.Host.SentSnapshot, packet => packet.Opcode == checkpoint).Body);
        Assert.Equal(body, Assert.Single(h.Guest.SentSnapshot, packet => packet.Opcode == checkpoint).Body);

        var goal = table.C2S("gcpExtremeRacingGoal_");
        await h.DispatchAsync(goal, BitConverter.GetBytes(1), h.HostGame, "kr-1256/game");
        await h.DispatchAsync(goal, BitConverter.GetBytes(1), h.GuestGame, "kr-1256/game");
        var records = table.S2C("gspExtremeRacingUpdatePlayRecord_");
        Assert.Single(h.GuestGame.SentSnapshot, packet => packet.Opcode == records);
        await h.Room.ExtremeRacingClock!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomPhase.Result, h.Room.Phase);
        var scoreboard = table.S2C("Unknown614");
        for (var attempt = 0; attempt < 500 && (!h.HostGame.SentSnapshot.Any(packet => packet.Opcode == scoreboard)
            || !h.GuestGame.SentSnapshot.Any(packet => packet.Opcode == scoreboard)); attempt++)
            await Task.Delay(10);
        Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == scoreboard);
        Assert.Single(h.GuestGame.SentSnapshot, packet => packet.Opcode == scoreboard);
        await h.DispatchAsync(goal, BitConverter.GetBytes(1), h.HostGame, "kr-1256/game");
        Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == records);
        Assert.Equal(h.HostSeat.Team, h.Room.DecidedWinnerTeam);
    }
}
