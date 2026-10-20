using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using YamlDotNet.RepresentationModel;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// An immutable opcode table for one client profile, seen through one connection. The instance
/// the constructor returns is the game view; <see cref="ForConnection"/> returns the relay view,
/// which shares the same rows.
/// </summary>
public sealed class OpcodeTable : IOpcodeTable, IOpcodeNameFallback
{
    private readonly Func<OpcodeConnection, PacketDirection, string, ushort?>? _fallback;
    private readonly Dictionary<(PacketDirection Direction, ushort Value), OpcodeEntry> _entries;
    private readonly OpcodeTable _game;
    private readonly OpcodeTable _relay;

    /// <summary>Creates the table.</summary>
    /// <param name="profile">The profile the table belongs to.</param>
    /// <param name="entries">The entries of every connection.</param>
    /// <param name="fallback">Optional logged lookup used only when an existing codec asks for a missing exact name.</param>
    /// <exception cref="ConfigException">Thrown when two rows claim one (connection, direction, value) or one (connection, direction, name).</exception>
    public OpcodeTable(ClientProfile profile, IEnumerable<OpcodeEntry> entries,
        Func<OpcodeConnection, PacketDirection, string, ushort?>? fallback = null)
    {
        _fallback = fallback;
        var all = entries.ToArray();
        _game = this;
        _relay = new OpcodeTable(profile, all, OpcodeConnection.Relay, this);
        Profile = profile;
        Connection = OpcodeConnection.Game;
        _entries = Index(profile, all, OpcodeConnection.Game);
    }

    private OpcodeTable(ClientProfile profile, OpcodeEntry[] all, OpcodeConnection connection, OpcodeTable game)
    {
        _game = game;
        _fallback = game._fallback;
        _relay = this;
        Profile = profile;
        Connection = connection;
        _entries = Index(profile, all, connection);
    }

    private static Dictionary<(PacketDirection, ushort), OpcodeEntry> Index(ClientProfile profile, OpcodeEntry[] all, OpcodeConnection connection)
    {
        var byValue = new Dictionary<(PacketDirection, ushort), OpcodeEntry>();
        var names = new HashSet<(PacketDirection, string)>();
        foreach (var entry in all.Where(e => e.Connection == connection || e.Connection == OpcodeConnection.Both))
        {
            if (!byValue.TryAdd((entry.Direction, entry.Value), entry))
                throw new ConfigException($"Opcode table '{profile.Id}': {connection} {entry.Direction} {entry.Value} is claimed by both {byValue[(entry.Direction, entry.Value)].Name} and {entry.Name}.");
            if (!names.Add((entry.Direction, entry.Name)))
                throw new ConfigException($"Opcode table '{profile.Id}': {connection} {entry.Direction} name {entry.Name} is assigned twice.");
        }
        return byValue;
    }

    /// <inheritdoc />
    public ClientProfile Profile { get; }

    /// <inheritdoc />
    public OpcodeConnection Connection { get; }

    /// <inheritdoc />
    public IOpcodeTable ForConnection(OpcodeConnection connection) => connection switch
    {
        OpcodeConnection.Game => _game,
        OpcodeConnection.Relay => _relay,
        _ => throw new ArgumentOutOfRangeException(nameof(connection), connection, "A view resolves either the game or the relay connection."),
    };

    /// <inheritdoc />
    public bool TryGet(PacketDirection d, ushort value, out OpcodeEntry? e) =>
        _entries.TryGetValue((d, value), out e);

    /// <inheritdoc />
    public IEnumerable<OpcodeEntry> All => _entries.Values;

    /// <inheritdoc />
    public ushort? ResolveMissingName(PacketDirection direction, string name) => _fallback?.Invoke(Connection, direction, name);

    /// <inheritdoc />
    public string Describe(PacketDirection d, ushort value) =>
        _entries.TryGetValue((d, value), out var entry) ? $"{entry.Name}({value})" : $"Unknown({value})";
}

