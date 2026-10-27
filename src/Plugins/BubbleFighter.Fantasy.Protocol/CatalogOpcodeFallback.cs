using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// Binds opcode-catalog research candidates that the runtime table leaves nameless. The catalog
/// (opcode-catalog.yaml beside the runtime table) is a generated review input, never authority: a row
/// is only bound when its name is not <c>Unknown*</c>, the runtime table has no reviewed name at that
/// value, and the best-graded peers agree on one name. A binding is logged as unreviewed, the runtime
/// table is never edited by it, and a later review can still promote or reject the row.
/// </summary>
internal sealed class CatalogOpcodeFallback(IOpcodeTable runtime, ILogger logger, string path)
{
    private readonly Dictionary<(OpcodeConnection, PacketDirection), List<Row>> _rows = [];

    /// <summary>
    /// Loads the profile's catalog and indexes its admissible rows. The profile must match the runtime
    /// table, and the file must exist (when explicitly configured), stay inside its byte and row
    /// bounds and parse as YAML. Rows keep an explicit <c>connection</c> column when the catalog
    /// carries one; rows without one are guarded before they default to the game scope:
    /// <c>rcp*</c>/<c>rsp*</c> names are refused for every build, and for kr-1256 additionally every
    /// row at value 2000 or above and every <c>gpp*</c> row. These prefixes and that region belong to
    /// the relay packet family — the catalog generator maps <c>gpp/rcp/rsp</c> to the relay space and
    /// <c>gcp/gsp</c> to the game space (tools/corpus/catalog.py SPACE_BY_PREFIX), kr-812 names the
    /// family with real RTTI (<c>Packet_gppKickFail</c>), and the kr-1256 catalog carries no
    /// connection column at all, so every row would otherwise default to game. The kr-1256 rows the
    /// guards skip are all shape-matched research names taken from legacy serializers (for example
    /// C2S 2076 <c>gppReqCreateAISubordinate</c>, shape@kr-812/id-1809), which the runtime table binds
    /// explicitly where a real reader exists.
    /// </summary>
    /// <exception cref="ConfigException">Thrown for a configured-but-missing, oversized, unparsable or
    /// mismatched catalog, an invalid direction, value or connection column, or a row budget overrun.</exception>
    internal static CatalogOpcodeFallback? Load(ProtocolOptions options, IOpcodeTable runtime, ILogger logger)
    {
        var path = options.OpcodeCatalogPath.Length > 0 ? options.OpcodeCatalogPath
            : Path.Combine(Path.GetDirectoryName(options.OpcodeTablePath) ?? "", "opcode-catalog.yaml");
        if (!File.Exists(path))
        {
            if (options.OpcodeCatalogPath.Length > 0) throw new ConfigException($"Configured opcode catalog is missing: {path}");
            return null;
        }
        if (new FileInfo(path).Length > options.MaxCatalogFileBytes) throw new ConfigException($"Opcode catalog exceeds its byte bound: {path}");
        Catalog document;
        try
        {
            document = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties().Build().Deserialize<Catalog>(File.ReadAllText(path));
        }
        catch (YamlDotNet.Core.YamlException error) { throw new ConfigException($"Invalid opcode catalog '{path}': {error.Message}"); }
        if (document is null || document.Profile != runtime.Profile.Id) throw new ConfigException($"Opcode catalog profile mismatch: {path}");
        if (document.Opcodes.Count > options.MaxCatalogRows) throw new ConfigException($"Opcode catalog exceeds its row bound: {path}");
        var result = new CatalogOpcodeFallback(runtime, logger, path);
        foreach (var row in document.Opcodes)
        {
            if (Grade(row.Grade) == 0 || row.Name.Length == 0 || row.Name.StartsWith("Unknown", StringComparison.Ordinal)) continue;
            var direction = row.Direction switch { "c2s" => PacketDirection.ClientToServer, "s2c" => PacketDirection.ServerToClient,
                _ => throw new ConfigException($"Invalid catalog direction in '{path}': {row.Direction}") };
            if (!ushort.TryParse(row.Value, out _)) throw new ConfigException($"Invalid catalog value in '{path}': {row.Value}");
            var connection = row.Connection;
            if (connection.Length == 0)
            {
                if (row.Name.StartsWith("rcp", StringComparison.Ordinal) || row.Name.StartsWith("rsp", StringComparison.Ordinal)) continue;
                if (document.Profile == "kr-1256" && (ushort.Parse(row.Value) >= 2000 || row.Name.StartsWith("gpp", StringComparison.Ordinal))) continue;
                connection = "game";
            }
            var scopes = connection switch { "game" => new[] { OpcodeConnection.Game }, "relay" => [OpcodeConnection.Relay],
                "both" => [OpcodeConnection.Game, OpcodeConnection.Relay], _ => throw new ConfigException($"Invalid catalog connection in '{path}': {connection}") };
            foreach (var scope in scopes)
            {
                var key = (scope, direction);
                if (!result._rows.TryGetValue(key, out var rows)) result._rows[key] = rows = [];
                rows.Add(row);
            }
        }
        return result;
    }

