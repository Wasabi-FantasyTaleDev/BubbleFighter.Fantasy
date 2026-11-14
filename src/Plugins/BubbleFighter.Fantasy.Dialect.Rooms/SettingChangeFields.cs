using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Resolves inherited request aliases while keeping unproven fields named by native offset.</summary>
/// <param name="ThirdString">The request string copied into the setting's NPC-key or theme string.</param>
/// <param name="FirstFlag">The request flag copied into the setting's +4 bool.</param>
/// <param name="SecondFlag">The request flag copied into the setting's +5 bool.</param>
/// <param name="DifficultyTarget">
/// The <c>GameSetting</c> u32 that receives the request's <c>difficulty</c>: the build's <c>difficulty_target</c>
/// alias, else the setting word named <c>difficulty</c>; null when the build's request carries no difficulty.
/// </param>
internal sealed record SettingChangeFields(string ThirdString, string FirstFlag, string SecondFlag, string? DifficultyTarget)
{
    internal static SettingChangeFields Require(PacketTables tables, string build)
    {
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("room_setting_change_fields", out var raw)
            || raw is not Dictionary<string, object?> aliases)
            throw new ConfigException($"{build} requires room_setting_change_fields in packets/builds.yaml.");
        string[] keys = ["third_string", "first_flag", "second_flag"];
        if (keys.Any(key => !aliases.ContainsKey(key))
            || aliases.Keys.Any(key => !keys.Contains(key, StringComparer.Ordinal) && key != "difficulty_target"))
            throw new ConfigException($"{build} room_setting_change_fields requires exactly {string.Join(", ", keys)} and optionally difficulty_target.");
        var fields = tables.Resolve(build, "RoomGameSettingChangeRequest");
        string Read(string key, string type)
        {
            if (!aliases.TryGetValue(key, out var value) || value is not string name
                || !fields.Any(field => field.Name == name && field.Type == type))
                throw new ConfigException($"{build} room_setting_change_fields.{key} must name a {type} request field.");
            return name;
        }
        // Only a request that names its difficulty word has one to place in the setting.
        string? target = null;
        if (fields.Any(field => field.Name == "difficulty" && field.Type == "u32"))
        {
            var setting = tables.Resolve(build, "GameSetting");
            target = aliases.GetValueOrDefault("difficulty_target") as string ?? "difficulty";
            if (!setting.Any(field => field.Name == target && field.Type == "u32"))
                throw new ConfigException($"{build} needs room_setting_change_fields.difficulty_target naming the u32 GameSetting field that holds the request's difficulty.");
        }
        else if (aliases.ContainsKey("difficulty_target") && aliases["difficulty_target"] is not string)
            throw new ConfigException($"{build} room_setting_change_fields.difficulty_target must name a GameSetting field.");
        var result = new SettingChangeFields(Read("third_string", "wstr"),
            Read("first_flag", "bool"), Read("second_flag", "bool"), target);
        if (result.FirstFlag == result.SecondFlag)
            throw new ConfigException($"{build} room_setting_change_fields flag aliases must be distinct.");
        return result;
    }
}
