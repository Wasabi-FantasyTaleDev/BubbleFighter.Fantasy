using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Wire;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// The map pools a room's random selection is drawn from, read from the client's own tables in
/// <c>ServerShared</c>: <c>HotRandomMapList.csv</c> (mode widget, map key, weight, usable),
/// <c>HotRandomDifficultyMapList.csv</c> (the pirate-battle modes' maps by difficulty) and
/// <c>MapMeta.xml</c> (per map: its stage <c>Directory</c>, <c>isRelease</c> and one
/// <c>is...</c> flag per mode). A map is playable in a mode when its flag for that mode is set:
/// <c>sub_10EAB70</c> names the flag for each mode id and the create path <c>sub_9A0880</c>
/// checks it for the chosen map. The plain <c>Random</c> marker draws from every released map
/// whose flag is set, <c>Random_Hot</c> from the hot list. The room keeps the marker it was given
/// and a concrete map is drawn at every start: S2C 505 echoes the
/// markers and S2C 510 carries a map from the room mode's pool.
/// </summary>
internal sealed class MapCatalog
{
    /// <summary>Client path of the hot random map list.</summary>
    internal const string HotRandomListPath = "ServerShared/HotRandomMapList.csv";

    /// <summary>Client path of the difficulty map list.</summary>
    internal const string DifficultyListPath = "ServerShared/HotRandomDifficultyMapList.csv";

    // "사용함" (in use) in the tables' Usable column.
    private const string Usable = "사용함";

    /// <summary>
    /// Mode id to the <c>MapMeta</c> flag that marks a map playable in that mode, from the
    /// client's switch <c>sub_10EAB70</c> (modes it does not list have no flag).
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, string> ModeMapFlags = new Dictionary<int, string>
    {
        [1] = "isNotemTeamModeMap", [14] = "isNotemTeamModeMap", [27] = "isNotemTeamModeMap", [37] = "isNotemTeamModeMap",
        [3] = "isItemTeamModeMap", [5] = "isStarModeMap", [6] = "isStarModeMap", [8] = "isZombieModeMap",
        [43] = "isZombieModeMap", [11] = "isSoccerEvent", [12] = "isSniperModeMap", [13] = "isSinglePvpModeMap",
        [15] = "isSinglePlayTutorial", [103] = "isSinglePlayTutorial", [16] = "isZombiePveModeMap",
        [18] = "isSurvivalSniperModeMap", [19] = "isBombmode", [20] = "isCaptainModeMap", [21] = "isBFRunner",
        [23] = "isArea32", [24] = "isExtremeRacing", [25] = "isSuddenSurvival", [26] = "isWeaponMasterModeMap",
        [28] = "isZombieGhostModeMap", [29] = "isControlPointModeMap", [30] = "isFinalZone", [31] = "isShotgunModeMap",
        [32] = "isZombieEscapeModeMap", [33] = "isAllPangModeMap", [34] = "isNotemPracticeModeMap",
        [36] = "isNotemPracticeModeMap", [35] = "isJumpJumpModeMap", [38] = "isNotemCustomAiWarModeMap",
        [39] = "isTeamSurvivalCustomAiWarMap", [40] = "isTrainingRoom", [41] = "isBnB", [42] = "isTopViewSurvival",
        [44] = "isSimpleSuddenSurvival", [45] = "isZombieSurvival", [101] = "isHordeModeMap", [102] = "isHordeZHEvent",
        [106] = "isPirateAssault",
    };

