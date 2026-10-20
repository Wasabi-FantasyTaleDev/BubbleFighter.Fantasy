using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the all-random presentation of a room whose master has not picked a map.</summary>
public sealed class RoomUnchosenMapTests
{
    /// <summary>kr-1256 shows the client's Random entry in the start block and in the three round-map keys.</summary>
    [Fact]
    public void Kr1256ShowsRandomInTheStartBlockAndEveryRoundKey()
    {
        var unchosen = Assert.IsType<RoomUnchosenMap>(RoomUnchosenMap.Load(TestBuilds.Packets, "kr-1256"));
        var setting = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "GameSetting",
            PacketCodec.Write(TestBuilds.Packets, "kr-1256", "GameSetting", new Dictionary<string, object?>
            {
                ["mode"] = (ushort)38, ["unknown_44"] = "rel_Village01_Byway", ["unknown_68"] = "rel_Village01_Byway",
                ["unknown_140"] = 1,
            }));
        unchosen.Apply(setting);
        Assert.Equal(("Random", "", "", ""), ((string)setting["unknown_44"]!, (string)setting["unknown_68"]!,
            (string)setting["unknown_92"]!, (string)setting["unknown_116"]!));
        Assert.Equal(["Random", "", "Random", "", "Random", ""],
            new[] { "unknown_144", "unknown_168", "unknown_192", "unknown_216", "unknown_240", "unknown_264" }
                .Select(name => (string)setting[name]!));
        // Everything the value does not name keeps the creator's bytes.
        Assert.Equal((ushort)38, Convert.ToUInt16(setting["mode"]));
        Assert.Equal(1, Convert.ToInt32(setting["unknown_140"]));
    }

    /// <summary>A pirate room shows its first difficulty's marker wherever another room shows Random.</summary>
    [Fact]
    public void ThePirateBattleShowsItsOwnMarker()
    {
        var unchosen = Assert.IsType<RoomUnchosenMap>(RoomUnchosenMap.Load(TestBuilds.Packets, "kr-1256"));
        var setting = new Dictionary<string, object?>();
        unchosen.Apply(setting, 101);
        Assert.Equal(["Random_Hot_Dif1", "Random_Hot_Dif1", "Random_Hot_Dif1", "Random_Hot_Dif1"],
            new[] { "unknown_44", "unknown_144", "unknown_192", "unknown_240" }.Select(name => (string)setting[name]!));
        Assert.Equal("", setting["unknown_68"]);
    }

    /// <summary>Only the custom AI-war rooms set the add-computer-player bit, and they keep the creator's other bits.</summary>
    [Theory]
    [InlineData(38, 4, 5)]
    [InlineData(39, 0, 1)]
    [InlineData(1, 4, 4)]
    [InlineData(101, 0, 0)]
    public void TheAiWarRoomsLetTheMasterAddComputerPlayers(int mode, int creator, int shown)
    {
        var flag = Assert.IsType<RoomAiSeatFlag>(RoomAiSeatFlag.Load(TestBuilds.Packets, "kr-1256"));
        var setting = new Dictionary<string, object?> { ["unknown_72"] = creator };
        flag.Apply(setting, mode);
        Assert.Equal(shown, setting["unknown_72"]);
    }

    /// <summary>The map change value names the request's strings and the room's mode word.</summary>
    [Fact]
    public void TheMapChangeReadsNameKeySceneAndMode()
    {
        var map = Assert.IsType<RoomSettingChangeMap>(RoomSettingChangeMap.Load(TestBuilds.Packets, "kr-1256"));
        var change = map.Read(PacketCodec.Write(TestBuilds.Packets, "kr-1256", "RoomGameSettingChangeRequest",
            new Dictionary<string, object?> { ["unknown_0"] = "name", ["unknown_24"] = "rel_Map", ["unknown_48"] = "", ["mode"] = 1 }));
        Assert.Equal(new RoomSettingChange(1, "name", "rel_Map", ""), change);
        Assert.Equal("rel_Map", map.RoomKey(change));
        var setting = new Dictionary<string, object?>();
        map.Apply(setting, change);
        Assert.Equal(("rel_Map", "rel_Map", "rel_Map", "name"), ((string)setting["unknown_44"]!, (string)setting["unknown_68"]!,
            (string)setting["unknown_92"]!, (string)setting["unknown_116"]!));
    }

    /// <summary>The builds whose layouts differ state none of the room values.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void TheRestatedBuildsCancelTheRoomValues(string build)
    {
        Assert.Null(RoomSettingChangeMap.Load(TestBuilds.Packets, build));
        Assert.Null(RoomAiSeatFlag.Load(TestBuilds.Packets, build));
    }

    /// <summary>The legacy builds present unchosen maps through their own room writer and state no value.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void BuildsWithoutTheValueLeaveTheSettingAlone(string build) =>
        Assert.Null(RoomUnchosenMap.Load(TestBuilds.Packets, build));
}
