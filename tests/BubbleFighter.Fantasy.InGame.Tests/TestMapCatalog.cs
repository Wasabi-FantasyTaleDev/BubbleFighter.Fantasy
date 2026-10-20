using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>A small client-data store holding just enough of the map tables for start tests.</summary>
internal static class TestMapCatalog
{
    private const string InUse = "사용함";

    /// <summary>One pirate mission (npc_Village07_Easy01 on stage rel_Village07_Easy02) and one PvP map.</summary>
    internal static MapCatalog Pirate() => MapCatalog.Load(new Store(new Dictionary<string, string>
    {
        [MapCatalog.HotRandomListPath] = $"Mode\tMapKey\tWeight\tUsable\n"
            + $"wgtPvp\trel_Village07_Easy02\t1\t{InUse}\n",
        [MapCatalog.DifficultyListPath] = $"Mode\tDifficulty\tMapKey\tUsable\n"
            + $"wgtHorde\t1\tnpc_Village07_Easy01\t{InUse}\n",
        [MapMetaTable.Path] = "<?xml version=\"1.0\"?><Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" "
            + "xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"><Worksheet ss:Name=\"MapMeta\"><Table>"
            + Row("[Field]", "npc_Village07_Easy01", "rel_Village07_Easy02")
            + Row("Directory", "rel_Village07_Easy02", "rel_Village07_Easy02")
            + Row("isRelease", "1", "1")
            + Row("isNotemTeamModeMap", "0", "1")
            + Row("isHordeModeMap", "1", "0")
            + "</Table></Worksheet></Workbook>",
    }));

    private static string Row(string field, params string[] values) =>
        "<Row><Cell><Data>" + field + "</Data></Cell><Cell><Data>desc</Data></Cell>"
        + string.Concat(values.Select(v => "<Cell><Data>" + v + "</Data></Cell>")) + "</Row>";

    private sealed class Store(Dictionary<string, string> files) : IClientDataStore
    {
        public bool Exists(string path) => files.ContainsKey(path);

        public byte[] Read(string path) =>
            files.TryGetValue(path, out var text)
                ? Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()
                : throw new FileNotFoundException(path);
    }
}
