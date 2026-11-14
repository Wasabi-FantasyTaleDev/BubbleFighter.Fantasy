using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Resolves explicitly configured localized room-mode names without changing native numeric mode identities.</summary>
internal sealed class RoomModeNames
{
    private readonly Dictionary<string, int> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a format-one mode table and rejects duplicate or ambiguous names before the row activates.</summary>
    internal static RoomModeNames? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement, path);
    }

    /// <summary>Resolves a complete whitespace-free Unicode name token using ordinal casing.</summary>
    internal bool TryResolve(string token, out int modeId)
    {
        modeId = 0;
        return token.Length != 0 && !token.Any(char.IsWhiteSpace) && !token.Any(char.IsControl)
            && _aliases.TryGetValue(token, out modeId);
    }

    /// <summary>Parses the same table used by the mode selector; descriptive native names are not implicit aliases.</summary>
    internal static RoomModeNames Parse(JsonElement root, string source)
    {
        var catalog = new RoomModeNames();
        RequireObject(root, source);
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number
            || !format.TryGetInt32(out var version) || version != 1
            || !root.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Array)
            throw Invalid(source, "expected format 1 and a modes array");
        var ids = new HashSet<int>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in modes.EnumerateArray())
        {
            RequireObject(mode, source);
            if (!mode.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out var id)
                || id is < RoomModeTag.MinModeId or > RoomModeTag.MaxModeId || !ids.Add(id))
                throw Invalid(source, "mode ids must be distinct integers from 1 to 65535");
            if (!mode.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String
                || keyValue.GetString() is not { Length: > 0 } key || !keys.Add(key))
                throw Invalid(source, $"mode {id} needs a distinct nonempty key");
            if (!mode.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Object
                || !mode.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                throw Invalid(source, $"mode {id} needs names and tokens objects");
            RequireObject(names, source);
            RequireObject(tokens, source);
            var languages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names.EnumerateObject())
            {
                if (name.Name.Length == 0 || name.Value.ValueKind != JsonValueKind.String)
                    throw Invalid(source, $"mode {id} names need nonempty language keys and text values");
                languages.Add(name.Name);
                var compact = Compact(name.Value.GetString()!, source);
                if (!tokens.TryGetProperty(name.Name, out var token) || token.ValueKind != JsonValueKind.String
                    || !string.Equals(token.GetString(), compact, StringComparison.Ordinal))
                    throw Invalid(source, $"mode {id} token '{name.Name}' must be its name without whitespace");
                catalog.Add(compact, id, source);
            }
            if (languages.Count == 0 || tokens.EnumerateObject().Any(token => !languages.Contains(token.Name)))
                throw Invalid(source, $"mode {id} names and tokens must have the same nonempty language set");
            if (mode.TryGetProperty("aliases", out var aliases))
            {
                if (aliases.ValueKind != JsonValueKind.Array) throw Invalid(source, $"mode {id} aliases must be an array");
                foreach (var alias in aliases.EnumerateArray())
                {
                    if (alias.ValueKind != JsonValueKind.String) throw Invalid(source, $"mode {id} aliases must be text");
                    var text = alias.GetString()!;
                    if (text.Any(char.IsWhiteSpace)) throw Invalid(source, $"mode {id} aliases cannot contain whitespace");
                    catalog.Add(Compact(text, source), id, source);
                }
            }
        }
        if (ids.Count == 0) throw Invalid(source, "at least one mode is required");
        return catalog;
    }

    private static string Compact(string text, string source)
    {
        if (text.Contains('#') || text.Any(char.IsControl))
            throw Invalid(source, "mode names cannot contain hash delimiters or control characters");
        var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length == 0 || compact.All(char.IsDigit))
            throw Invalid(source, "mode names must be nonempty and cannot replace numeric tags");
        return compact;
    }

    private void Add(string token, int id, string source)
    {
        if (_aliases.TryGetValue(token, out var previous) && previous != id)
            throw Invalid(source, $"name '{token}' is ambiguous between modes {previous.ToString(CultureInfo.InvariantCulture)} and {id.ToString(CultureInfo.InvariantCulture)}");
        _aliases[token] = id;
    }

    private static void RequireObject(JsonElement value, string source)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid(source, "expected an object");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!keys.Add(property.Name)) throw Invalid(source, $"duplicate property '{property.Name}'");
    }

    private static ConfigException Invalid(string source, string reason) => new($"Invalid room mode name table '{source}': {reason}.");
}
