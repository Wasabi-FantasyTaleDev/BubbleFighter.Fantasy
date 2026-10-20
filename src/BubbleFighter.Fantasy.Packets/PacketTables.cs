using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using YamlDotNet.Serialization;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>One field of a structure as one client build has it, in wire order.</summary>
/// <param name="Name">The field's name: what the server fills it from, or <c>unknown_*</c>.</param>
/// <param name="Type">The wire type as the table spells it, such as <c>u32</c> or <c>list&lt;u32&gt;</c>.</param>
/// <param name="Default">The value written when the server has none of its own for the field.</param>
/// <param name="Vacant">The value an empty seat carries instead, when the table states one.</param>
/// <param name="Menu">The table's <c>menu</c> value for the field (a number, a field name or <c>empty</c>), or null.</param>
/// <param name="Origin">The build whose row or variant this field comes from.</param>
/// <param name="Offset">Where that build's reader stores the field in the client's object, when the table states it.</param>
public sealed record PacketField(string Name, string Type, long Default, long? Vacant, string? Menu, string Origin, int? Offset = null);

/// <summary>
/// The packet structure tables of a directory: <c>builds.yaml</c> names the client builds and what each
/// is based on, and every other <c>*.yaml</c> holds structures. A structure is written once, as the
/// baseline build has it; a build that differs says so beside the field it changes
/// (<c>except</c>, <c>only</c>, <c>also</c>, or a replacement under the build's name), or, when it
/// differs a lot, restates the whole structure as <c>Name@build</c>. What a build does not state is
/// its base's.
///
/// </summary>
public sealed class PacketTables
{
    /// <summary>The file format this reader understands; a file states it as <c>version</c>.</summary>
    public const int FormatVersion = 1;

    private const string BuildsFile = "builds.yaml";

    private static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
    {
        "u8", "u16", "u32", "u64", "bool", "f32", "f64", "wstr", "empty_list",
    };

    // The scalars a fixed run (`u32[49]`) may repeat.
    private static readonly HashSet<string> RunScalars = new(StringComparer.Ordinal)
    {
        "u8", "u16", "u32", "u64", "bool", "f32", "f64",
    };

