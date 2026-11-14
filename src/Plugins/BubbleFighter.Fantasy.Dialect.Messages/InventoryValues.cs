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
    /// <summary>Gets the article-list wrapper, which follows the common envelope unless the build overrides it.</summary>
    internal string ArticleEnvelope { get; init; } = Envelope;

    /// <summary>Gets whether this exact build admits ordinary equipment ownership to its Article table.</summary>
    internal bool OrdinaryArticlesAdmitted { get; init; }

    /// <summary>Gets whether this exact build reads ordinary ownership expiry as relative day-double bits.</summary>
    internal bool OrdinaryExpiryAdmitted { get; init; }

    /// <summary>The plain body.</summary>
    internal const string Plain = "plain";

    /// <summary>The <c>u32</c> compressed length followed by one zlib stream.</summary>
    internal const string Zlib = "zlib";

    /// <summary>Ascending item id, empty rows dropped on a restatement, connection-relative day counts.</summary>
    internal const string Ranked = "ranked";

    /// <summary>The stored rows in stored order, with their own identity fields.</summary>
    internal const string Mirror = "mirror";

    /// <summary>The full sequence has at most one bag, equipped, Article and cash frame.</summary>
    internal const int MaxPublicationFrames = 4;

    private static readonly string[] LegacyReplaced = ["bag"];
    private static readonly string[] LegacyPurchase = ["slots"];
    private static readonly string[] LegacyChanged = ["changed"];

    private static readonly HashSet<string> Keys =
        new(StringComparer.Ordinal) { "envelope", "article_envelope", "ordinary_article_builds", "ordinary_expiry_builds", "rows", "replaced", "purchase", "changed" };
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
        var articleEnvelope = Text(map, "article_envelope", envelope, where);
        if (articleEnvelope is not (Plain or Zlib))
            throw new ConfigException($"{where}.article_envelope must be '{Plain}' or '{Zlib}'.");
        var rows = Text(map, "rows", Ranked, where);
        if (rows is not (Ranked or Mirror))
            throw new ConfigException($"{where}.rows must be '{Ranked}' or '{Mirror}'.");
        var ordinaryExpiry = AdmitsBuild(map, tables, build, "ordinary_expiry_builds", where);
        if (ordinaryExpiry && rows != Mirror)
            throw new ConfigException($"{where}.ordinary_expiry_builds requires mirrored inventory rows.");
        return new(envelope, rows,
            Entries(map, "replaced", LegacyReplaced, ReplacedEntries, where),
            Entries(map, "purchase", LegacyPurchase, PurchaseEntries, where),
            Entries(map, "changed", LegacyChanged, ChangedEntries, where))
        {
            ArticleEnvelope = articleEnvelope,
            OrdinaryArticlesAdmitted = AdmitsBuild(map, tables, build, "ordinary_article_builds", where),
            OrdinaryExpiryAdmitted = ordinaryExpiry,
        };
    }

    private static bool AdmitsBuild(Dictionary<string, object?> map, PacketTables tables, string build, string key, string where)
    {
        if (!map.TryGetValue(key, out var raw)) return false;
        if (raw is not string[] admitted || admitted.Distinct(StringComparer.Ordinal).Count() != admitted.Length
            || admitted.Any(name => !tables.Builds.Contains(name, StringComparer.Ordinal)))
            throw new ConfigException($"{where}.{key} must list distinct known builds.");
        return admitted.Contains(build, StringComparer.Ordinal);
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
