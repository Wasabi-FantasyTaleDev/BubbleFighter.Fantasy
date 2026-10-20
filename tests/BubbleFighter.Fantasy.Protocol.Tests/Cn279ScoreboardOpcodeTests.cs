using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Separates CN2011's native full-score route from later-build numbers and catalog attribution.</summary>
public sealed class Cn279ScoreboardOpcodeTests
{
    [Fact]
    public void FullScoreUsesTheNativeRoomDispatcherNumber()
    {
        var table = LoadTable();
        Assert.Equal((ushort)722, table.S2C("gspGameBroadcastPlayerScoreData"));
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 722, out var entry));
        Assert.Equal(Evidence.Verified, entry!.Evidence);
        // 711 is the item box creation here (cn-279 dispatcher sub_82AB20 case 0x2C7), not the later builds' score data.
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 711, out var created));
        Assert.Equal("gspGameCreateGameItem", created!.Name);
        Assert.False(table.TryGet(PacketDirection.ServerToClient, 729, out _));
        Assert.Equal((ushort)725, table.S2C("gspGameTimeOverEnd"));
        Assert.Equal((ushort)726, table.S2C("gspGameShowResult_"));
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 725, out var timeOver));
        Assert.Equal(Evidence.Verified, timeOver!.Evidence);
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 726, out var reward));
        Assert.Equal(Evidence.Verified, reward!.Evidence);
    }

    private static IOpcodeTable LoadTable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "profiles", "cn-279", "opcodes.yaml");
            if (File.Exists(path))
                return OpcodeTableLoader.Load(path, new ClientProfile("cn-279", "cn", 279, 0, 0, "cn-279"));
        }
        throw new InvalidOperationException("The repository CN2011 opcode table was not found.");
    }
}