    private readonly Dictionary<string, BuildEntry> _builds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Table>> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Build, string Struct), IReadOnlyList<PacketField>> _resolved = new();
    private readonly List<string> _buildOrder = [];
    private readonly List<string> _structOrder = [];

    private PacketTables(string directory) => Directory = directory;

    /// <summary>Gets the directory the tables were read from.</summary>
    public string Directory { get; }

    /// <summary>Gets the builds of <c>builds.yaml</c>, each after its base.</summary>
    public IReadOnlyList<string> Builds => _buildOrder;

    /// <summary>Gets the structure names, in file order.</summary>
    public IReadOnlyList<string> Structs => _structOrder;

    private sealed record BuildEntry(string? Base, IDictionary<object, object> Values, string File);

    private sealed record Row(RowField Field, bool Only, Dictionary<string, RowField[]> ByBuild, string Where);

    private sealed record RowField(string Name, string Type, long Default, long? Vacant, string? Menu, int? Offset);

    private sealed record Table(string Owner, List<Row> Rows, string Where);

    /// <summary>Reads and checks every table of a directory.</summary>
    /// <param name="directory">The packets directory.</param>
    /// <returns>The tables, with every build's every structure already resolved once.</returns>
    /// <exception cref="ConfigException">
    /// Thrown for a missing directory or <c>builds.yaml</c>, an unknown format version, malformed YAML, an
    /// unknown build, type or key, a base loop, duplicate field names, or a conditional field whose gate
    /// is missing, follows it, has the wrong type or cannot hold the mask.
    /// </exception>
    public static PacketTables Load(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
            throw new ConfigException($"The packets directory '{directory}' does not exist.");
        var tables = new PacketTables(directory);
        var buildsPath = Path.Combine(directory, BuildsFile);
        if (!File.Exists(buildsPath))
            throw new ConfigException($"'{buildsPath}' is missing: it names the client builds the tables are written for.");
        tables.ReadBuilds(buildsPath, ReadFile(buildsPath));
        foreach (var build in tables.Builds)
        {
            if (tables.ValuesOf(build).TryGetValue("capabilities", out var rawCapabilities))
            {
                if (rawCapabilities is not Dictionary<string, object?> capabilities)
                    throw new ConfigException($"'{buildsPath}' build '{build}': capabilities must be a map of boolean values.");
                foreach (var capability in capabilities.Keys)
                    tables.CapabilityOf(build, capability);
            }
        }
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.yaml").Order(StringComparer.Ordinal))
        {
            if (!string.Equals(Path.GetFileName(file), BuildsFile, StringComparison.Ordinal))
                tables.ReadStructs(file, ReadFile(file));
        }
        foreach (var name in tables._structOrder)
        {
            if (!tables._structs[name].ContainsKey(tables._buildOrder[0]))
                throw new ConfigException($"Structure '{name}' has no table of the baseline build '{tables._buildOrder[0]}'.");
            foreach (var build in tables._buildOrder)
                tables.Resolve(build, name);
        }
        return tables;
    }

    /// <summary>Gets the build a build is based on, or null for the baseline.</summary>
    /// <param name="build">The build.</param>
    /// <returns>The base build's name, or null.</returns>
    /// <exception cref="ConfigException">Thrown for a build <c>builds.yaml</c> does not name.</exception>
    public string? BaseOf(string build) => Entry(build).Base;

    /// <summary>Gets a build and the builds it is based on, nearest first.</summary>
    /// <param name="build">The build.</param>
    /// <returns>The build, its base, its base's base, down to the baseline.</returns>
    /// <exception cref="ConfigException">Thrown for a build <c>builds.yaml</c> does not name.</exception>
    public IReadOnlyList<string> LineageOf(string build)
    {
        var lineage = new List<string>();
        for (var current = build; current is not null; current = Entry(current).Base)
            lineage.Add(current);
        return lineage;
    }

    /// <summary>Gets one structure as one build has it.</summary>
    /// <param name="build">The build.</param>
    /// <param name="structure">The structure name.</param>
    /// <returns>The fields in wire order.</returns>
    /// <exception cref="ConfigException">Thrown for an unknown build or structure, duplicate field names, or an invalid conditional gate.</exception>
    public IReadOnlyList<PacketField> Resolve(string build, string structure)
    {
        if (_resolved.TryGetValue((build, structure), out var done))
            return done;
        var lineage = LineageOf(build);
        if (!_structs.TryGetValue(structure, out var tables))
            throw new ConfigException($"The packets directory '{Directory}' has no structure '{structure}'.");
        var owner = lineage.First(tables.ContainsKey);
        var table = tables[owner];
        var fields = new List<PacketField>();
        foreach (var row in table.Rows)
        {
            // The nearest build of the lineage that the row mentions decides; none means the row as written.
            var decided = false;
            foreach (var member in lineage)
            {
                if (row.ByBuild.TryGetValue(member, out var replacement))
                {
                    fields.AddRange(replacement.Select(field => ToField(field, member)));
                    decided = true;
                    break;
                }
                if (member == owner)
                    break;
            }
            if (!decided && !row.Only)
                fields.Add(ToField(row.Field, owner));
        }
        if (fields.GroupBy(field => field.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
            throw new ConfigException($"{table.Where}: build '{build}' ends up with two fields named '{twice.Key}'.");
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            if (!TryParseWhen(field.Type, out var gate, out var mask, out var inner))
                continue;
            var source = fields.Take(index).FirstOrDefault(candidate => candidate.Name == gate);
            var width = source?.Type switch { "u8" => 8, "u16" => 16, "u32" => 32, "u64" => 64, _ => 0 };
            if (width == 0)
                throw new ConfigException($"{table.Where}: build '{build}' field '{field.Name}' requires an earlier unsigned scalar '{gate}'.");
            if (mask == 0 || (width < 64 && mask >= (1UL << width)))
                throw new ConfigException($"{table.Where}: build '{build}' field '{field.Name}' mask must be nonzero and fit '{gate}' ({source!.Type}).");
            if (char.IsAsciiLetterUpper(inner[0]) && !_structs.ContainsKey(inner))
                throw new ConfigException($"{table.Where}: build '{build}' field '{field.Name}' names unknown structure '{inner}'.");
        }
        return _resolved[(build, structure)] = fields;
    }

    /// <summary>Gets a build's own values of <c>builds.yaml</c> laid over its base's, as nested maps of text.</summary>
    /// <param name="build">The build.</param>
    /// <returns>The merged values; a nested map is a <see cref="Dictionary{TKey,TValue}"/> of the same kind and a
    /// list of scalars is a <see cref="string"/> array that replaces the base's list as a whole.</returns>
    /// <exception cref="ConfigException">Thrown for an unknown build.</exception>
    public Dictionary<string, object?> ValuesOf(string build)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in LineageOf(build).Reverse())
            Overlay(merged, Entry(member).Values);
        merged.Remove("base");
        return merged;
    }

    /// <summary>Checks a named, inherited native capability before a dialect binds its packets.</summary>
    /// <param name="build">The packet-table build.</param>
    /// <param name="capability">The key under the build's capabilities map.</param>
    /// <returns>Whether the capability has been enabled for this build.</returns>
    /// <exception cref="ConfigException">Thrown for an unknown build, missing capability, or non-boolean value.</exception>
    public bool CapabilityOf(string build, string capability)
    {
        var values = ValuesOf(build);
        if (values.TryGetValue("capabilities", out var raw) && raw is Dictionary<string, object?> capabilities
            && capabilities.TryGetValue(capability, out var value) && value is string text
            && bool.TryParse(text, out var enabled))
            return enabled;
        throw new ConfigException($"Build '{build}' must define boolean capabilities.{capability} in '{BuildsFile}'.");
    }

    /// <summary>Counts the leaf values a build states itself in <c>builds.yaml</c>, its base aside.</summary>
    /// <param name="build">The build.</param>
    /// <returns>The number of values.</returns>
    /// <exception cref="ConfigException">Thrown for an unknown build.</exception>
    public int StatedValues(string build) => Leaves(Entry(build).Values) - (Entry(build).Base is null ? 0 : 1);

    /// <summary>Counts the rows that state something for a build, and the structures it restates in full.</summary>
    /// <param name="build">The build.</param>
    /// <returns>The row count and the names of the structures restated as <c>Name@build</c>.</returns>
    public (int Rows, IReadOnlyList<string> OwnTables) StatedRows(string build)
    {
        var rows = _structs.Values.SelectMany(tables => tables.Values).Sum(table => table.Rows.Count(row => row.ByBuild.ContainsKey(build)));
        var own = _structOrder.Where(name => build != _buildOrder[0] && _structs[name].ContainsKey(build)).ToArray();
        return (rows, own);
    }

    /// <summary>Renders one structure of one build as a table of position, type, name, values and origin.</summary>
    /// <param name="build">The build.</param>
    /// <param name="structure">The structure name.</param>
    /// <returns>The text, one field per line.</returns>
    /// <exception cref="ConfigException">Thrown for an unknown build or structure.</exception>
    public string Show(string build, string structure)
    {
        var fields = Resolve(build, structure);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{structure} of {build} ({fields.Count} fields; lineage {string.Join(" <- ", LineageOf(build))})");
        text.AppendLine("  #  offset  type                     name                      default  vacant  menu             from");
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{i,3}  {(field.Offset is { } offset ? "+" + offset.ToString(CultureInfo.InvariantCulture) : ""),6}  {field.Type,-24} {field.Name,-25} {(field.Default == 0 ? "" : field.Default.ToString(CultureInfo.InvariantCulture)),7}  {field.Vacant?.ToString(CultureInfo.InvariantCulture) ?? "",6}  {field.Menu ?? "",-16} {field.Origin}");
        }
        return text.ToString();
    }

    /// <summary>Renders the fields in which one build's structure differs from another's.</summary>
    /// <param name="left">The first build.</param>
    /// <param name="right">The second build.</param>
    /// <param name="structure">The structure name.</param>
    /// <returns>One line per field only one build has, or that the two write differently.</returns>
    /// <exception cref="ConfigException">Thrown for an unknown build or structure.</exception>
    public string Diff(string left, string right, string structure)
    {
        var a = Resolve(left, structure);
        var b = Resolve(right, structure);
        var names = b.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        var other = b.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{structure}: {left} ({a.Count} fields) against {right} ({b.Count} fields)");
        foreach (var field in a)
        {
            if (!names.Contains(field.Name))
                text.AppendLine(CultureInfo.InvariantCulture, $"  only {left,-8} {Describe(field)}");
            else if (Describe(other[field.Name]) != Describe(field))
                text.AppendLine(CultureInfo.InvariantCulture, $"  differs       {Describe(field)}  |  {right}: {Describe(other[field.Name])}");
        }
        var mine = a.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in b.Where(field => !mine.Contains(field.Name)))
            text.AppendLine(CultureInfo.InvariantCulture, $"  only {right,-8} {Describe(field)}");
        return text.ToString();
    }

    /// <summary>Renders everything the tables say about one build as JSON, for tools that are
    /// not.NET.</summary> <param name="build">The build.</param> <returns>An object with <c>build</c>,
    /// <c>base</c>, <c>values</c> and, per structure, its fields in wire order.</returns> <exception
    /// cref="ConfigException">Thrown for an unknown build.</exception>
    public string ToJson(string build)
    {
        var structs = _structOrder.ToDictionary(name => name, name => Resolve(build, name).Select(field => new Dictionary<string, object?>
        {
            ["name"] = field.Name, ["type"] = field.Type, ["default"] = field.Default, ["vacant"] = field.Vacant,
            ["menu"] = field.Menu, ["offset"] = field.Offset, ["from"] = field.Origin,
        }).ToArray());
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["build"] = build, ["base"] = BaseOf(build), ["values"] = ValuesOf(build), ["structs"] = structs,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string Describe(PacketField field) =>
        $"{field.Name}: {field.Type}" + (field.Default != 0 ? $" = {field.Default}" : "") + (field.Vacant is { } vacant ? $" ? {vacant}" : "")
        + (field.Menu is null ? "" : $" (menu {field.Menu})");

    private static PacketField ToField(RowField field, string origin) =>
        new(field.Name, field.Type, field.Default, field.Vacant, field.Menu, origin, field.Offset);

    private BuildEntry Entry(string build) =>
        _builds.TryGetValue(build, out var entry)
            ? entry
            : throw new ConfigException($"'{Path.Combine(Directory, BuildsFile)}' does not name the build '{build}'.");

    private static IDictionary<object, object> ReadFile(string path)
    {
        object? graph;
        try
        {
            using var reader = File.OpenText(path);
            graph = new DeserializerBuilder().Build().Deserialize(reader);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ConfigException($"'{path}' is not valid YAML: {ex.Message}");
        }
        if (graph is not IDictionary<object, object> map)
            throw new ConfigException($"'{path}' must be a YAML mapping.");
        if (!map.TryGetValue("version", out var version) || version as string != FormatVersion.ToString(CultureInfo.InvariantCulture))
            throw new ConfigException($"'{path}' must state 'version: {FormatVersion}', the table format this server reads; it states '{version ?? "nothing"}'.");
        return map;
    }

    private void ReadBuilds(string path, IDictionary<object, object> file)
    {
        foreach (var key in file.Keys)
        {
            if (key is not ("version" or "builds"))
                throw new ConfigException($"'{path}' has the unknown key '{key}'.");
        }
        if (!file.TryGetValue("builds", out var raw) || raw is not IDictionary<object, object> builds || builds.Count == 0)
            throw new ConfigException($"'{path}' must list at least one build under 'builds'.");
        foreach (var (rawName, rawEntry) in builds)
        {
            var name = rawName as string ?? "";
            var values = rawEntry as IDictionary<object, object> ?? new Dictionary<object, object>();
            _builds[name] = new BuildEntry(values.TryGetValue("base", out var rawBase) ? rawBase as string : null, values, path);
        }
        // Order each build after its base; the one build without a base is the baseline.
        var pending = _builds.Keys.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(name => _builds[name].Base is not { } parent || _buildOrder.Contains(parent)).ToList();
            if (ready.Count == 0)
            {
                var stuck = pending[0];
                throw new ConfigException(_builds.ContainsKey(_builds[stuck].Base!)
                    ? $"'{path}': the bases loop at '{stuck}'."
                    : $"'{path}': build '{stuck}' is based on '{_builds[stuck].Base}', which is not listed.");
            }
            foreach (var name in ready)
            {
                _buildOrder.Add(name);
                pending.Remove(name);
            }
        }
        if (_builds.Values.Count(entry => entry.Base is null) != 1)
            throw new ConfigException($"'{path}' must have exactly one build without a base: the baseline.");
    }

    private void ReadStructs(string path, IDictionary<object, object> file)
    {
        foreach (var key in file.Keys)
        {
            if (key is not ("version" or "structs"))
                throw new ConfigException($"'{path}' has the unknown key '{key}'.");
        }
        if (!file.TryGetValue("structs", out var raw) || raw is not IDictionary<object, object> structs)
            return;
        foreach (var (rawKey, rawRows) in structs)
        {
            var key = rawKey as string ?? "";
            var at = key.IndexOf('@');
            var name = (at < 0 ? key : key[..at]).Trim();
            var owner = at < 0 ? _buildOrder[0] : key[(at + 1)..].Trim();
            var where = $"'{path}' structure '{key}'";
            if (name.Length == 0 || !_builds.ContainsKey(owner))
                throw new ConfigException($"{where}: a structure is 'Name' or 'Name@build' with a build of {BuildsFile}.");
            if (rawRows is not IList<object> rows)
                throw new ConfigException($"{where} must be a list of fields.");
            if (!_structs.TryGetValue(name, out var tables))
            {
                _structs[name] = tables = new Dictionary<string, Table>(StringComparer.Ordinal);
                _structOrder.Add(name);
            }
            if (tables.ContainsKey(owner))
                throw new ConfigException($"{where} is written twice.");
            tables[owner] = new Table(owner, [.. rows.Select((row, index) => ReadRow(row, $"{where} row {index + 1}"))], where);
        }
    }

    private Row ReadRow(object? raw, string where)
    {
        if (raw is not IDictionary<object, object> map)
            throw new ConfigException($"{where} must be 'name: type' with optional offset, default, vacant, menu, except, only, also and build keys.");
        var byBuild = new Dictionary<string, RowField[]>(StringComparer.Ordinal);
        var field = ReadField(map, where, key => key is "except" or "only" or "also" || _builds.ContainsKey(key));
        var only = false;
        foreach (var (rawKey, value) in map)
        {
            var key = rawKey as string ?? "";
            switch (key)
            {
                case "except":
                    foreach (var build in BuildList(value, where, key))
                        Add(build, []);
                    break;
                case "only":
                    only = true;
                    foreach (var build in BuildList(value, where, key))
                        Add(build, [field]);
                    break;
                case "also":
                    foreach (var build in BuildList(value, where, key))
                        Add(build, [field]);
                    break;
                default:
                    if (_builds.ContainsKey(key))
                    {
                        var items = value switch
                        {
                            IList<object> list => list,
                            IDictionary<object, object> single => [single],
                            _ => throw new ConfigException($"{where}: '{key}' must list the fields that build has here."),
                        };
                        Add(key, [.. items.Select((item, index) => item is IDictionary<object, object> itemMap
                            ? ReadField(itemMap, $"{where} {key} field {index + 1}", _ => false)
                            : throw new ConfigException($"{where}: every field under '{key}' must be 'name: type'."))]);
                    }
                    break;
            }
        }
        return new Row(field, only, byBuild, where);

        void Add(string build, RowField[] fields)
        {
            if (!byBuild.TryAdd(build, fields))
                throw new ConfigException($"{where} states build '{build}' twice.");
        }
    }

    private IEnumerable<string> BuildList(object? raw, string where, string key)
    {
        if (raw is not IList<object> list || list.Count == 0)
            throw new ConfigException($"{where}: '{key}' must be a list of builds.");
        foreach (var item in list)
        {
            var build = item as string ?? "";
            if (!_builds.ContainsKey(build))
                throw new ConfigException($"{where}: '{key}' names '{build}', which is not a build of {BuildsFile}.");
            yield return build;
        }
    }

    // One field: the single key that is neither a field property nor a row annotation is its name, and
    // that key's value is its type.
    private static RowField ReadField(IDictionary<object, object> map, string where, Func<string, bool> annotation)
    {
        string? name = null;
        string? type = null;
        string? menu = null;
        long constant = 0;
        long? vacant = null;
        int? offset = null;
        foreach (var (rawKey, value) in map)
        {
            var key = rawKey as string ?? "";
            switch (key)
            {
                case "menu":
                    menu = value as string ?? throw new ConfigException($"{where}: 'menu' must be a number, a field name or 'empty'.");
                    break;
                case "default":
                    constant = Number(value as string, where, key);
                    break;
                case "vacant":
                    vacant = Number(value as string, where, key);
                    break;
                case "offset":
                    offset = checked((int)Number(value as string, where, key));
                    break;
                default:
                    if (annotation(key))
                        break;
                    if (name is not null)
                        throw new ConfigException($"{where} has both '{name}' and '{key}': a row is one field. Is '{key}' a misspelt build or key?");
                    name = key;
                    type = (value as string)?.Trim() ?? throw new ConfigException($"{where}: field '{key}' needs a type.");
                    break;
            }
        }
        if (name is null || type is null)
            throw new ConfigException($"{where} names no field.");
        if (name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
            throw new ConfigException($"{where}: field name '{name}' must be lower-case letters, digits and underscores.");
        var known = Scalars.Contains(type)
            || TryParseRun(type, out _, out _)
            || (type.StartsWith("list<", StringComparison.Ordinal) && type.EndsWith('>') && type.Length > 6)
            || TryParseOptional(type, out _, out _)
            || TryParseWhen(type, out _, out _, out _)
            || (type.Length > 0 && char.IsAsciiLetterUpper(type[0]) && type.All(char.IsAsciiLetterOrDigit));
        if (!known)
            throw new ConfigException($"{where}: field '{name}' has the unknown type '{type}'.");
        return new RowField(name, type, constant, vacant, menu, offset);
    }

    /// <summary>
    /// Splits a conditional field such as <c>when&lt;mask&amp;0x01,f32&gt;</c>. The field is present when any masked
    /// bit is set in the earlier unsigned scalar; table resolution validates that dependency and its width.
    /// </summary>
    /// <param name="type">The field type as written in a table.</param>
    /// <param name="gate">The earlier field in the same structure.</param>
    /// <param name="mask">The hexadecimal bits that select this field.</param>
    /// <param name="inner">The scalar or named structure encoded when selected.</param>
    /// <returns>Whether the spelling is a conditional scalar or structure field.</returns>
    public static bool TryParseWhen(string type, out string gate, out ulong mask, out string inner)
    {
        ArgumentNullException.ThrowIfNull(type);
        gate = inner = "";
        mask = 0;
        const string prefix = "when<";
        if (!type.StartsWith(prefix, StringComparison.Ordinal) || !type.EndsWith('>'))
            return false;
        var separator = type.IndexOf("&0x", StringComparison.Ordinal);
        var comma = type.IndexOf(',', StringComparison.Ordinal);
        if (separator <= prefix.Length || comma <= separator + 3)
            return false;
        var name = type[prefix.Length..separator];
        var element = type[(comma + 1)..^1];
        if (name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            || !ulong.TryParse(type.AsSpan(separator + 3, comma - separator - 3),
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits)
            || !(RunScalars.Contains(element) || element == "wstr"
                || (element.Length > 0 && char.IsAsciiLetterUpper(element[0]) && element.All(char.IsAsciiLetterOrDigit))))
            return false;
        (gate, mask, inner) = (name, bits, element);
        return true;
    }

    /// <summary>Splits a fixed run such as <c>u32[49]</c> or <c>ScoreRow[16]</c> into its element type and count.</summary>
    /// <param name="type">The field type as the table spells it.</param>
    /// <param name="element">The repeated scalar or structure, when <paramref name="type"/> is a run.</param>
    /// <param name="count">How many of it follow one another, when <paramref name="type"/> is a run.</param>
    /// <returns>Whether the type repeats a scalar or named structure between one and 65,535 times.</returns>
    public static bool TryParseRun(string type, out string element, out int count)
    {
        ArgumentNullException.ThrowIfNull(type);
        element = "";
        count = 0;
        var open = type.IndexOf('[', StringComparison.Ordinal);
        if (open <= 0 || !type.EndsWith(']')
            || !int.TryParse(type.AsSpan(open + 1, type.Length - open - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            || n is <= 0 or > ushort.MaxValue)
            return false;
        var inner = type[..open];
        if (!RunScalars.Contains(inner)
            && !(char.IsAsciiLetterUpper(inner[0]) && inner.All(char.IsAsciiLetterOrDigit)))
            return false;
        (element, count) = (type[..open], n);
        return true;
    }

    /// <summary>
    /// Splits a tagged optional block such as <c>optional&lt;0x3A,RoomPlayerTail&gt;</c>: one tag byte, and the
    /// named structure only when the byte equals <paramref name="tag"/> (kr-1256 sub_1143710 branches this way).
    ///
    /// </summary>
    /// <param name="type">The field type as the table spells it.</param>
    /// <param name="tag">The tag byte that announces the structure, when <paramref name="type"/> is optional.</param>
    /// <param name="structure">The structure that follows that tag, when <paramref name="type"/> is optional.</param>
    /// <returns>Whether the type is an optional block with a hexadecimal tag and a named structure.</returns>
    public static bool TryParseOptional(string type, out byte tag, out string structure)
    {
        ArgumentNullException.ThrowIfNull(type);
        tag = 0;
        structure = "";
        const string Prefix = "optional<0x";
        if (!type.StartsWith(Prefix, StringComparison.Ordinal) || !type.EndsWith('>'))
            return false;
        var comma = type.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0
            || !byte.TryParse(type.AsSpan(Prefix.Length, comma - Prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return false;
        var inner = type[(comma + 1)..^1];
        if (inner.Length == 0 || !char.IsAsciiLetterUpper(inner[0]) || !inner.All(char.IsAsciiLetterOrDigit))
            return false;
        (tag, structure) = (value, inner);
        return true;
    }

    /// <summary>Gets the nearest build of a lineage that the tables name.</summary>
    /// <param name="lineage">A build and the builds it descends from, nearest first.</param>
    /// <returns>The first member of <paramref name="lineage"/> that <c>builds.yaml</c> names, or null.</returns>
    public string? NearestBuild(IEnumerable<string> lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        return lineage.FirstOrDefault(_builds.ContainsKey);
    }

    private static long Number(string? text, string where, string key) =>
        long.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ConfigException($"{where}: '{key}' must be a whole number, not '{text}'.");

    private static void Overlay(Dictionary<string, object?> target, IDictionary<object, object> overlay)
    {
        foreach (var (rawKey, value) in overlay)
        {
            var key = rawKey as string ?? "";
            if (value is IDictionary<object, object> nested)
            {
                if (!target.TryGetValue(key, out var existing) || existing is not Dictionary<string, object?> inner)
                    target[key] = inner = new Dictionary<string, object?>(StringComparer.Ordinal);
                Overlay(inner, nested);
            }
            else if (value is IList<object> list)
            {
                // A list of scalars (for example a build's starter item ids) replaces the base's whole list.
                target[key] = list.Select(item => item as string
                    ?? throw new ConfigException($"builds.yaml: '{key}' must list scalars only.")).ToArray();
            }
            else
            {
                target[key] = value as string;
            }
        }
    }

    private static int Leaves(IDictionary<object, object> map) =>
        map.Values.Sum(value => value is IDictionary<object, object> nested ? Leaves(nested) : 1);
}
