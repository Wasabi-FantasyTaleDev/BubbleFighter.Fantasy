using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native unassigned-team values without redirecting their player statistics into red-team updates.</summary>
public sealed class TeamScoreProjectionTests
{
    /// <summary>A zero team is the native no-total-update value; assigned team identifiers remain unchanged.</summary>
    [Theory]
    [InlineData("kr-812", 0)]
    [InlineData("kr-812", 1)]
    [InlineData("kr-812", 2)]
    [InlineData("cn-717", 0)]
    [InlineData("id-1809", 0)]
    [InlineData("cn-706", 0)]
    public void LiveScorePreservesTheTeamAndPlayerCounters(string build, int team)
    {
        var body = PlayRoomCodec.WriteScoreUpdate(TestBuilds.Packets, build,
            new RoundScoreUpdated(7, team, [11, 22], [new(3, 2, 3, 7, 1)]));

        Assert.Equal(25, body.Length);
        Assert.Equal(team, body[0]);
        Assert.Equal(team == 0 ? 0 : team == 1 ? 11 : 22, BitConverter.ToUInt16(body, 1));
        Assert.Equal(3, body[3]);
        Assert.Equal(255, body[4]);
        Assert.Equal(4u, BitConverter.ToUInt32(body, 5));
        Assert.Equal("1D07000003001802000A010000000000", Convert.ToHexString(body[9..]));
    }

    /// <summary>Invalid byte values fail instead of silently acquiring a real team's identity.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void LiveScoreRejectsAnUnrepresentableTeam(int team) =>
        Assert.Throws<OverflowException>(() => PlayRoomCodec.WriteScoreUpdate(TestBuilds.Packets, "kr-812",
            new RoundScoreUpdated(7, team, [11, 22], [])));

    /// <summary>Occupied legacy rows retain the same native team as shared gameplay and modern rows.</summary>
    [Theory]
    [InlineData("kr-812", 0)]
    [InlineData("kr-812", 1)]
    [InlineData("kr-812", 2)]
    [InlineData("kr-812", 15)]
    [InlineData("cn-324", 0)]
    [InlineData("cn-406", 0)]
    [InlineData("cn-706", 0)]
    [InlineData("cn-717", 0)]
    [InlineData("id-400", 0)]
    [InlineData("id-1809", 0)]
    public void ScoreRowsPreserveNativeTeamIdentifiers(string build, int team)
    {
        var row = new RoundScoreboardRow(0, "P", team, 6, new(0, 2, 3, 7, 1), null);
        var body = PlayRoomCodec.WriteScoreRowBody(TestBuilds.Packets, build, TestBuilds.Packets.Resolve(build, "ScoreRow"), row);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "ScoreRow", body);

        Assert.Equal(team, Convert.ToInt32(decoded["team"]));
        Assert.Equal(6, Convert.ToInt32(decoded["level"]));
        Assert.Equal(7, Convert.ToInt32(decoded["score"]));
    }
}
