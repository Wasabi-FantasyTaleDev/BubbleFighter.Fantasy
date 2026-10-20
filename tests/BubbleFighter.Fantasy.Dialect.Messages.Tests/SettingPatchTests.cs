using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks map-setting patches against the previous bytes and deliberately reordered field schemas.</summary>
public sealed class SettingPatchTests
{
    private static byte[] Request(string build)
    {
        var fields = TestBuilds.Packets.Resolve(build, "RoomGameSettingChangeRequest");
        var values = fields.ToDictionary(f => f.Name, f => (object?)(f.Type switch
        {
            "wstr" => "changed_" + f.Name,
            "bool" => true,
            _ => 77,
        }));
        values["mode"] = 101;
        values["sub_mode"] = -7;
        return PacketCodec.Write(TestBuilds.Packets, build, "RoomGameSettingChangeRequest", values);
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void NamedPatchMatchesPreviousNativeOrderAndRetainsUntouchedScalars(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var values = TestBuilds.Packets.Resolve(build, "GameSetting").ToDictionary(f => f.Name, f =>
            (object?)(f.Type switch
            {
                "wstr" => "original_" + f.Name,
                "bool" => false,
                "f32" => BitConverter.Int32BitsToSingle(unchecked((int)0x7FA12345)),
                "f64" => BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)),
                "empty_list" => null,
                _ => f.Default,
            }));
        var original = PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", values);
        var change = Request(build);
        var expected = SettingPatchGolden.ApplyGameSettingChange(schema, TestBuilds.Packets, build, original, change);
        var actual = PlazaRoomWire.ApplyGameSettingChange(schema, TestBuilds.Packets, build, original, change);
        Assert.Equal(expected, actual);
        Assert.Equal(actual, PlazaRoomWire.ApplyGameSettingChange(schema, TestBuilds.Packets, build, actual, change));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ApplyGameSettingChange(
            schema, TestBuilds.Packets, build, [.. original, 0], change));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void FieldNamesSurviveReorderedWordsStringsAndFlags(string build)
    {
        var originalSchema = TestBuilds.Of(build).Schema;
        var fields = originalSchema.GameSetting.Reverse().ToArray();
        var schema = originalSchema with { GameSetting = fields };
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        foreach (var f in fields)
            WriteDefault(writer, f);
        var body = PlazaRoomWire.ApplyGameSettingChange(schema, TestBuilds.Packets, build,
            buffer.WrittenSpan, Request(build));
        var reader = new PacketReader(body);
        foreach (var f in fields)
        {
            if (f.Type == RoomFieldType.WStr)
            {
                var value = reader.ReadWideStringPrefixed();
                var expected = f.Value switch
                {
                    RoomFieldValue.MapDirectory => "changed_map_directory",
                    RoomFieldValue.CurrentMap => "changed_current_map",
                    RoomFieldValue.MapName => "changed_map_key",
                    RoomFieldValue.NpcTableKey or RoomFieldValue.MapTheme => "changed_" +
                        TestBuilds.Packets.Resolve(build, "RoomGameSettingChangeRequest").First(x =>
                            x.Name is "unknown_60" or "unknown_12").Name,
                    _ => "",
                };
                Assert.Equal(expected, value);
            }
            else if (f.Type == RoomFieldType.Bool)
                Assert.Equal(f.Name is "unknown_4" or "unknown_5", reader.ReadBool32());
            else if (f.Type == RoomFieldType.U32)
                Assert.Equal(f.Name == "mode" ? 101 : f.Name == "unknown_12" ? -7 : 0, reader.ReadInt32());
            else
                Skip(ref reader, f);
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void MenuOnlyUnknownLayoutCannotReceiveAnOptimisticMapPatch()
    {
        var schema = TestBuilds.Of("cn-735").Schema;
        Assert.Throws<ConfigException>(() => PlazaRoomWire.ApplyGameSettingChange(
            schema, TestBuilds.Packets, "cn-735", default, Request("cn-735")));
        Assert.NotEmpty(PlazaRoomWire.ApplyGameSettingChange(
            schema, TestBuilds.Packets, "cn-735", default, default));
    }

    private static void WriteDefault(PacketWriter w, RoomField f)
    {
        switch (f.Type)
        {
            case RoomFieldType.WStr: w.WriteWideStringPrefixed(""); break;
            case RoomFieldType.Bool: w.WriteBool32(false); break;
            case RoomFieldType.U8: w.WriteByte(0); break;
            case RoomFieldType.U16: w.WriteUInt16(0); break;
            case RoomFieldType.F64: w.WriteDouble(0); break;
            case RoomFieldType.F32: w.WriteSingle(0); break;
            default: w.WriteInt32(0); break;
        }
    }

    private static void Skip(ref PacketReader r, RoomField f)
    {
        if (f.Type == RoomFieldType.U8) r.ReadByte();
        else if (f.Type == RoomFieldType.U16) r.ReadUInt16();
        else if (f.Type == RoomFieldType.F64) r.ReadDouble();
        else r.ReadInt32();
    }
}
