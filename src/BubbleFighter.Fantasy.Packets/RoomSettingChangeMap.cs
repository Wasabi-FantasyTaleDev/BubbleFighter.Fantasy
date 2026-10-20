using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>
/// A build's <c>room_setting_change_map</c> value: where the master's map change request (C2S
/// <c>gcpRoomChangeGameSetting</c>) carries the map's display name, key, scene and the room's mode, and which
/// <c>GameSetting</c> fields the room then shows them in. A scene that starts with the mission prefix is a pirate
/// mission: the room keeps it in every map field, because the mission key is what loads the mission's stage.
/// </summary>
public sealed class RoomSettingChangeMap
{
    private static readonly string[] RequestKeys = ["name", "key", "scene", "mode"];
    private static readonly string[] TargetKeys = ["selection", "key", "current", "name"];

    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly IReadOnlyDictionary<string, string> _request;
    private readonly IReadOnlyDictionary<string, string> _targets;
    private readonly string _missionPrefix;

    private RoomSettingChangeMap(PacketTables tables, string build, IReadOnlyDictionary<string, string> request,
        IReadOnlyDictionary<string, string> targets, string missionPrefix) =>
        (_tables, _build, _request, _targets, _missionPrefix) = (tables, build, request, targets, missionPrefix);

    /// <summary>Loads a build's value, or returns null when the build states none.</summary>
    /// <param name="tables">The packet tables the build is named in.</param>
    /// <param name="build">The selected packet lineage.</param>
    /// <returns>The checked value, or null.</returns>
    /// <exception cref="ConfigException">
    /// Thrown when the value lacks a key, names a field the request or the build's <c>GameSetting</c> does not have,
    /// or names one of the wrong type.
    /// </exception>
    public static RoomSettingChangeMap? Load(PacketTables tables, string build)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (!tables.ValuesOf(build).TryGetValue("room_setting_change_map", out var raw) || raw is null)
            return null;
        var where = $"{build} room_setting_change_map";
        if (raw is not Dictionary<string, object?> map
            || map.Keys.Any(key => key is not ("request" or "targets" or "mission_prefix"))
            || map.GetValueOrDefault("request") is not Dictionary<string, object?> request
            || map.GetValueOrDefault("targets") is not Dictionary<string, object?> targets
            || map.GetValueOrDefault("mission_prefix") is not string { Length: > 0 } prefix)
            throw new ConfigException($"{where} must state request, targets and mission_prefix.");
        var requestFields = tables.Resolve(build, "RoomGameSettingChangeRequest");
        var settingFields = tables.Resolve(build, "GameSetting");
        return new(tables, build,
            Names(request, RequestKeys, requestFields, key => key == "mode" ? "u32" : "wstr", $"{where}.request"),
            Names(targets, TargetKeys, settingFields, _ => "wstr", $"{where}.targets"),
            prefix);
    }

    /// <summary>Reads one change request.</summary>
    /// <param name="body">The request body, with no trailing data.</param>
    /// <returns>The room's mode and the map the master picked.</returns>
    public RoomSettingChange Read(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(_tables, _build, "RoomGameSettingChangeRequest", body);
        return new(Convert.ToInt32(fields[_request["mode"]], System.Globalization.CultureInfo.InvariantCulture),
            (string)fields[_request["name"]], (string)fields[_request["key"]], (string)fields[_request["scene"]]);
    }

    /// <summary>Gets the key the room keeps for a change: the mission when the scene is one, else the map key.</summary>
    /// <param name="change">The change.</param>
    /// <returns>The key.</returns>
    public string RoomKey(RoomSettingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return IsMission(change) ? change.Scene : change.Key;
    }

    /// <summary>Shows one change in a decoded <c>GameSetting</c>.</summary>
    /// <param name="setting">The decoded game setting; the four target fields are replaced.</param>
    /// <param name="change">The change.</param>
    public void Apply(IDictionary<string, object?> setting, RoomSettingChange change)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(change);
        var mission = IsMission(change) ? change.Scene : null;
        setting[_targets["selection"]] = mission ?? (change.Scene.Length == 0 ? change.Key : change.Scene);
        setting[_targets["key"]] = mission ?? change.Key;
        setting[_targets["current"]] = mission ?? change.Key;
        setting[_targets["name"]] = change.Name.Length == 0 ? change.Key : change.Name;
    }

    private bool IsMission(RoomSettingChange change) =>
        change.Scene.StartsWith(_missionPrefix, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> Names(Dictionary<string, object?> map, string[] keys,
        IReadOnlyList<PacketField> fields, Func<string, string> type, string where)
    {
        if (map.Count != keys.Length || keys.Any(key => !map.ContainsKey(key)))
            throw new ConfigException($"{where} requires exactly {string.Join(", ", keys)}.");
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (map[key] is not string name || !fields.Any(field => field.Name == name && field.Type == type(key)))
                throw new ConfigException($"{where}.{key} must name a {type(key)} field.");
            names[key] = name;
        }
        return names;
    }
}

/// <summary>One map change the room master made.</summary>
/// <param name="Mode">The room's mode as the request carries it.</param>
/// <param name="Name">The map's display name.</param>
/// <param name="Key">The map key.</param>
/// <param name="Scene">The scene key; a pirate mission's key when it has the mission prefix.</param>
public sealed record RoomSettingChange(int Mode, string Name, string Key, string Scene);
