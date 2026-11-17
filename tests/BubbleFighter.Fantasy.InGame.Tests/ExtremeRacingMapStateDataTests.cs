using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks native vanish metadata, explicit random-selection policy and all resource bounds.</summary>
public sealed class ExtremeRacingMapStateDataTests
{
    /// <summary>The native key controls group count; the similarly named resource comment key does not override it.</summary>
    [Fact]
    public void NativeGroupKeyAndFallbackDetermineTheCompleteVector()
    {
        Assert.Equal(new[] { 2, 3 }, Read(new RacingMapStateStore()));
        var store = new RacingMapStateStore(3);
        store.Options = store.Options.Replace("AreaGroupSize=3", "AreaGroupCount=999", StringComparison.Ordinal);
        Assert.Equal(new[] { 2, 3, 4 }, Read(store));
    }

    /// <summary>A track without an active vanish stage has no route vector and does not need a world-block read.</summary>
    [Theory]
    [InlineData("[SavePoint]\nStageCount=5\n")]
    [InlineData("[VanishArea]\nProcessStage=0\n")]
    public void InactiveVanishAreaNeedsNoHelpers(string text)
    {
        var store = new RacingMapStateStore { Options = text };
        Assert.Empty(Read(store));
        Assert.Equal(1, store.Reads);
    }

    /// <summary>Native track shapes cover both the inactive first map and every active second-map route and helper.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTrackShapesReadEveryActiveGroup(bool active)
    {
        var store = new RacingMapStateStore { Options = "[SavePoint]\nStageCount=5\n", Helpers = [] };
        if (active)
        {
            store.Options += "[VanishArea]\nProcessStage=2\nAreaGroupCount=3\n";
            for (var group = 0; group < 3; group++)
            {
                store.Options += $"TileTotalSize_{group}=49\nTileColumnSize_{group}=7\nTileRowSize_{group}=7\nAreaCount_{group}=20\n";
                for (var route = 0; route < 20; route++)
                    store.Options += $"Area_{group}_{route:00}=" + string.Join(" ", Enumerable.Range(0, 10 + 5 * group)
                        .Select(tile => (tile + route) % 49)) + "\n";
            }
            store.Helpers = [.. Enumerable.Range(0, 3).SelectMany(group => Enumerable.Range(0, 49)
                .Select(tile => ($"#eff_vanishing_{group}_{tile:00}", Vector3.Zero)))];
        }
        Assert.Equal(active ? new[] { 20, 20, 20 } : Array.Empty<int>(), Read(store));
        Assert.Equal(active ? 2 : 1, store.Reads);
        if (active)
        {
            store.Options = store.Options.Replace("Area_2_19=", "Missing_2_19=", StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => Read(store));
        }
    }

    /// <summary>The native acceptance check squares the column count even when the independently stored row count differs.</summary>
    [Fact]
    public void NativeTileTotalUsesColumnsSquared()
    {
        var store = new RacingMapStateStore();
        store.Options = store.Options.Replace("TileRowSize_0=2", "TileRowSize_0=3", StringComparison.Ordinal);
        Assert.Equal(new[] { 2, 3 }, Read(store));
    }

    /// <summary>Malformed, incomplete or incompatible groups fail before any selection can be published.</summary>
    [Theory]
    [InlineData("AreaGroupSize=2", "AreaGroupSize=0")]
    [InlineData("AreaGroupSize=2", "AreaGroupSize=33")]
    [InlineData("AreaCount_0=2", "AreaCount_0=129")]
    [InlineData("TileTotalSize_0=4", "TileTotalSize_0=3")]
    [InlineData("Area_0_01=1 3", "Area_0_01=1")]
    [InlineData("Area_0_01=1 3", "Area_0_01=1 4")]
    [InlineData("Area_0_01=1 3", "Area_0_01=1 -1")]
    [InlineData("Area_0_01=1 3", "Area_0_01=1 two")]
    [InlineData("Area_0_01=1 3", "Missing_0_01=1 3")]
    [InlineData("Area_0_01=1 3", "Area_0_01=1 3\nArea_0_01=0 2")]
    public void InvalidNativeGroupsFail(string from, string to)
    {
        var store = new RacingMapStateStore();
        store.Options = store.Options.Replace(from, to, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Read(store));
    }

