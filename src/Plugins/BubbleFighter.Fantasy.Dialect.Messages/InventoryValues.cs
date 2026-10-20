using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// One build's <c>inventory</c> block of <c>packets/builds.yaml</c>: how the bag bodies are wrapped
/// and which packets each publication sends, in order. A build that states no block keeps the
/// legacy baseline: one whole-bag packet and the slots packet as the update.
/// </summary>
/// <param name="Envelope">The body wrapper: <c>plain</c> or <c>zlib</c>.</param>
/// <param name="Rows">How a stored bag becomes the wire list: <c>ranked</c> or <c>mirror</c>.</param>
/// <param name="Replaced">The packets a whole-bag restatement sends, in order.</param>
/// <param name="Purchase">The packets a post-purchase restatement sends, in order.</param>
/// <param name="Changed">The packets a changed-slot publication sends, in order.</param>
internal sealed record InventoryValues(string Envelope, string Rows, string[] Replaced, string[] Purchase, string[] Changed)
{
    /// <summary>The plain body.</summary>
    internal const string Plain = "plain";

    /// <summary>The <c>u32</c> compressed length followed by one zlib stream.</summary>
    internal const string Zlib = "zlib";

    /// <summary>Ascending item id, empty rows dropped on a restatement, connection-relative day counts.</summary>
    internal const string Ranked = "ranked";

    /// <summary>The stored rows in stored order, with their own identity fields.</summary>
    internal const string Mirror = "mirror";

    private static readonly string[] LegacyReplaced = ["bag"];
    private static readonly string[] LegacyPurchase = ["slots"];
    private static readonly string[] LegacyChanged = ["changed"];

    private static readonly HashSet<string> Keys =
        new(StringComparer.Ordinal) { "envelope", "rows", "replaced", "purchase", "changed" };
    private static readonly HashSet<string> ReplacedEntries =
        new(StringComparer.Ordinal) { "bag", "equipped", "articles", "cash" };
    private static readonly HashSet<string> PurchaseEntries =
        new(StringComparer.Ordinal) { "bag", "slots" };
    private static readonly HashSet<string> ChangedEntries =
        new(StringComparer.Ordinal) { "changed" };

    /// <summary>Gets whether a publication sends the named entry.</summary>
    /// <param name="entry">The entry name.</param>
    /// <returns>Whether any of the three lists names it.</returns>
    internal bool Uses(string entry) =>
        Replaced.Contains(entry, StringComparer.Ordinal)
        || Purchase.Contains(entry, StringComparer.Ordinal)
        || Changed.Contains(entry, StringComparer.Ordinal);

    /// <summary>Reads one build's inventory block, refusing anything it cannot use.</summary>
    /// <param name="tables">The packet tables the build is named in.</param>
    /// <param name="build">The build.</param>
    /// <returns>The resolved values.</returns>
    /// <exception cref="ConfigException">Thrown for a malformed block, an unknown key or an unknown entry name.</exception>
    internal static InventoryValues Of(PacketTables tables, string build)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("inventory", out var raw) || raw is null)
            return new(Plain, Ranked, LegacyReplaced, LegacyPurchase, LegacyChanged);
        var where = $"'{tables.Directory}' build '{build}' inventory";
        if (raw is not Dictionary<string, object?> map)
            throw new ConfigException($"{where}: the value must be a mapping.");
        if (map.Keys.FirstOrDefault(key => !Keys.Contains(key)) is { } unknown)
            throw new ConfigException($"{where} has the unknown value '{unknown}'.");
        var envelope = Text(map, "envelope", Plain, where);
        if (envelope is not (Plain or Zlib))
            throw new ConfigException($"{where}.envelope must be '{Plain}' or '{Zlib}'.");
        var rows = Text(map, "rows", Ranked, where);
        if (rows is not (Ranked or Mirror))
            throw new ConfigException($"{where}.rows must be '{Ranked}' or '{Mirror}'.");
        return new(envelope, rows,
            Entries(map, "replaced", LegacyReplaced, ReplacedEntries, where),
            Entries(map, "purchase", LegacyPurchase, PurchaseEntries, where),
            Entries(map, "changed", LegacyChanged, ChangedEntries, where));
    }

    private static string Text(Dictionary<string, object?> map, string key, string fallback, string where) =>
        !map.TryGetValue(key, out var raw) || raw is null
            ? fallback
            : raw as string ?? throw new ConfigException($"{where}.{key} must be a value.");

    private static string[] Entries(Dictionary<string, object?> map, string key, string[] fallback,
        HashSet<string> allowed, string where)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
            return fallback;
        var entries = raw as string[]
            ?? throw new ConfigException($"{where}.{key} must be a list of entry names.");
        foreach (var entry in entries)
        {
            if (!allowed.Contains(entry))
                throw new ConfigException($"{where}.{key} names '{entry}'; expected one of {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
        }
        if (entries.Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ConfigException($"{where}.{key} names an entry twice.");
        return entries;
    }
}
