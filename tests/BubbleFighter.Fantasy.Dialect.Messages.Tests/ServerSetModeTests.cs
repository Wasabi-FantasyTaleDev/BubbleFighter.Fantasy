using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>A mode the server sets on a room must reach the mode word of the settings block the clients read.</summary>
public sealed class ServerSetModeTests
{
    private static Dictionary<string, object?> Original(string build) =>
        TestBuilds.Packets.Resolve(build, "GameSetting").ToDictionary(f => f.Name, f => (object?)(f.Type switch
        {
            "wstr" => "original_" + f.Name,
            "bool" => true,
            "f32" or "f64" or "empty_list" => f.Default,
            _ => 5,
        }));

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void TheServerSetModeIsWrittenIntoTheBlocksModeWordAndNothingElseChanges(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var values = Original(build);
        values["mode"] = 4;
        var block = PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", values);

        var changed = PlazaRoomWire.ApplyGameSettingMode(schema, block, 8);

        // Reading the packet table back names the mode; every other field keeps its value.
        var read = PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", changed);
        Assert.Equal(8, Convert.ToInt32(read["mode"]));
        values["mode"] = 8;
        Assert.Equal(block.Length, changed.Length);
        Assert.Equal(PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", values), changed);
        // Applying the same mode again changes nothing.
        Assert.Equal(changed, PlazaRoomWire.ApplyGameSettingMode(schema, changed, 8));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    public void AModeAfterTheMastersChangeWinsOverTheModeThatChangeCarried(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var values = Original(build);
        values["mode"] = 4;
        var block = PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", values);
        var change = PacketCodec.Write(TestBuilds.Packets, build, "RoomGameSettingChangeRequest",
            TestBuilds.Packets.Resolve(build, "RoomGameSettingChangeRequest").ToDictionary(f => f.Name, f => (object?)(f.Type switch
            {
                "wstr" => "map_" + f.Name,
                "bool" => false,
                _ => f.Name == "mode" ? 3 : 0,
            })));

        var withChange = PlazaRoomWire.ApplyGameSettingChange(schema, TestBuilds.Packets, build, block, change);
        Assert.Equal(3, Convert.ToInt32(PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", withChange)["mode"]));

        var forced = PlazaRoomWire.ApplyGameSettingMode(schema, withChange, 8);
        Assert.Equal(8, Convert.ToInt32(PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", forced)["mode"]));
    }

    [Fact]
    public void ASchemaWithoutAModeWordRefusesTheChangeLoudly()
    {
        var schema = TestBuilds.Of("kr-812").Schema;
        schema = schema with { GameSetting = schema.GameSetting.Where(f => f.Value != RoomFieldValue.Mode).ToArray() };
        Assert.Throws<ConfigException>(() => PlazaRoomWire.ApplyGameSettingMode(schema, [], 8));
    }
}