    /// <summary>
    /// Mode id to mode widget name, from the client's mode switch <c>sub_1134B50</c> (the widget
    /// names are the exact strings the client binds; the tables' <c>Mode</c> column uses them).
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, string> ModeWidgets = new Dictionary<int, string>
    {
        [1] = "wgtPvp", [3] = "wgtItem", [5] = "wgtPvpStar", [6] = "wgtPvpStarTeam", [7] = "wgtEventLucci",
        [8] = "wgtZombie", [9] = "wgtNPC", [10] = "wgtConquerTeam", [11] = "wgtEventSoccer", [12] = "wgtSniper",
        [13] = "wgtSinglePvp", [14] = "wgtTeamSurvival", [15] = "wgtSinglePlay", [17] = "wgtEventMole",
        [18] = "wgtSniperTeamSurvival", [19] = "wgtBomb", [20] = "wgtCaptain", [21] = "wgtBFRunner",
        [23] = "wgtArea32", [24] = "wgtExtremeRacing", [25] = "wgtSuddenSurvival", [26] = "wgtWeaponMaster",
        [27] = "wgtSnowfight", [28] = "wgtZombieGhost", [29] = "wgtControlPoint", [30] = "wgtFinalZone",
        [31] = "wgtShotgun", [32] = "wgtZombieEscape", [33] = "wgtAllPang", [34] = "wgtNoItemPractice",
        [35] = "wgtJumpJump", [36] = "wgtNoItemTutorial", [37] = "wgtPsionic", [38] = "wgtNoItemCustomAiWar",
        [39] = "wgtTeamSurvivalCustomAiWar", [40] = "wgtTrainingRoom", [41] = "wgtBnB", [42] = "wgtTopViewSurvival",
        [43] = "wgtZombieOrigin", [44] = "wgtSimpleSuddenSurvival", [45] = "wgtZombieSurvival",
        [101] = "wgtHorde", [102] = "wgtHordeZHE", [103] = "wgtHordeTutor", [105] = "wgtZombiePVEPrologue",
        [106] = "wgtPirateAssaultSeason3",
    };

    private readonly Dictionary<string, List<(string Map, int Weight)>> _hot;
    private readonly Dictionary<string, SortedDictionary<int, List<string>>> _byDifficulty;
    private readonly MapMetaTable _meta;

    private MapCatalog(Dictionary<string, List<(string, int)>> hot,
        Dictionary<string, SortedDictionary<int, List<string>>> byDifficulty, MapMetaTable meta)
    {
        _hot = hot;
        _byDifficulty = byDifficulty;
        _meta = meta;
    }

    /// <summary>Gets how many modes have a hot random pool.</summary>
    internal int HotModeCount => _hot.Count;

    /// <summary>Gets how many modes draw from the difficulty list.</summary>
    internal int DifficultyModeCount => _byDifficulty.Count;

    /// <summary>Gets how many maps <c>MapMeta</c> describes.</summary>
    internal int MapCount => _meta.Maps.Count();

    /// <summary>Loads the random tables and <c>MapMeta</c> from the client data. A missing table is an error.</summary>
    internal static MapCatalog Load(IClientDataStore store)
    {
        var hot = new Dictionary<string, List<(string, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(store.Read(HotRandomListPath), HotRandomListPath, "Mode", "MapKey", "Weight", "Usable"))
        {
            if (row[3] != Usable)
                continue;
            if (!int.TryParse(row[2], out var weight) || weight <= 0)
                throw new InvalidDataException($"{HotRandomListPath}: map '{row[1]}' has weight '{row[2]}'.");
            Pool(hot, row[0]).Add((row[1], weight));
        }

        var byDifficulty = new Dictionary<string, SortedDictionary<int, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(store.Read(DifficultyListPath), DifficultyListPath, "Mode", "Difficulty", "MapKey", "Usable"))
        {
            if (row[3] != Usable)
                continue;
            if (!int.TryParse(row[1], out var difficulty))
                throw new InvalidDataException($"{DifficultyListPath}: map '{row[2]}' has difficulty '{row[1]}'.");
            if (!byDifficulty.TryGetValue(row[0], out var levels))
                byDifficulty[row[0]] = levels = [];
            if (!levels.TryGetValue(difficulty, out var maps))
                levels[difficulty] = maps = [];
            maps.Add(row[2]);
        }

