using System.Globalization;
using System.Security.Cryptography;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Resolves an actor's local ordinary-defence roll from the build's own <c>ShopItemSpec</c> rows, the way the client
/// aggregates the worn set into its attribute block, and issues the round's 1000-byte table for that roll.
/// </summary>
internal sealed class DefenceSeedCatalog
{
    private enum Effect
    {
        Minimum, Maximum,
        RcThreshold, RcMinimum, RcMaximum, RcCount,
        KrCount, KrThreshold, KrMinimum,
        BbCount, BbThreshold, BbMinimum, BbMaximum,
    }

    private sealed record ItemEffect(Effect Kind, float Amount);

    // The client's own effect names (kr-812 sub_993C50 registers them as ids 18/19, 67-70, 94-96 and 98-101) and how
    // kr-812 sub_9961C0 applies each: float sums, integer sums, or an integer assignment of the truncated value.
    private static readonly Dictionary<string, (string Family, Effect Kind)> Names = new(StringComparer.Ordinal)
    {
        ["damagedDecRatioMin"] = ("ordinary", Effect.Minimum),
        ["damagedDecRatioMax"] = ("ordinary", Effect.Maximum),
        ["RCequipCheckValue"] = ("rc", Effect.RcThreshold),
        ["RCdamagedDecRatioMin"] = ("rc", Effect.RcMinimum),
        ["RCdamagedDecRatioMax"] = ("rc", Effect.RcMaximum),
        ["RCequipValue"] = ("rc", Effect.RcCount),
        ["KRequipValue"] = ("kr", Effect.KrCount),
        ["KRequipCheckValue"] = ("kr", Effect.KrThreshold),
        ["KRdamagedDecRatioMin"] = ("kr", Effect.KrMinimum),
        ["BBequipValue"] = ("bb", Effect.BbCount),
        ["BBequipCheckValue"] = ("bb", Effect.BbThreshold),
        ["BBdamagedDecRatioMin"] = ("bb", Effect.BbMinimum),
        ["BBdamagedDecRatioMax"] = ("bb", Effect.BbMaximum),
    };

    private static readonly string[] Families = ["rc", "kr", "bb"];

    private readonly IReadOnlyDictionary<int, ItemEffect[]> _effects;
    private readonly bool _singlePrecision;
    private readonly int _rcThreshold;
    private readonly int[] _krOwners;
    private readonly int[] _bbOwners;
    private readonly HashSet<int> _skipped;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    private DefenceSeedCatalog(IReadOnlyDictionary<int, ItemEffect[]> effects, bool singlePrecision, int rcThreshold,
        int[] krOwners, int[] bbOwners, HashSet<int> skipped)
    {
        _effects = effects;
        _singlePrecision = singlePrecision;
        _rcThreshold = rcThreshold;
        _krOwners = krOwners;
        _bbOwners = bbOwners;
        _skipped = skipped;
    }

    /// <summary>Reads the build's defence rules from <c>packets/builds.yaml</c> and its effect rows from client data.</summary>
    /// <param name="data">The build's client data.</param>
    /// <param name="tables">The packet tables holding the build's <c>defence_seed</c> values.</param>
    /// <param name="build">The client build.</param>
    /// <param name="path">The client path of the item workbook.</param>
    /// <param name="maxBytes">The workbook size cap.</param>
    /// <param name="limitKey">The snake_case option that owns <paramref name="maxBytes"/>.</param>
    /// <returns>The resolved catalog.</returns>
    /// <exception cref="ConfigException">The build states no audited rules, or a defence row is malformed or bound to a
    /// channel the server cannot know.</exception>
    internal static DefenceSeedCatalog Load(IClientDataStore data, PacketTables tables, string build, string path,
        long maxBytes, string limitKey)
    {
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("defence_seed", out var raw) || raw is not Dictionary<string, object?> rules)
            throw new ConfigException($"Build '{build}' states no audited defence_seed rules in packets/builds.yaml.");
        var families = Strings(rules, "families");
        if (families.Any(family => !Families.Contains(family, StringComparer.Ordinal)) || families.Distinct().Count() != families.Length)
            throw new ConfigException($"Build '{build}' defence_seed.families must name distinct families of {string.Join(", ", Families)}.");
        var precision = Text(rules, "precision");
        if (precision is not ("single" or "double"))
            throw new ConfigException($"Build '{build}' defence_seed.precision must be single or double.");
        var rcThreshold = int.Parse(Text(rules, "rc_threshold_default"), NumberStyles.None, CultureInfo.InvariantCulture);
        var krOwners = Items(rules, "kr_owner_items");
        var bbOwners = Items(rules, "bb_owner_items");
        var skipped = Items(rules, "skipped_items").ToHashSet();

