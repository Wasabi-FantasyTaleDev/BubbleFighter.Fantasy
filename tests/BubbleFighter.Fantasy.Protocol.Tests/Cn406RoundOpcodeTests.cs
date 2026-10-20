using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Pins reviewed CN2013 round routes separately from conflicting catalog candidates.</summary>
public sealed class Cn406RoundOpcodeTests
{
    [Theory]
    [InlineData(PacketDirection.ClientToServer, 503, "gcpRoomReqEnterGame")]
    [InlineData(PacketDirection.ClientToServer, 506, "gcpRoomReqLeaveGame")]
    [InlineData(PacketDirection.ClientToServer, 701, "gcpGameReportMapLoadingComplete")]
    [InlineData(PacketDirection.ClientToServer, 704, "gcpRespawn")]
    [InlineData(PacketDirection.ClientToServer, 705, "gcpRequestRespawnIdx")]
    [InlineData(PacketDirection.ClientToServer, 713, "gcpGameReqRespawn")]
    [InlineData(PacketDirection.ClientToServer, 724, "gcpReqEndStage")]
    [InlineData(PacketDirection.ServerToClient, 510, "gspRoomEnterGame")]
    [InlineData(PacketDirection.ServerToClient, 701, "gspGameMapLoadingComplete")]
    [InlineData(PacketDirection.ServerToClient, 702, "gspGameEverybodyLoadingComplete")]
    [InlineData(PacketDirection.ServerToClient, 703, "gspGameCountDown")]
    [InlineData(PacketDirection.ServerToClient, 704, "gspGameStartGame")]
    [InlineData(PacketDirection.ServerToClient, 705, "gspGameEndGame")]
    [InlineData(PacketDirection.ServerToClient, 707, "gspRespawn")]
    [InlineData(PacketDirection.ServerToClient, 708, "gspRequestRespawnIdx")]
    [InlineData(PacketDirection.ServerToClient, 729, "gspGameBroadcastPlayerScoreData")]
    [InlineData(PacketDirection.ServerToClient, 732, "gspGameTimeOverEnd")]
    [InlineData(PacketDirection.ServerToClient, 730, "gspGameRespawnCharacter")]
    [InlineData(PacketDirection.ServerToClient, 733, "gspGameShowResult_")]
    public void NativeSenderOrDispatcherProvesEachRoundAssignment(PacketDirection direction, int value, string name)
    {
        var table = LoadTable();
        Assert.Equal((ushort)value, table.Find(direction, name));
        Assert.True(table.TryGet(direction, (ushort)value, out var entry));
        Assert.Equal(Evidence.Verified, entry!.Evidence);
    }

    [Fact]
    public void NativeScoreboardCannotResolveToTheCatalogsConflictingItemNumber()
    {
        // cn-406 sub_708710 constructs the scoreboard at 729;
        // cn-406 sub_435110 constructs the item packet at 711.
        var table = LoadTable();
        Assert.Equal((ushort)729, table.S2C("gspGameBroadcastPlayerScoreData"));
        Assert.Equal((ushort)711, table.S2C("gspGameCreateGameItem"));
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 711, out var item));
        Assert.Equal("gspGameCreateGameItem", item!.Name);
        Assert.Equal(Evidence.Verified, item.Evidence);
    }

    private static IOpcodeTable LoadTable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "profiles", "cn-406", "opcodes.yaml");
            if (File.Exists(path))
                return OpcodeTableLoader.Load(path, new ClientProfile("cn-406", "cn", 406, 0, 0, "cn-406"));
        }
        throw new InvalidOperationException("The repository CN2013 opcode table was not found.");
    }
}