/// <summary>Loads <c>opcodes.yaml</c> files into <see cref="OpcodeTable"/> instances.</summary>
public static class OpcodeTableLoader
{
    /// <summary>Loads an opcode table file.</summary>
    /// <param name="path">The YAML file path.</param>
    /// <param name="profile">The profile the table belongs to; must match the file's <c>profile</c> key.</param>
    /// <returns>The loaded table.</returns>
    /// <exception cref="ConfigException">Thrown on a missing file, malformed YAML, profile
    /// mismatch, invalid field, or duplicate (direction, value) pair.</exception>
    public static OpcodeTable Load(string path, ClientProfile profile)
    {
        if (!File.Exists(path))
            throw new ConfigException($"Opcode table file '{path}' does not exist.");
        object? graph;
        using (var reader = new StreamReader(path))
        {
            var stream = new YamlStream();
            try
            {
                stream.Load(reader);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                throw new ConfigException($"Opcode table file '{path}': {ex.Message}");
            }
            graph = stream.Documents.Count == 0 ? null : ToGraph(stream.Documents[0].RootNode);
        }
        if (graph is not IReadOnlyDictionary<string, object?> doc)
            throw new ConfigException($"Opcode table file '{path}' must be a YAML mapping.");
        if (doc.TryGetValue("profile", out var declared) && declared?.ToString() is { } declaredId && declaredId != profile.Id)
            throw new ConfigException($"Opcode table file '{path}' declares profile '{declaredId}' but is loaded for '{profile.Id}'.");
        if (!doc.TryGetValue("opcodes", out var list) || list is not IList<object?> rows)
            throw new ConfigException($"Opcode table file '{path}' must contain an 'opcodes' list.");

        var entries = new List<OpcodeEntry>();
        var seen = new HashSet<(OpcodeConnection, PacketDirection, ushort)>();
        var named = new HashSet<(OpcodeConnection, PacketDirection, string)>();
        var line = 0;
        foreach (var row in rows)
        {
            line++;
            if (row is not IReadOnlyDictionary<string, object?> map)
                throw new ConfigException($"Opcode table file '{path}': entry {line} must be a mapping.");
            var value = RequiredUshort(map, "value", path, line);
            var direction = RequiredEnum<PacketDirection>(map, "direction", path, line, "c2s", PacketDirection.ClientToServer, "s2c", PacketDirection.ServerToClient);
            var name = RequiredString(map, "name", path, line);
            var evidence = RequiredEvidence(map, path, line);
            var source = map.TryGetValue("source", out var s) ? s?.ToString() : null;
            var notes = map.TryGetValue("notes", out var n) ? n?.ToString() : null;
            var nameSource = OptionalNameSource(map, name, path, line);
            var connection = OptionalConnection(map, path, line);
            foreach (var scope in connection == OpcodeConnection.Both ? new[] { OpcodeConnection.Game, OpcodeConnection.Relay } : [connection])
            {
                if (!seen.Add((scope, direction, value)))
                    throw new ConfigException($"Opcode table file '{path}': duplicate ({scope}, {direction}, {value}) at entry {line}.");
                if (!named.Add((scope, direction, name)))
                    throw new ConfigException($"Opcode table file '{path}': duplicate ({scope}, {direction}) name '{name}' at entry {line}.");
            }
            entries.Add(new OpcodeEntry(value, direction, name, evidence, source, notes, nameSource, connection));
        }
        return new OpcodeTable(profile, entries);
    }

    // Rows that omit 'connection' keep their old meaning: the game connection.
    private static OpcodeConnection OptionalConnection(IReadOnlyDictionary<string, object?> map, string path, int line)
    {
        if (!map.TryGetValue("connection", out var raw) || raw?.ToString() is not { Length: > 0 } text)
            return OpcodeConnection.Game;
        return text switch
        {
            "game" => OpcodeConnection.Game,
            "relay" => OpcodeConnection.Relay,
            "both" => OpcodeConnection.Both,
            _ => throw new ConfigException($"Opcode table file '{path}': entry {line} 'connection' must be 'game', 'relay', or 'both'."),
        };
    }

