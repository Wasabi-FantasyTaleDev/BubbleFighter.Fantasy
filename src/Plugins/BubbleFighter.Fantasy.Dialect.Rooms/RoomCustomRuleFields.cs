using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Projects committed custom options onto the selected build's native game-setting vector.</summary>
internal sealed class RoomCustomRuleFields(string target, string key, string values)
{
    internal static RoomCustomRuleFields Load(PacketTables tables, string build)
    {
        if (!tables.CapabilityOf(build, "room_custom_rules")
            || !tables.ValuesOf(build).TryGetValue("room_custom_rule_fields", out var raw)
            || raw is not Dictionary<string, object?> fields || fields.Count != 3
            || !fields.TryGetValue("target", out var targetValue) || targetValue is not string target
            || !fields.TryGetValue("key", out var keyValue) || keyValue is not string key
            || !fields.TryGetValue("values", out var valuesValue) || valuesValue is not string values)
            throw new ConfigException($"Build '{build}' requires an audited room_custom_rule_fields projection.");
        PacketLayoutContract.RequireFields(tables, build, "GameSetting", "Custom room rules",
            (target, "list<GameSettingCountedRow>"));
        PacketLayoutContract.RequireSignature(tables, build, "GameSettingCountedRow", "Custom room rules",
            $"{key}:u16,{values}:list<u32>");
        return new(target, key, values);
    }

    internal void Apply(IDictionary<string, object?> setting, IReadOnlyList<RoomCustomRuleValue> rules) =>
        setting[target] = rules.Select(rule => (object)new Dictionary<string, object?>
        {
            [key] = rule.ValueId,
            [values] = rule.Values.Select(value => (object)value).ToArray(),
        }).ToArray();
}
