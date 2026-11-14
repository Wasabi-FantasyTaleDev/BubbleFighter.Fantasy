using System.Globalization;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>The tutorial room's numbered stages and the first-clear gift of each, by map key.</summary>
/// <param name="Numbers">MapMeta <c>stageNum</c> by map key.</param>
/// <param name="Gifts">MapMeta <c>IsItemAward</c> (a shop Stock or StockSet id) by map key, for stages that name one.</param>
internal sealed record TutorialStages(IReadOnlyDictionary<string, int> Numbers, IReadOnlyDictionary<string, int> Gifts);

/// <summary>
/// The tutorial room's numbered stages from the client's <c>ServerShared/MapMeta.xml</c>: each
/// <c>isSinglePlayTutorial</c> row with a <c>stageNum</c> above 0, by map key. The room lists cleared stages by that
/// number (kr-812 <c>sub_6B3AC0</c>); the prologue's 0 is not a listed stage. Only these few numbers are kept, with
/// the stage's <c>IsItemAward</c>. The kr-812 client never names that column; it describes the server's first-clear
/// gift ("when cleared, gift item (StockID, StockSetID)" in the kr-812 workbook's note).
/// </summary>
internal static class TutorialStageTable
{
    /// <summary>Reads the stage numbers and gifts from <paramref name="store"/>.</summary>
    /// <param name="store">The build's client data.</param>
    /// <returns>Stage numbers and gifts by map key, compared without regard to case.</returns>
    internal static TutorialStages Load(IClientDataStore store) =>
        // The few numbers come out of the whole map table; a store with compiled client data keeps just them.
        CompiledClientData.Load(store, CompiledTutorialStages.Instance, "", Build);

    private static TutorialStages Build(IClientDataStore store)
    {
        var meta = MapMetaTable.Parse(store.Read(MapMetaTable.Path));
        var stages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var gifts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in meta.Maps)
        {
            if (!meta.IsSet(map, "isSinglePlayTutorial")
                || !int.TryParse(meta.Get(map, "stageNum"), NumberStyles.None, CultureInfo.InvariantCulture, out var stage) || stage <= 0)
                continue;
            stages[map] = stage;
            if (int.TryParse(meta.Get(map, "IsItemAward"), NumberStyles.None, CultureInfo.InvariantCulture, out var gift) && gift > 0)
                gifts[map] = gift;
        }
        return new TutorialStages(stages, gifts);
    }
}

/// <summary>
/// The tutorial stages in the build's compiled client data: the stage numbers, then the gifts, each as a count and
/// its map keys with their numbers in ordinal key order. Keys keep their letter case and compare without it.
/// </summary>
internal static class CompiledTutorialStages
{
    internal static readonly ICompiledCatalog<TutorialStages> Instance =
        new PackedCatalog<TutorialStages, KeyValuePair<string, int>[][]>("tutorial-stages",
            stages => [Pairs(stages.Numbers), Pairs(stages.Gifts)],
            packed => packed.Length == 2 ? new TutorialStages(Table(packed[0]), Table(packed[1]))
                : throw new InvalidDataException("The compiled tutorial stages are not two tables."));

    private static KeyValuePair<string, int>[] Pairs(IReadOnlyDictionary<string, int> table) =>
        table.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();

    private static Dictionary<string, int> Table(KeyValuePair<string, int>[]? pairs)
    {
        if (pairs is null)
            throw new InvalidDataException("The compiled tutorial stages lack a table.");
        var table = new Dictionary<string, int>(pairs.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (map, number) in pairs)
            if (map is null || !table.TryAdd(map, number))
                throw new InvalidDataException($"The compiled tutorial stages repeat or lack a map key ('{map}').");
        return table;
    }
}