        return new MapCatalog(hot, byDifficulty, MapMetaTable.Parse(store.Read(MapMetaTable.Path)));
    }

    /// <summary>
    /// Picks the map a round of <paramref name="mode"/> is played on, from the room's stored
    /// selection. A mode that draws from the difficulty list (the pirate battle) always plays one
    /// of those maps: the selected one when it is on the list, otherwise one of the lowest
    /// difficulty. Otherwise a concrete selection is kept, a <c>Random</c> marker draws from the
    /// released maps whose <c>MapMeta</c> flag for the mode is set and a <c>Random_Hot</c>
    /// marker from the mode's hot pool (weighted). Returns <see langword="null"/> when nothing fits.
    /// </summary>
    internal string? Resolve(int mode, MapMetaBlob selection, Random random)
    {
        var selected = ConcreteSelection(selection);
        ModeWidgets.TryGetValue(mode, out var widget);
        if (widget is not null && _byDifficulty.TryGetValue(widget, out var levels))
        {
            // Any slot may carry the mission key: the pirate map report stores the stage key
            // beside it (C2S 513 key rel_..., scene npc_...).
            foreach (var slot in new[] { selection.Unknown68, selection.Unknown44, selection.Unknown92, selection.Unknown116 })
            {
                if (!string.IsNullOrEmpty(slot) && levels.Values.Any(maps => maps.Contains(slot, StringComparer.OrdinalIgnoreCase)))
                    return slot;
            }
            var lowest = levels.First().Value;
            return lowest[random.Next(lowest.Count)];
        }
        if (selected is not null)
            return selected;
        if (!IsHotMarker(selection))
        {
            var pool = ModePool(mode);
            return pool.Count == 0 ? null : pool[random.Next(pool.Count)];
        }
        if (widget is null || !_hot.TryGetValue(widget, out var hot) || hot.Count == 0)
            return null;
        var pick = random.Next(hot.Sum(entry => entry.Weight));
        foreach (var (map, weight) in hot)
        {
            if (pick < weight)
                return map;
            pick -= weight;
        }
        return hot[^1].Map;
    }

    /// <summary>
    /// The released maps playable in <paramref name="mode"/>: <c>isRelease</c> and the mode's
    /// <c>sub_10EAB70</c> flag both set in <c>MapMeta</c>. Empty for a mode without a flag.
    /// </summary>
    internal IReadOnlyList<string> ModePool(int mode) =>
        ModeMapFlags.TryGetValue(mode, out var flag)
            ? _meta.Maps.Where(map => _meta.IsSet(map, "isRelease") && _meta.IsSet(map, flag)).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Whether <paramref name="mode"/> plays maps from the difficulty list (the pirate battle).</summary>
    internal bool IsDifficultyMode(int mode) =>
        ModeWidgets.TryGetValue(mode, out var widget) && _byDifficulty.ContainsKey(widget);

    /// <summary>
    /// The stage a map loads: its <c>MapMeta</c> <c>Directory</c> (a mission key such as
    /// <c>npc_Village07_Easy01</c> plays on <c>rel_Village07_Easy02</c>), or the key itself.
    /// </summary>
    internal string StageKey(string map)
    {
        var directory = _meta.Get(map, "Directory");
        return directory.Length > 0 ? directory : map;
    }

    /// <summary>The first stored map slot that holds a real key rather than a random marker.</summary>
    internal static string? ConcreteSelection(MapMetaBlob map)
    {
        foreach (var candidate in new[] { map.Unknown68, map.Unknown44, map.Unknown92, map.Unknown116 })
        {
            if (!string.IsNullOrEmpty(candidate) && !Handlers.RoomPush.RandomMapMarkers.Contains(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Whether the selection carries any map at all, concrete or a random marker.</summary>
    internal static bool HasSelection(MapMetaBlob map) =>
        new[] { map.Unknown68, map.Unknown44, map.Unknown92, map.Unknown116 }.Any(slot => !string.IsNullOrEmpty(slot));

    private static bool IsHotMarker(MapMetaBlob map) =>
        new[] { map.Unknown68, map.Unknown44, map.Unknown92 }.Contains("Random_Hot", StringComparer.Ordinal);

    private static List<(string, int)> Pool(Dictionary<string, List<(string, int)>> pools, string widget)
    {
        if (!pools.TryGetValue(widget, out var pool))
            pools[widget] = pool = [];
        return pool;
    }

    // The tables are UTF-16 (little-endian, with a byte-order mark), tab-separated, one header row.
    private static IEnumerable<string[]> Rows(byte[] bytes, string path, params string[] columns)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine()?.Split('\t') ?? throw new InvalidDataException($"{path} is empty.");
        var indexes = columns.Select(column =>
        {
            var index = Array.FindIndex(header, h => h.Trim() == column);
            return index >= 0 ? index : throw new InvalidDataException($"{path} has no '{column}' column.");
        }).ToArray();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim().Length == 0)
                continue;
            var cells = line.Split('\t');
            yield return indexes.Select(i => i < cells.Length ? cells[i].Trim() : string.Empty).ToArray();
        }
    }
}
