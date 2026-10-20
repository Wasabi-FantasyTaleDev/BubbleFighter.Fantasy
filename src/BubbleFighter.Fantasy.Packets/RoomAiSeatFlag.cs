using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>
/// A build's <c>room_ai_seat</c> value: the <c>SessionSetting</c> word whose lowest bit lets the room master add
/// computer players to empty seats, and the modes whose rooms set it. Every other mode keeps the creator's word.
/// </summary>
public sealed class RoomAiSeatFlag
{
    private readonly string _field;
    private readonly HashSet<int> _modes;

    private RoomAiSeatFlag(string field, HashSet<int> modes) => (_field, _modes) = (field, modes);

    /// <summary>Loads a build's value, or returns null when the build states none.</summary>
    /// <param name="tables">The packet tables the build is named in.</param>
    /// <param name="build">The selected packet lineage.</param>
    /// <returns>The checked value, or null.</returns>
    /// <exception cref="ConfigException">
    /// Thrown when the field is not a <c>u32</c> of the build's <c>SessionSetting</c> or the modes are not positive
    /// whole numbers.
    /// </exception>
    public static RoomAiSeatFlag? Load(PacketTables tables, string build)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (!tables.ValuesOf(build).TryGetValue("room_ai_seat", out var raw) || raw is null)
            return null;
        var where = $"{build} room_ai_seat";
        if (raw is not Dictionary<string, object?> map || map.Count != 2
            || map.GetValueOrDefault("field") is not string field
            || map.GetValueOrDefault("modes") is not string[] { Length: > 0 } modes)
            throw new ConfigException($"{where} must state field and a list of modes.");
        if (!tables.Resolve(build, "SessionSetting").Any(candidate => candidate.Name == field && candidate.Type == "u32"))
            throw new ConfigException($"{where}.field must name a u32 SessionSetting field.");
        var ids = new HashSet<int>();
        foreach (var mode in modes)
        {
            if (!int.TryParse(mode, out var id) || id <= 0 || !ids.Add(id))
                throw new ConfigException($"{where}.modes must list distinct positive mode ids.");
        }
        return new(field, ids);
    }

    /// <summary>Sets the flag in one decoded <c>SessionSetting</c> when the room's mode is one of the value's modes.</summary>
    /// <param name="setting">The decoded session setting.</param>
    /// <param name="mode">The room's mode.</param>
    public void Apply(IDictionary<string, object?> setting, int mode)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (_modes.Contains(mode))
            setting[_field] = Convert.ToInt32(setting[_field], System.Globalization.CultureInfo.InvariantCulture) | 1;
    }
}