        IReadOnlyList<IReadOnlyList<string>> rows;
        try
        {
            rows = SpreadsheetWorkbook.Load(WorkbookFile.FromBytes(path, data.Read(path)), maxBytes, limitKey,
                ["ShopItemSpec"])["ShopItemSpec"];
        }
        catch (Exception failure) when (failure is FileNotFoundException or InvalidOperationException or System.Xml.XmlException)
        {
            throw new ConfigException($"Defence seeds cannot read '{path}' ShopItemSpec: {failure.Message}");
        }
        var header = rows.FirstOrDefault(row => row.Contains("shopItemId") && row.Contains("buffName"))
            ?? throw new ConfigException($"'{path}' ShopItemSpec lacks the shopItemId and buffName columns.");
        int Column(string name)
        {
            var columns = header.Select((cell, index) => (cell, index)).Where(pair => pair.cell == name).ToArray();
            return columns.Length == 1 ? columns[0].index
                : throw new ConfigException($"'{path}' ShopItemSpec requires one '{name}' column.");
        }
        var itemColumn = Column("shopItemId");
        var nameColumn = Column("buffName");
        var valueColumn = Column("value");
        var channelColumn = Column("channel");
        var effects = new Dictionary<int, List<ItemEffect>>();
        foreach (var row in rows)
        {
            if (!Names.TryGetValue(Cell(row, nameColumn), out var effect)) continue;
            if (effect.Family != "ordinary" && !families.Contains(effect.Family, StringComparer.Ordinal)) continue;
            var itemText = Cell(row, itemColumn);
            if (!int.TryParse(itemText, NumberStyles.None, CultureInfo.InvariantCulture, out var item) || item <= 0)
                throw new ConfigException($"'{path}' defence row names item '{itemText}'.");
            // kr-812 sub_9961C0 applies a row only on channel 0 or the player's current channel; the server does not
            // track the client's channel id, so a channel-bound defence row has no audited value here.
            var channel = Cell(row, channelColumn);
            if (channel.Length > 0 && channel != "0")
                throw new ConfigException($"'{path}' item {item} binds a defence row to channel {channel}.");
            // The loader stores the parsed decimal as a protected float (kr-812 sub_99D810 / kr-812 sub_991700).
            var text = Cell(row, valueColumn);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || !float.IsFinite((float)parsed))
                throw new ConfigException($"'{path}' item {item} has a non-numeric defence value '{text}'.");
            if (!effects.TryGetValue(item, out var list)) effects.Add(item, list = []);
            list.Add(new(effect.Kind, (float)parsed));
        }
        return new(effects.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), precision == "single",
            rcThreshold, krOwners, bbOwners, skipped);

        string Text(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) && value is string text
            ? text : throw new ConfigException($"Build '{build}' defence_seed.{key} is missing.");
        string[] Strings(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) && value is string[] list
            ? list : throw new ConfigException($"Build '{build}' defence_seed.{key} must be a list.");
        int[] Items(Dictionary<string, object?> map, string key) => Strings(map, key).Select(text =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id
                : throw new ConfigException($"Build '{build}' defence_seed.{key} holds '{text}', not an item id.")).ToArray();
    }

    /// <summary>
    /// Resolves the local roll of a worn set: the character first, then each distinct item in ascending order, as
    /// kr-812 sub_986F60 visits them; set bonuses as kr-812 sub_986B80 finalizes them; the RC bonus and the endpoint
    /// truncation as the consumer applies them at roll time (kr-812 sub_557230).
    /// </summary>
    /// <param name="equipment">The accepted worn set, or null when the room carries none.</param>
    /// <returns>The roll's integer percents.</returns>
    /// <exception cref="InvalidOperationException">The roll does not fit the native byte table.</exception>
    internal DefencePercentRange Range(MatchSeedEquipment? equipment)
    {
        // The snapshot is already distinct, ordered and bounded by the inventory's slot count (MatchSeedEquipment).
        var worn = equipment?.WornItemIds ?? [];
        // kr-812 sub_9A5800 resets every sum and threshold to zero except the RC threshold.
        float min = 0, max = 0, rcMin = 0, rcMax = 0, krMin = 0, bbMin = 0, bbMax = 0;
        int rcThreshold = _rcThreshold, rcCount = 0, krCount = 0, krThreshold = 0, bbCount = 0, bbThreshold = 0;
        foreach (var item in worn.OrderBy(id => id is >= 300000 and < 400000 ? 0 : 1).ThenBy(id => id))
        {
            // kr-812 sub_99C480 applies no rows for these characters.
            if (_skipped.Contains(item) || !_effects.TryGetValue(item, out var effects)) continue;
            foreach (var effect in effects)
            {
                switch (effect.Kind)
                {
                    case Effect.Minimum: min += effect.Amount; break;
                    case Effect.Maximum: max += effect.Amount; break;
                    case Effect.RcThreshold: rcThreshold = Truncate(effect.Amount); break;
                    case Effect.RcMinimum: rcMin += effect.Amount; break;
                    case Effect.RcMaximum: rcMax += effect.Amount; break;
                    case Effect.RcCount: rcCount = unchecked(rcCount + Truncate(effect.Amount)); break;
                    case Effect.KrCount: krCount = unchecked(krCount + Truncate(effect.Amount)); break;
                    case Effect.KrThreshold: krThreshold = Truncate(effect.Amount); break;
                    case Effect.KrMinimum: krMin += effect.Amount; break;
                    case Effect.BbCount: bbCount = unchecked(bbCount + Truncate(effect.Amount)); break;
                    case Effect.BbThreshold: bbThreshold = Truncate(effect.Amount); break;
                    case Effect.BbMinimum: bbMin += effect.Amount; break;
                    case Effect.BbMaximum: bbMax += effect.Amount; break;
                    default: throw new InvalidOperationException("Unknown defence effect.");
                }
            }
        }
        // kr-812 sub_986B80 adds the KR minimum, then the BB pair, once an owner item is worn and the count is met.
        if (_krOwners.Any(worn.Contains) && krCount >= krThreshold) min += krMin;
        if (_bbOwners.Any(worn.Contains) && bbCount >= bbThreshold)
        {
            min += bbMin;
            max += bbMax;
        }
        // kr-812 sub_557230 adds the RC pair when its count is met, then truncates both endpoints times 100.
        if (rcCount >= rcThreshold)
        {
            min += rcMin;
            max += rcMax;
        }
        var range = new DefencePercentRange(Percent(min), Percent(max));
        if (range.First < 0 || range.First + range.Count - 1 > byte.MaxValue)
            throw new InvalidOperationException($"Defence percents {range.Low}..{range.High} do not fit the native byte table.");
        return range;
    }

    /// <summary>Issues one actor's table for a round; the same actor, round and worn set always give the same bytes.</summary>
    /// <param name="roomId">The room.</param>
    /// <param name="generation">The room start generation.</param>
    /// <param name="entityId">The actor's native scene entity.</param>
    /// <param name="equipment">The actor's accepted worn set.</param>
    /// <returns>The 1000 table bytes.</returns>
    internal byte[] Table(int roomId, int generation, int entityId, MatchSeedEquipment? equipment)
    {
        Span<byte> message = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(message, roomId);
        BinaryPrimitives.WriteInt32LittleEndian(message[4..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(message[8..], entityId);
        Span<byte> seed = stackalloc byte[32];
        HMACSHA256.HashData(_secret, message, seed);
        return DefenceSeedTable.Build(Range(equipment), seed);
    }

    // Device flags determine legacy endpoint precision (kr-812 sub_ABBFE0 / cn-406 sub_9B85B0).
    // kr-1256 sub_483620 explicitly multiplies in single precision before truncating.
    private int Percent(float value)
    {
        var scaled = _singlePrecision ? value * 100.0f : value * 100.0;
        return double.IsFinite(scaled) && scaled is > int.MinValue and < int.MaxValue
            ? (int)scaled : throw new InvalidOperationException("Defence endpoint exceeds the native integer range.");
    }

    // kr-812 sub_9961C0 stores (int)value, a truncation of the parsed float.
    private static int Truncate(float value) =>
        float.IsFinite(value) && value is > int.MinValue and < int.MaxValue
            ? (int)value : throw new InvalidOperationException("Defence integer attribute exceeds the native range.");

    private static string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column].Trim() : "";
}