    /// <summary>The resource byte limit, helper bound and exact helper names are checked before returning a usable group.</summary>
    [Fact]
    public void MissingHelpersAndResourceLimitsFail()
    {
        var missing = new RacingMapStateStore { Helpers = [] };
        Assert.Throws<InvalidDataException>(() => Read(missing));
        var duplicate = new RacingMapStateStore();
        duplicate.Helpers = [.. duplicate.Helpers, duplicate.Helpers[0]];
        Assert.Throws<InvalidDataException>(() => Read(duplicate));
        Assert.Throws<InvalidDataException>(() => Read(new(), new() { ExtremeRacingMaximumFileBytes = 8 }));
        Assert.Throws<InvalidDataException>(() => Read(new(), new() { ExtremeRacingMaximumHelpers = 1 }));
        Assert.Throws<InvalidDataException>(() => Read(new(), new() { ExtremeRacingMaximumVanishTiles = 3 }));
    }

    /// <summary>Seeded selection is repeatable while all generated native selectors remain within their per-map ranges.</summary>
    [Theory]
    [InlineData("random")]
    [InlineData("seeded")]
    public void SelectionUsesExplicitServerPolicyAndNativeBounds(string policy)
    {
        var options = new InGameOptions { ExtremeRacingMapStateSelection = policy, ExtremeRacingMapStateSeed = policy == "seeded" ? 72 : null };
        var first = ExtremeRacingMapData.SelectMapState(new[] { 2, 3, 4 }, options);
        Assert.InRange(first.SecondBombPattern, 0, 9);
        Assert.InRange(first.ThirdBombPattern, 0, 9);
        Assert.InRange(first.StoneSeed, 0, int.MaxValue - 1);
        Assert.Equal(3, first.VanishSelections.Count);
        for (var group = 0; group < 3; group++) Assert.InRange(first.VanishSelections[group], 0, group + 1);
        if (policy == "seeded")
        {
            var again = ExtremeRacingMapData.SelectMapState(new[] { 2, 3, 4 }, options);
            Assert.Equal(first.SecondBombPattern, again.SecondBombPattern);
            Assert.Equal(first.ThirdBombPattern, again.ThirdBombPattern);
            Assert.Equal(first.StoneSeed, again.StoneSeed);
            Assert.Equal(first.VanishSelections, again.VanishSelections);
        }
    }

    /// <summary>Ambiguous or incomplete RNG policy fails through the production configuration binder.</summary>
    [Theory]
    [InlineData("random", 1)]
    [InlineData("seeded", null)]
    [InlineData("seeded", -1)]
    [InlineData("unspecified", null)]
    public void InvalidSelectionPolicyFailsBinding(string policy, int? seed)
    {
        var row = new PluginRow { Id = "ingame", Plugin = "ingame", Source = "test", Config = new Dictionary<string, object?>
            { ["extreme_racing_map_state_selection"] = policy, ["extreme_racing_map_state_seed"] = seed } };
        Assert.Throws<ConfigException>(() => new PluginConfig(row).Bind<InGameOptions>());
    }

    private static int[] Read(RacingMapStateStore store, InGameOptions? options = null) =>
        ExtremeRacingMapData.ReadVanishRouteCounts(store, "map", options ?? new());
}

internal sealed class RacingMapStateStore : IClientDataStore
{
    internal RacingMapStateStore(int groups = 2)
    {
        Options = "[SavePoint]\nStageCount=5\n[VanishArea]\nProcessStage=2\nAreaGroupSize=" + groups + "\n";
        for (var group = 0; group < groups; group++)
        {
            Options += $"TileTotalSize_{group}=4\nTileColumnSize_{group}=2\nTileRowSize_{group}=2\nAreaCount_{group}={group + 2}\n";
            for (var route = 0; route < group + 2; route++) Options += $"Area_{group}_{route:00}={(route % 2 == 0 ? "0 2" : "1 3")}\n";
        }
        Helpers = [.. Enumerable.Range(0, groups).SelectMany(group => Enumerable.Range(0, 4)
            .Select(tile => ($"#eff_vanishing_{group}_{tile:00}", Vector3.Zero)))];
    }
    internal string Options { get; set; }
    internal (string Name, Vector3 Position)[] Helpers { get; set; }
    internal int Reads { get; private set; }
    public bool Exists(string path) => path is "Maps/map/option.ini" or "Maps/map/track.wb";
    public byte[] Read(string path) => throw new InvalidOperationException("A bounded read is required.");
    public byte[] Read(string path, int maximumBytes)
    {
        Reads++;
        var bytes = path switch
        {
            "Maps/map/option.ini" => Encoding.ASCII.GetBytes(Options),
            "Maps/map/track.wb" => WorldBlockFixture.Create(Helpers),
            _ => throw new FileNotFoundException(path),
        };
        return bytes.Length <= maximumBytes ? bytes : throw new InvalidDataException("Synthetic map exceeds the resource bound.");
    }
}
