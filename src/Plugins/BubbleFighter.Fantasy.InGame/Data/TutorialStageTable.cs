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
    internal static TutorialStages Load(IClientDataStore store)
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
