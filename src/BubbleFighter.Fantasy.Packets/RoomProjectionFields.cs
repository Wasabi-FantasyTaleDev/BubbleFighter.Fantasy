using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>Maps shared waiting-room values to a build's native game-setting fields and stage gates.</summary>
public sealed class RoomProjectionFields
{
    private readonly string _mode;
    private readonly string _modeType;
    private readonly string[] _maps;
    private readonly string _flags;
    private readonly uint _clearMask;
    private readonly string[] _words;
    private readonly string[] _booleans;
    private readonly Dictionary<string, HashSet<uint>> _preservedWords;

    private RoomProjectionFields(string mode, string modeType, string[] maps, string flags, uint clearMask,
        string[] words, string[] booleans, Dictionary<string, HashSet<uint>> preservedWords) =>
        (_mode, _modeType, _maps, _flags, _clearMask, _words, _booleans, _preservedWords) =
        (mode, modeType, maps, flags, clearMask, words, booleans, preservedWords);

    /// <summary>Loads the inherited aliases and rejects missing, overlapping or incorrectly typed native fields.</summary>
    /// <param name="tables">The packet layouts and build metadata.</param>
    /// <param name="build">The client build whose waiting room consumes the projection.</param>
    /// <returns>A projection checked against that build's complete game-setting layout.</returns>
    public static RoomProjectionFields Load(PacketTables tables, string build)
    {
        var where = $"{build} room_projection_fields";
        if (!tables.ValuesOf(build).TryGetValue("room_projection_fields", out var raw)
            || raw is not Dictionary<string, object?> map || map.Count is < 6 or > 7
            || map.Keys.Any(key => key is not ("mode" or "maps" or "flags" or "clear_mask"
                or "clear_words" or "clear_booleans" or "preserve_word_values"))
            || map.GetValueOrDefault("mode") is not string mode
            || map.GetValueOrDefault("maps") is not string[] { Length: > 0 } maps
            || map.GetValueOrDefault("flags") is not string flags
            || map.GetValueOrDefault("clear_mask") is not string maskText
            || !uint.TryParse(maskText, NumberStyles.None, CultureInfo.InvariantCulture, out var mask)
            || map.GetValueOrDefault("clear_words") is not string[] words
            || map.GetValueOrDefault("clear_booleans") is not string[] booleans)
            throw new ConfigException($"{where} requires mode, maps, flags, clear_mask, clear_words and clear_booleans.");
        var fields = tables.Resolve(build, "GameSetting");
        var modeType = fields.SingleOrDefault(field => field.Name == mode)?.Type;
        if (modeType is not ("u16" or "u32"))
            throw new ConfigException($"{where}.mode must name a u16 or u32 GameSetting field.");
        Require([flags, .. words], "u32");
        Require(maps, "wstr");
        Require(booleans, "bool");
        var names = new[] { mode, flags }.Concat(maps).Concat(words).Concat(booleans).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ConfigException($"{where} aliases must name distinct GameSetting fields.");
        return new(mode, modeType, maps, flags, mask, words, booleans, ReadPreservedWords(tables, build, map, where));

        void Require(IEnumerable<string> names, string type)
        {
            if (names.Any(name => !fields.Any(field => field.Name == name && field.Type == type)))
                throw new ConfigException($"{where} must name existing {type} GameSetting fields.");
        }
    }

    private static Dictionary<string, HashSet<uint>> ReadPreservedWords(PacketTables tables, string build,
        Dictionary<string, object?> projection, string where)
    {
        var result = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        if (!projection.TryGetValue("preserve_word_values", out var raw)) return result;
        where += ".preserve_word_values";
        if (raw is not Dictionary<string, object?> map || map.Count != 2
            || map.GetValueOrDefault("builds") is not string[] { Length: > 0 } builds
            || builds.Distinct(StringComparer.Ordinal).Count() != builds.Length
            || builds.Any(target => !tables.Builds.Contains(target, StringComparer.Ordinal))
            || map.GetValueOrDefault("fields") is not Dictionary<string, object?> { Count: > 0 } fields)
            throw new ConfigException($"{where} requires distinct known builds and a nonempty fields map.");
        foreach (var (field, value) in fields)
        {
            if (value is not string[] { Length: > 0 } texts)
                throw new ConfigException($"{where}.{field} requires a nonempty list of unsigned words.");
            var allowed = new HashSet<uint>();
            foreach (var text in texts)
                if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var word) || !allowed.Add(word))
                    throw new ConfigException($"{where}.{field} requires distinct unsigned 32-bit decimal values.");
            // An inherited allowance validates its named builds, whose clear-word aliases may differ from the child.
            foreach (var target in builds)
                if (tables.ValuesOf(target).GetValueOrDefault("room_projection_fields") is not Dictionary<string, object?> targetMap
                    || targetMap.GetValueOrDefault("clear_words") is not string[] words
                    || !words.Contains(field, StringComparer.Ordinal)
                    || !tables.Resolve(target, "GameSetting").Any(native => native.Name == field && native.Type == "u32"))
                    throw new ConfigException($"{where}.{field} must name an already-cleared u32 GameSetting field in '{target}'.");
            if (builds.Contains(build, StringComparer.Ordinal)) result.Add(field, allowed);
        }
        return result;
    }

    /// <summary>Reads the creator's mode from the native field named by the build metadata.</summary>
    /// <param name="setting">The complete decoded game setting.</param>
    /// <returns>The shared numeric mode identifier.</returns>
    public int ReadMode(IReadOnlyDictionary<string, object?> setting) =>
        Convert.ToInt32(setting[_mode], CultureInfo.InvariantCulture);

    /// <summary>Reads the first populated map alias in the build's audited fallback order.</summary>
    /// <param name="setting">The complete decoded game setting.</param>
    /// <returns>The retained map text, or an empty string if every native field is empty.</returns>
    public string ReadMap(IReadOnlyDictionary<string, object?> setting) =>
        _maps.Select(name => (string)setting[name]!).FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    /// <summary>Writes the committed mode without applying waiting-room gates to an otherwise empty list row.</summary>
    /// <param name="setting">The game-setting fields to encode.</param>
    /// <param name="mode">The committed shared mode identifier.</param>
    public void WriteMode(IDictionary<string, object?> setting, int mode) =>
        setting[_mode] = _modeType == "u16" ? checked((ushort)mode) : checked((uint)mode);

    /// <summary>Clears declared native stage gates except explicitly admitted word values, then writes the committed mode.</summary>
    /// <param name="setting">The complete retained game setting.</param>
    /// <param name="mode">The committed shared mode identifier.</param>
    public void Apply(IDictionary<string, object?> setting, int mode)
    {
        // PacketCodec represents u32 as its signed bit pattern; preserve unrelated high flags as well.
        var flags = unchecked((uint)Convert.ToInt32(setting[_flags], CultureInfo.InvariantCulture));
        setting[_flags] = unchecked((int)(flags & ~_clearMask));
        foreach (var word in _words)
            if (!_preservedWords.TryGetValue(word, out var allowed)
                || !allowed.Contains(unchecked((uint)Convert.ToInt32(setting[word], CultureInfo.InvariantCulture))))
                setting[word] = 0;
        foreach (var flag in _booleans)
            setting[flag] = false;
        WriteMode(setting, mode);
    }
}
