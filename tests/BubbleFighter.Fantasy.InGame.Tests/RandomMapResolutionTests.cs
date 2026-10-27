using System.Text;
using BubbleFighter.Fantasy.RoomCore;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public class RandomMapResolutionTests
{
    private const string InUse = "사용함";

    private static MapMetaBlob Blob(string unknown44, string unknown68, string unknown92) =>
        MapMetaBlob.Empty() with { Unknown44 = unknown44, Unknown68 = unknown68, Unknown92 = unknown92 };

    // A sideways MapMeta sheet: one field per row, one map per column (the shipped layout).
    private static string MapMeta(params (string Field, string[] Values)[] rows)
    {
        static string Row(string field, IEnumerable<string> values) =>
            "<Row><Cell><Data ss:Type=\"String\">" + field + "</Data></Cell><Cell><Data ss:Type=\"String\">desc</Data></Cell>"
            + string.Concat(values.Select(v => "<Cell><Data ss:Type=\"String\">" + v + "</Data></Cell>")) + "</Row>";
        var maps = new[] { "rel_Meta_A", "rel_Meta_B", "rel_Meta_Unreleased", "npc_Village07_Easy01", "rel_Village07_Easy02" };
        return "<?xml version=\"1.0\"?><Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" "
            + "xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"><Worksheet ss:Name=\"MapMeta\"><Table>"
            + "<Row><Cell><Data ss:Type=\"String\">MapMeta:HorzTable</Data></Cell></Row>"
            + Row("[Field]", maps)
            + string.Concat(rows.Select(r => Row(r.Field, r.Values)))
            + "</Table></Worksheet></Workbook>";
    }

    private static MapCatalog Catalog() => MapCatalog.Load(new Store(new Dictionary<string, string>
    {
        [MapCatalog.HotRandomListPath] = $"Mode\tMapKey\tWeight\tUsable\tComment\n"
            + $"wgtPvp\trel_Desert01_Oneway\t1\t{InUse}\t\n"
            + $"wgtPvp\trel_Forest01_Hill\t3\t{InUse}\t\n"
            + "wgtPvp\trel_Unused\t1\tno\t\n"
            + $"wgtZombie\trel_zombie_01_bomb\t1\t{InUse}\t\n",
        [MapCatalog.DifficultyListPath] = $"Mode\tDifficulty\tMapKey\tUsable\tComment\n"
            + $"wgtHorde\t1\tnpc_Village07_Easy01\t{InUse}\t\n"
            + $"wgtHorde\t2\tnpc_Village07_Easy02\t{InUse}\t\n",
        [MapMetaTable.Path] = MapMeta(
            ("Directory", ["rel_Meta_A", "rel_Meta_B", "rel_Meta_Unreleased", "rel_Village07_Easy02", "rel_Village07_Easy02"]),
            ("isRelease", ["1", "1", "0", "1", "1"]),
            ("isNotemTeamModeMap", ["1", "1", "1", "0", "1"]),
            ("isZombieModeMap", ["0", "1", "0", "0", "0"]),
            ("isHordeModeMap", ["0", "0", "0", "1", "0"])),
    }));

    [Fact]
    public void HotMarkersDrawFromTheModesOwnUsablePool()
    {
        var catalog = Catalog();
        var drawn = Enumerable.Range(0, 200).Select(seed => catalog.Resolve(1, Blob("Random_Hot", "Random_Hot", ""), new Random(seed))).ToHashSet();
        Assert.Equal(new HashSet<string?> { "rel_Desert01_Oneway", "rel_Forest01_Hill" }, drawn);
        Assert.Equal("rel_zombie_01_bomb", catalog.Resolve(8, Blob("Random_Hot", "", ""), new Random(1)));
    }

    [Fact]
    public void PlainRandomDrawsReleasedMapsFlaggedForTheMode()
    {
        // sub_10EAB70: mode 1 -> isNotemTeamModeMap, mode 8 -> isZombieModeMap. Unreleased maps and
        // maps without the mode's flag (the pirate mission key) never come up.
        var catalog = Catalog();
        var pvp = Enumerable.Range(0, 300).Select(seed => catalog.Resolve(1, Blob("Random", "Random", ""), new Random(seed))).ToHashSet();
        Assert.Equal(new HashSet<string?> { "rel_Meta_A", "rel_Meta_B", "rel_Village07_Easy02" }, pvp);
        Assert.Equal("rel_Meta_B", catalog.Resolve(8, Blob("Random", "Random", ""), new Random(3)));
    }

    [Fact]
    public void ConcreteSelectionsAreKept()
    {
        Assert.Equal("rel_Village00_boomhill", Catalog().Resolve(1, Blob("rel_Village00_boomhill", "rel_Village00_boomhill", ""), new Random(1)));
    }

    [Fact]
    public void PirateBattleAlwaysPlaysADifficultyListMap()
    {
        var catalog = Catalog();
        // A normal map, or a marker, is replaced by a lowest-difficulty pirate map (mode 101, wgtHorde).
        Assert.Equal("npc_Village07_Easy01", catalog.Resolve(101, Blob("", "rel_Village01_Byway", ""), new Random(1)));
        Assert.Equal("npc_Village07_Easy01", catalog.Resolve(101, Blob("", "Random", ""), new Random(1)));
        Assert.Equal("npc_Village07_Easy02", catalog.Resolve(101, Blob("", "npc_Village07_Easy02", ""), new Random(1)));
        // The pirate map report keeps the stage key beside the mission key; the mission key wins.
        Assert.Equal("npc_Village07_Easy02", catalog.Resolve(101, Blob("npc_Village07_Easy02", "rel_Village07_Easy02", ""), new Random(1)));
        Assert.True(catalog.IsDifficultyMode(101));
        Assert.False(catalog.IsDifficultyMode(1));
    }

    [Fact]
    public void AMissionKeyLoadsItsMapMetaDirectory()
    {
        var catalog = Catalog();
        Assert.Equal("rel_Village07_Easy02", catalog.StageKey("npc_Village07_Easy01"));
        Assert.Equal("rel_Meta_A", catalog.StageKey("rel_Meta_A"));
        Assert.Equal("rel_Not_In_Meta", catalog.StageKey("rel_Not_In_Meta"));
    }

    [Fact]
    public void AModeWithoutAPoolHasNoMap()
    {
        // Mode 19 (wgtBomb) has neither a hot pool nor a flagged map in this catalog.
        Assert.Null(Catalog().Resolve(19, Blob("", "Random_Hot", ""), new Random(1)));
        Assert.Null(Catalog().Resolve(19, Blob("", "Random", ""), new Random(1)));
    }

    [Fact]
    public void UnresolvedMarkersNeverReachFirstMapKey()
    {
        Assert.Null(MapCatalog.ConcreteSelection(Blob("Random", "Random", "Random")));
        Assert.True(MapCatalog.HasSelection(Blob("", "Random", "")));
    }

    private sealed class Store(Dictionary<string, string> files) : IClientDataStore
    {
        public bool Exists(string path) => files.ContainsKey(path);

        public byte[] Read(string path) =>
            files.TryGetValue(path, out var text)
                ? Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()
                : throw new FileNotFoundException(path);
    }
}
