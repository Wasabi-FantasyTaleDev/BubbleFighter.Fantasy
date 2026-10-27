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

    private RoomProjectionFields(string mode, string modeType, string[] maps, string flags, uint clearMask,
        string[] words, string[] booleans) =>
        (_mode, _modeType, _maps, _flags, _clearMask, _words, _booleans) =
        (mode, modeType, maps, flags, clearMask, words, booleans);

    /// <summary>Loads the inherited aliases and rejects missing, overlapping or incorrectly typed native fields.</summary>
    /// <param name="tables">The packet layouts and build metadata.</param>
    /// <param name="build">The client build whose waiting room consumes the projection.</param>
    /// <returns>A projection checked against that build's complete game-setting layout.</returns>
    public static RoomProjectionFields Load(PacketTables tables, string build)
    {
        var where = $"{build} room_projection_fields";
        if (!tables.ValuesOf(build).TryGetValue("room_projection_fields", out var raw)
            || raw is not Dictionary<string, object?> map || map.Count != 6
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
        return new(mode, modeType, maps, flags, mask, words, booleans);

        void Require(IEnumerable<string> names, string type)
        {
            if (names.Any(name => !fields.Any(field => field.Name == name && field.Type == type)))
                throw new ConfigException($"{where} must name existing {type} GameSetting fields.");
        }
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

    /// <summary>Clears only the declared native stage gates and writes the committed mode, retaining all other values.</summary>
    /// <param name="setting">The complete retained game setting.</param>
    /// <param name="mode">The committed shared mode identifier.</param>
    public void Apply(IDictionary<string, object?> setting, int mode)
    {
        // PacketCodec represents u32 as its signed bit pattern; preserve unrelated high flags as well.
        var flags = unchecked((uint)Convert.ToInt32(setting[_flags], CultureInfo.InvariantCulture));
        setting[_flags] = unchecked((int)(flags & ~_clearMask));
        foreach (var word in _words)
            setting[word] = 0;
        foreach (var flag in _booleans)
            setting[flag] = false;
        WriteMode(setting, mode);
    }
}