    /// <summary>
    /// Resolves one nameless runtime slot to a candidate value. All rows of the same name must agree
    /// on one value (otherwise the candidate is ambiguous), the value must not carry a better-graded
    /// name under a peer row (otherwise the catalog conflicts with itself), and the runtime slot must
    /// be empty or <c>Unknown*</c> (otherwise the catalog would override reviewed data). The winning
    /// row is the highest-graded match, and the binding is logged as an unreviewed candidate.
    /// </summary>
    /// <returns>The candidate value, or <see langword="null"/> when the catalog has no admissible row for the name.</returns>
    /// <exception cref="ConfigException">Thrown when the candidate value is ambiguous, conflicts with
    /// another catalog name, or disagrees with a reviewed runtime binding.</exception>
    internal ushort? Resolve(OpcodeConnection connection, PacketDirection direction, string name)
    {
        if (!_rows.TryGetValue((connection, direction), out var rows)) return null;
        var matches = rows.Where(row => row.Name == name).ToArray();
        if (matches.Length == 0) return null;
        var values = matches.Select(row => ushort.Parse(row.Value)).Distinct().ToArray();
        if (values.Length != 1) throw new ConfigException($"Catalog fallback for {runtime.Profile.Id}/{connection} {direction} {name} is ambiguous: {string.Join(',', values)}.");
        var value = values[0];
        var peers = rows.Where(row => ushort.Parse(row.Value) == value).ToArray();
        var bestGrade = peers.Max(row => Grade(row.Grade));
        var bestNames = peers.Where(row => Grade(row.Grade) == bestGrade).Select(row => row.Name).Distinct().ToArray();
        if (bestNames.Length != 1 || bestNames[0] != name)
            throw new ConfigException($"Catalog fallback for {runtime.Profile.Id}/{connection} {direction} {name} conflicts with another name at {value}.");
        var view = runtime.ForConnection(connection);
        if (view.TryGet(direction, value, out var occupied) && occupied is not null && !occupied.Name.StartsWith("Unknown", StringComparison.Ordinal))
            throw new ConfigException($"Catalog fallback {name}={value} conflicts with runtime {occupied.Name} on {runtime.Profile.Id}/{connection}.");
        var selected = matches.MaxBy(row => Grade(row.Grade))!;
        logger.LogWarning("Opcode catalog candidate binding: profile={Profile} connection={Connection} direction={Direction} requested={Requested} candidate={Candidate}? opcode={Opcode} grade={Grade} name_source={NameSource} method={Method} source={Source} note={Note}; runtime name missing, existing codec only; unreviewed, opcodes.yaml unchanged.",
            runtime.Profile.Id, connection, direction, name, selected.Name, value, selected.Grade, selected.NameSource, selected.Method, path, selected.Note);
        return value;
    }

    /// <summary>
    /// Ranks the catalog's research grades, best first: reviewed/runtime (a row the review accepted or
    /// the runtime already carries), label (a client RTTI name), strong, cross (cross-build agreement),
    /// shape and shape-single (write-shape matches), candidate, ordered, weak and pair (paired
    /// request/reply guesses). Unrecognized grades score zero and are never bound.
    /// </summary>
    private static int Grade(string grade) => grade switch
    {
        "reviewed" or "runtime" => 10, "label" => 9, "strong" => 8, "cross" => 7,
        "shape" => 6, "shape-single" => 5, "candidate" => 4, "ordered" => 3, "weak" => 2, "pair" => 1, _ => 0,
    };

    private sealed class Catalog
    {
        public string Profile { get; set; } = "";
        public List<Row> Opcodes { get; set; } = [];
    }

    private sealed class Row
    {
        public string Value { get; set; } = "";
        public string Direction { get; set; } = "";
        public string Connection { get; set; } = "";
        public string Name { get; set; } = "";
        public string Grade { get; set; } = "";
        public string NameSource { get; set; } = "";
        public string Method { get; set; } = "";
        public string Note { get; set; } = "";
    }
}
