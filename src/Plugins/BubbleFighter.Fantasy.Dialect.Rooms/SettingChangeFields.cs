using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Resolves inherited request aliases while keeping unproven fields named by native offset.</summary>
internal sealed record SettingChangeFields(string ThirdString, string FirstFlag, string SecondFlag)
{
    internal static SettingChangeFields Require(PacketTables tables, string build)
    {
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("room_setting_change_fields", out var raw)
            || raw is not Dictionary<string, object?> aliases)
            throw new ConfigException($"{build} requires room_setting_change_fields in packets/builds.yaml.");
        string[] keys = ["third_string", "first_flag", "second_flag"];
        if (aliases.Count != keys.Length || aliases.Keys.Any(key => !keys.Contains(key, StringComparer.Ordinal)))
            throw new ConfigException($"{build} room_setting_change_fields requires exactly {string.Join(", ", keys)}.");
        var fields = tables.Resolve(build, "RoomGameSettingChangeRequest");
        string Read(string key, string type)
        {
            if (!aliases.TryGetValue(key, out var value) || value is not string name
                || !fields.Any(field => field.Name == name && field.Type == type))
                throw new ConfigException($"{build} room_setting_change_fields.{key} must name a {type} request field.");
            return name;
        }
        var result = new SettingChangeFields(Read("third_string", "wstr"),
            Read("first_flag", "bool"), Read("second_flag", "bool"));
        if (result.FirstFlag == result.SecondFlag)
            throw new ConfigException($"{build} room_setting_change_fields flag aliases must be distinct.");
        return result;
    }
}
