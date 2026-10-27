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

    /// <summary>The native CN and TW map panels use their own strings without inheriting KR AI-seat flags.</summary>
    [Theory]
    [InlineData("cn-774", "unknown_52", "unknown_76", "unknown_100", "unknown_124", "unknown_148")]
    [InlineData("tw-454", "unknown_36", "unknown_60", "unknown_84", "unknown_108", "unknown_132")]
    public void NativeMapPanelsRetainAllFourStrings(string build, string selection, string key, string name, string first, string third)
    {
        var map = Assert.IsType<RoomSettingChangeMap>(RoomSettingChangeMap.Load(TestBuilds.Packets, build));
        var body = PacketCodec.Write(TestBuilds.Packets, build, "RoomGameSettingChangeRequest", new Dictionary<string, object?>
        {
            ["unknown_0"] = "native-first", ["unknown_24"] = "map name", ["unknown_48"] = "native-third",
            ["unknown_72"] = "rel_Village01", ["unknown_128"] = 1,
        });
        var change = map.Read(body);
        Assert.Equal(1, change.Mode);
        Assert.Equal("rel_Village01", map.RoomKey(change));
        var setting = new Dictionary<string, object?> { ["unknown_16"] = 42 };
        map.Apply(setting, change);
        Assert.Equal("rel_Village01", setting[selection]);
        Assert.Equal("rel_Village01", setting[key]);
        Assert.Equal("map name", setting[name]);
        Assert.Equal("native-first", setting[first]);
        Assert.Equal("native-third", setting[third]);
        Assert.Equal(42, setting["unknown_16"]);
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() => map.Read(body[..^1]));
        Assert.Null(RoomAiSeatFlag.Load(TestBuilds.Packets, build));
    }

    /// <summary>The legacy builds present unchosen maps through their own room writer and state no value.</summary>
    [Theory]
    [InlineData("kr-812")]
    public void BuildsWithoutTheValueLeaveTheSettingAlone(string build) =>
        Assert.Null(RoomUnchosenMap.Load(TestBuilds.Packets, build));

    /// <summary>Native room previews discard the startup map and keep all unselected round keys random.</summary>
    [Theory]
    [InlineData("cn-774", "unknown_52", "unknown_76", "unknown_176", "unknown_224", "unknown_272")]
    [InlineData("tw-454", "unknown_36", "unknown_60", "unknown_160", "unknown_208", "unknown_256")]
    public void NativeUnchosenMapClearsStartupResolution(string build, string selection, string resolved,
        string first, string second, string third)
    {
        var tables = TestBuilds.Packets;
        var setting = PacketCodec.Read(tables, build, "GameSetting", PacketCodec.Write(tables, build, "GameSetting",
            new Dictionary<string, object?> { [selection] = "rel_Village01_Byway", [resolved] = "rel_Village01_Byway" }));
        var originalLength = PacketCodec.Write(tables, build, "GameSetting", setting).Length;
        Assert.IsType<RoomUnchosenMap>(RoomUnchosenMap.Load(tables, build)).Apply(setting);
        var body = PacketCodec.Write(tables, build, "GameSetting", setting);
        Assert.Equal(originalLength - 2 * 2 * "rel_Village01_Byway".Length + 4 * 2 * "Random".Length, body.Length);
        var decoded = PacketCodec.Read(tables, build, "GameSetting", body);
        Assert.Equal("Random", decoded[selection]);
        Assert.Equal("", decoded[resolved]);
        Assert.All(new[] { first, second, third }, key => Assert.Equal("Random", decoded[key]));
    }
}
