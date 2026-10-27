using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>
/// A build's <c>room_unchosen_map</c> value: the <c>GameSetting</c> fields a room shows until its master picks a
/// map, because the create request carries the client's startup map rather than a choice. Each entry names a
/// field of the build's <c>GameSetting</c> and the value it is shown with; a field not named keeps the creator's
/// bytes. A null entry cancels an inherited field when a build restates the layout. The all-random test is the client's own comparison: kr-1256 <c>sub_96BA90</c> compares the three
/// round-map keys (the <c>GameSetting</c> strings at +144/+192/+240, held at room session +284/+332/+380) with
/// the client's Random literal kr-1256 <c>sub_1135A50</c> and shows the all-random picture on a match, and
/// kr-1256 <c>sub_9EB6C0</c> makes the same test on the start block's first string
/// (packets/builds.yaml <c>room_unchosen_map</c>). A build may state <c>room_unchosen_difficulty</c> as well:
/// in that mode every field shown as the all-random entry shows the mode's own marker instead — the pirate
/// battle's first difficulty, whose mode-101 create carries the marker in its round keys and whose host start
/// resolves it back to one of that difficulty's missions (kr-812 <c>sub_415D30</c>/<c>sub_415F30</c> from
/// kr-812 <c>sub_5C3F80</c>; packets/builds.yaml <c>room_unchosen_difficulty</c>).
/// </summary>
public sealed class RoomUnchosenMap
{
    private const string AllRandom = "Random";

    private readonly IReadOnlyDictionary<string, object> _fields;
    private readonly int _difficultyMode;
    private readonly string _difficultyKey;

    private RoomUnchosenMap(IReadOnlyDictionary<string, object> fields, int difficultyMode, string difficultyKey) =>
        (_fields, _difficultyMode, _difficultyKey) = (fields, difficultyMode, difficultyKey);

    /// <summary>Gets the field names and the values they are shown with.</summary>
    public IReadOnlyDictionary<string, object> Fields => _fields;

    /// <summary>Loads a build's value, or returns null when the build states none.</summary>
    /// <param name="tables">The packet tables the build is named in.</param>
    /// <param name="build">The selected packet lineage.</param>
    /// <returns>The checked value, or null.</returns>
    /// <exception cref="ConfigException">
    /// Thrown when the value is not a mapping, names a field the build's <c>GameSetting</c> lacks, or gives a field a
    /// value of the wrong kind (text for <c>wstr</c>, a whole number for <c>u32</c>).
    /// </exception>
    public static RoomUnchosenMap? Load(PacketTables tables, string build)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (!tables.ValuesOf(build).TryGetValue("room_unchosen_map", out var raw) || raw is null)
            return null;
        var where = $"{build} room_unchosen_map";
        if (raw is not Dictionary<string, object?> map || map.Count == 0)
            throw new ConfigException($"{where} must be a mapping of GameSetting field names to values.");
        var layout = tables.Resolve(build, "GameSetting");
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in map)
        {
            if (value is null)
                continue;
            var field = layout.FirstOrDefault(candidate => candidate.Name == name)
                ?? throw new ConfigException($"{where} names '{name}', which the build's GameSetting does not have.");
            fields[name] = field.Type switch
            {
                "wstr" => value as string ?? throw new ConfigException($"{where}.{name} must be text."),
                "u32" when value is string text && uint.TryParse(text, out var number) => checked((int)number),
                _ => throw new ConfigException($"{where}.{name} is a {field.Type} field, which this value cannot set."),
            };
        }
        var (difficultyMode, difficultyKey) = (0, "");
        if (tables.ValuesOf(build).TryGetValue("room_unchosen_difficulty", out var rawDifficulty) && rawDifficulty is not null)
        {
            if (rawDifficulty is not Dictionary<string, object?> difficulty || difficulty.Count != 2
                || difficulty.GetValueOrDefault("mode") is not string modeText || !int.TryParse(modeText, out difficultyMode)
                || difficultyMode <= 0 || difficulty.GetValueOrDefault("key") is not string { Length: > 0 } key)
                throw new ConfigException($"{build} room_unchosen_difficulty must state a positive mode and a key.");
            difficultyKey = key;
        }
        return new(fields, difficultyMode, difficultyKey);
    }

    /// <summary>Shows the unchosen map in one decoded <c>GameSetting</c>.</summary>
    /// <param name="setting">The decoded game setting; the named fields are replaced.</param>
    /// <param name="mode">The room's mode; the difficulty mode shows its own marker in place of the all-random entry.</param>
    public void Apply(IDictionary<string, object?> setting, int mode = 0)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var difficulty = _difficultyMode != 0 && mode == _difficultyMode;
        foreach (var (name, value) in _fields)
            setting[name] = difficulty && value is AllRandom ? _difficultyKey : value;
    }
}
