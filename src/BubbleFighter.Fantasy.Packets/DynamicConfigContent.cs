using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using YamlDotNet.Serialization;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>Reads the build's published dynamic-configuration content for its packet writer and feature adapters.</summary>
public static class DynamicConfigContent
{
    // The spellings a content value may use for an OLE automation date. Only these exact shapes convert, so a label or
    // a path is never mistaken for a date.
    private static readonly string[] DateFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"];

    /// <summary>Returns a published slot's content, or null when the build supplies no content for that slot.</summary>
    /// <param name="tables">The packet tables whose build values locate the content file.</param>
    /// <param name="build">The native build whose published content is requested.</param>
    /// <param name="bit">The category's registry bit.</param>
    /// <param name="schema">The schema the feature expects the category to publish.</param>
    /// <returns>The category's normalized values, or null when the category is absent.</returns>
    /// <exception cref="ConfigException">The published category does not have the requested schema and content.</exception>
    public static IReadOnlyDictionary<string, object?>? PublishedSlot(PacketTables tables, string build, int bit, string schema)
    {
        if (Load(tables, build).GetValueOrDefault(bit.ToString(CultureInfo.InvariantCulture)) is not Dictionary<string, object?> entry)
            return null;
        if (entry.GetValueOrDefault("schema") as string != schema || entry.GetValueOrDefault("content") is not Dictionary<string, object?> content)
            throw new ConfigException($"Build '{build}' dynamic_config slot {bit} must publish {schema} content.");
        return content;
    }

    /// <summary>Loads category rows beside the packet schemas and normalizes their YAML maps and lists for the codec.</summary>
    /// <param name="tables">The packet tables that own the content directory.</param>
    /// <param name="build">The native build whose dynamic_config block names the content file.</param>
    /// <param name="fileKey">The content-file key within the build's dynamic_config block.</param>
    /// <returns>Category rows by registry bit, or an empty map when the build does not name a content file.</returns>
    /// <exception cref="ConfigException">The named content file is missing or does not declare any slots.</exception>
    public static Dictionary<string, object?> Load(PacketTables tables, string build, string fileKey = "content_file")
    {
        if (tables.ValuesOf(build).GetValueOrDefault("dynamic_config") is not Dictionary<string, object?> block
            || block.GetValueOrDefault(fileKey) is not string { Length: > 0 } name)
            return [];
        var path = Path.Combine(tables.Directory, name);
        if (!File.Exists(path))
            throw new ConfigException($"Build '{build}' dynamic_config names the missing content file '{path}'.");
        var deserializer = new DeserializerBuilder().Build();
        var parsed = deserializer.Deserialize<Dictionary<object, object>>(new StringReader(File.ReadAllText(path)));
        if (parsed.GetValueOrDefault("slots") is not IDictionary<object, object> slots || slots.Count == 0)
            throw new ConfigException($"'{path}' must list at least one slot under 'slots'.");
        return new Dictionary<string, object?>(Normalize(slots), StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> Normalize(IDictionary<object, object> map)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in map)
        {
            var key = rawKey as string ?? rawKey.ToString() ?? "";
            normalized[key] = rawValue switch
            {
                IDictionary<object, object> nested => Normalize(nested),
                IList<object> list => list.Select(item => item is IDictionary<object, object> nestedItem
                    ? (object?)Normalize(nestedItem) : DateOrValue(item)).ToArray(),
                _ => DateOrValue(rawValue),
            };
        }
        return normalized;
    }

    // A date written as text becomes the OLE automation date the f64 fields carry (days since 1899-12-30). The client
    // compares these with its own local wall clock (kr-1256 sub_E5EF10 converts _time64 through _localtime64_s), so the
    // text is a wall-clock time with no zone and converts without one.
    private static object? DateOrValue(object? value) =>
        value is string text && text.Length >= 10 && char.IsAsciiDigit(text[0])
            && DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToOADate()
            : value;
}