    private static string RequiredString(IReadOnlyDictionary<string, object?> map, string key, string path, int line)
    {
        if (!map.TryGetValue(key, out var value) || value?.ToString() is not { Length: > 0 } s)
            throw new ConfigException($"Opcode table file '{path}': entry {line} is missing '{key}'.");
        return s;
    }

    private static ushort RequiredUshort(IReadOnlyDictionary<string, object?> map, string key, string path, int line)
    {
        var text = RequiredString(map, key, path, line);
        if (!ushort.TryParse(text, out var value))
            throw new ConfigException($"Opcode table file '{path}': entry {line} '{key}' must be an integer in [0, 65535].");
        return value;
    }

    private static T RequiredEnum<T>(IReadOnlyDictionary<string, object?> map, string key, string path, int line, string a, T va, string b, T vb) where T : struct
    {
        var text = RequiredString(map, key, path, line);
        return text.Equals(a, StringComparison.OrdinalIgnoreCase) ? va
            : text.Equals(b, StringComparison.OrdinalIgnoreCase) ? vb
            : throw new ConfigException($"Opcode table file '{path}': entry {line} '{key}' must be '{a}' or '{b}'.");
    }

    private static Evidence RequiredEvidence(IReadOnlyDictionary<string, object?> map, string path, int line) =>
        RequiredString(map, "evidence", path, line).ToLowerInvariant() switch
        {
            "verified" => Evidence.Verified,
            "inferred" => Evidence.Inferred,
            "unverified" => Evidence.Unverified,
            "captured" => Evidence.Captured,
            _ => throw new ConfigException(
                $"Opcode table file '{path}': entry {line} 'evidence' must be 'verified', 'inferred', 'unverified', or 'captured'."),
        };

    // name_source is optional while the table is being classified; when present it must be
    // one of the four forms. UnknownNNN names pair only with 'none', and names ending in '_'
    // (ours, not the client's) pair only with 'coined', so a reader can tell them apart.
    private static string? OptionalNameSource(IReadOnlyDictionary<string, object?> map, string name, string path, int line)
    {
        var coinedName = name.EndsWith('_');
        if (!map.TryGetValue("name_source", out var raw) || raw?.ToString() is not { Length: > 0 } text)
            return coinedName
                ? throw new ConfigException($"Opcode table file '{path}': entry {line} name '{name}' ends in '_' and must declare name_source 'coined'.")
                : null;
        var valid = text is "mirrored" or "none" or "coined"
            || (text.StartsWith("label@", StringComparison.Ordinal) && text.Length > "label@".Length);
        if (!valid)
            throw new ConfigException(
                $"Opcode table file '{path}': entry {line} 'name_source' must be 'label@<profile>', 'mirrored', 'coined', or 'none'.");
        if (name.StartsWith("Unknown", StringComparison.Ordinal) != (text == "none"))
            throw new ConfigException(
                $"Opcode table file '{path}': entry {line} pairs name '{name}' with name_source '{text}'; "
                + "UnknownNNN names take 'none' and only they do.");
        if (coinedName != (text == "coined"))
            throw new ConfigException(
                $"Opcode table file '{path}': entry {line} pairs name '{name}' with name_source '{text}'; "
                + "coined names end in '_' and only they do.");
        return text;
    }

    private static object? ToGraph(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value,
        YamlMappingNode mapping => mapping.Children.ToDictionary(
            kv => ((YamlScalarNode)kv.Key).Value ?? "",
            kv => ToGraph(kv.Value)),
        YamlSequenceNode sequence => sequence.Children.Select(ToGraph).Cast<object?>().ToList(),
        _ => null,
    };
}
