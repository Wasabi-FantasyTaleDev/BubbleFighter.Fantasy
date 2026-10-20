using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks inherited aliases independently of scalar order and rejects ambiguous native mappings.</summary>
public sealed class SettingChangeAliasTests
{
    [Theory]
    [InlineData("kr-812", "unknown_60", "unknown_116", "unknown_117")]
    [InlineData("cn-717", "unknown_60", "unknown_116", "unknown_117")]
    [InlineData("cn-324", "unknown_12", "unknown_20", "unknown_21")]
    [InlineData("cn-279", "unknown_12", "unknown_20", "unknown_21")]
    [InlineData("id-400", "unknown_60", "unknown_116", "unknown_117")]
    [InlineData("id-1809", "unknown_60", "unknown_116", "unknown_117")]
    [InlineData("cn-706", "unknown_60", "unknown_116", "unknown_117")]
    public void BuildAliasesMatchNativeOffsetNames(string build, string third, string first, string second) =>
        Assert.Equal(new SettingChangeFields(third, first, second),
            SettingChangeFields.Require(TestBuilds.Packets, build));

    [Fact]
    public void ReorderedScalarsUseAliasesInsteadOfTypeOccurrence()
    {
        WithTables("third_string: unknown_60\n      first_flag: unknown_116\n      second_flag: unknown_117", tables =>
        {
            var values = new Dictionary<string, object?>
            {
                ["unknown_extra_string"] = "decoy", ["unknown_extra_flag"] = true,
                ["unknown_117"] = true, ["unknown_60"] = "third", ["unknown_116"] = false,
                ["map_key"] = "key", ["map_directory"] = "display", ["current_map"] = "directory",
                ["mode"] = 101, ["sub_mode"] = -7,
            };
            var body = PacketCodec.Write(tables, "child", "RoomGameSettingChangeRequest", values);
            Assert.Equal(new GameSettingChange("display", "directory", "third", "key", false, true, 101, -7),
                PlazaRoomWire.ReadChangeGameSetting(tables, "child", body));
        });
    }

    [Theory]
    [InlineData("third_string: missing\n      first_flag: unknown_116\n      second_flag: unknown_117")]
    [InlineData("third_string: unknown_116\n      first_flag: unknown_116\n      second_flag: unknown_117")]
    [InlineData("third_string: unknown_60\n      first_flag: unknown_116\n      second_flag: unknown_116")]
    [InlineData("third_string: unknown_60\n      first_flag: unknown_116")]
    [InlineData("third_string: unknown_60\n      first_flag: unknown_116\n      second_flag: unknown_117\n      typo: ignored")]
    public void MissingWrongTypedDuplicateOrUnknownAliasesFail(string aliases) =>
        WithTables(aliases, tables => Assert.Throws<ConfigException>(() => SettingChangeFields.Require(tables, "child")));

    private static void WithTables(string aliases, Action<PacketTables> assertion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-setting-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "builds.yaml"),
                "version: 1\nbuilds:\n  root:\n    room_setting_change_fields:\n      " + aliases + "\n  child:\n    base: root\n");
            File.WriteAllText(Path.Combine(directory, "request.yaml"), """
                version: 1
                structs:
                  RoomGameSettingChangeRequest:
                    - unknown_extra_string: wstr
                    - unknown_extra_flag: bool
                    - unknown_117: bool
                    - map_key: wstr
                    - unknown_60: wstr
                    - sub_mode: u32
                    - unknown_116: bool
                    - current_map: wstr
                    - mode: u32
                    - map_directory: wstr
                """);
            assertion(PacketTables.Load(directory));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
