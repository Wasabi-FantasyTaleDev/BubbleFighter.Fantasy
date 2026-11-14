using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks bounded BnB resource loading, native RNG arithmetic, and the order of item selection.</summary>
public sealed class BnBMapDataTests
{
    /// <summary>The signed seed edge cases retain the client's wrapping initialization and float-scaled draws.</summary>
    [Theory]
    [InlineData(0, "7262,8173,7680,5581,2060,5588,9060,4421,9775,2737,2919,4673,6326,4695,9821,303")]
    [InlineData(422402801, "7530,7529,8111,2911,2945,8893,9741,4092,7629,7896,8765,8923,4426,4396,6846,9077")]
    [InlineData(int.MinValue, "2486,1107,4670,7716,6575,4327,3540,9438,1012,6424,286,2480,3201,9897,6821,6548")]
    [InlineData(int.MaxValue, "7262,8173,7680,5581,2060,5588,9060,4421,9775,2737,2919,4673,6326,4695,9821,303")]
    public void NativeRandomDrawsArePinned(int seed, string expected)
    {
        var random = new BnBRandomSource(seed);
        Assert.Equal(expected.Split(',').Select(int.Parse), Enumerable.Range(0, 16).Select(_ => random.Next(10000)));
    }

    /// <summary>The final multiplication uses float precision instead of retaining a double sample.</summary>
    [Fact]
    public void NativeRandomScalesTheRoundedFloatSample()
    {
        Assert.Equal(726243264, new BnBRandomSource(0).Next(1000000000));
    }

    /// <summary>A guaranteed static result still consumes a draw and excludes that cell from the later tile pass.</summary>
    [Fact]
    public void GuaranteedStaticItemsConsumeDrawsBeforeTileItems()
    {
        var store = BnBTestData.Store(3, 1, [1, 1, 1], [1, 0, 0]);
        var layout = JsonNode.Parse(store.Files[BnBTestData.LayoutPath])!.AsObject();
        layout["tilePresets"] = JsonNode.Parse("""[{"tileIndex":1,"totalWeight":10000,"items":[{"itemType":"fire","weight":8000}]}]""");
        store.Files[BnBTestData.LayoutPath] = JsonSerializer.SerializeToUtf8Bytes(layout);
        var map = BnBTestData.Load(store);
        Assert.Equal(new[] { BnBItemKind.Bomb, BnBItemKind.None, BnBItemKind.Fire }, map.CreateInitialItems(0));
    }

    /// <summary>An uncovered static interval leaves the cell eligible for its tile preset without a new destruction draw.</summary>
    [Fact]
    public void EmptyStaticResultFallsThroughToTilePreset()
    {
        var store = BnBTestData.Store(1, 1, [1], [1]);
        var layout = JsonNode.Parse(store.Files[BnBTestData.LayoutPath])!.AsObject();
        layout["staticPresets"] = JsonNode.Parse("""[{"staticIndex":1,"totalWeight":10000,"items":[{"itemType":"bomb","weight":1000}]}]""");
        layout["tilePresets"] = JsonNode.Parse("""[{"tileIndex":1,"totalWeight":10000,"items":[{"itemType":"roller","weight":8500}]}]""");
        store.Files[BnBTestData.LayoutPath] = JsonSerializer.SerializeToUtf8Bytes(layout);
        Assert.Equal(BnBItemKind.Roller, BnBTestData.Load(store).CreateInitialItems(0)[0]);
    }

    /// <summary>Tile identifiers include the native zero sentinel and use blockType before interpreting type.</summary>
    [Fact]
    public void TileSentinelAndBlockClassificationArePreserved()
    {
        var store = BnBTestData.Store(4, 1, [0, 1, 2, 3]);
        var map = BnBTestData.Load(store);
        Assert.Equal(new[] { BnBBlockState.Empty, BnBBlockState.Destructible, BnBBlockState.Solid, BnBBlockState.Empty },
            Enumerable.Range(0, 4).Select(map.InitialBlock));
        Assert.Equal(new[] { 1048576, 1048576 }, store.ReadLimits);
    }

    /// <summary>The explicit scene origin and JSON offset select cells without horizontal wrapping or nonfinite coordinates.</summary>
    [Fact]
    public void PositionUsesRequiredSceneBoundsAndOffset()
    {
        var store = BnBTestData.Store(3, 2);
        var json = JsonNode.Parse(store.Files[BnBTestData.MapPath])!.AsObject();
        json["offset"] = JsonSerializer.SerializeToNode(new[] { 4f, -2f, 8f });
        json["grid_unit"] = 2f;
        store.Files[BnBTestData.MapPath] = JsonSerializer.SerializeToUtf8Bytes(json);
        var map = BnBMapData.Load(store, "test", 1048576, 1024, new(10, 20));
        Assert.Equal(14f, map.MinimumX);
        Assert.Equal(20f, map.MaximumX);
        Assert.Equal(24f, map.MinimumZ);
        Assert.Equal(28f, map.MaximumZ);
        Assert.Equal(0, map.PositionToGrid(14, 0, 28));
        Assert.Equal(4, map.PositionToGrid(17, 999, 25));
        Assert.Equal(-1, map.PositionToGrid(20, 0, 28));
        Assert.Equal(-1, map.PositionToGrid(14, 0, 24));
        Assert.Equal(-1, map.PositionToGrid(13, 0, 28));
        Assert.Equal(-1, map.PositionToGrid(14, float.NaN, 28));
        Assert.Equal(-1, map.PositionToGrid(float.PositiveInfinity, 0, 28));
    }

    /// <summary>Malformed dimensions, tile references and presets fail before the round allocates its cell ledger.</summary>
    [Theory]
    [InlineData("cells")]
    [InlineData("layer")]
    [InlineData("tile")]
    [InlineData("static")]
    [InlineData("weight")]
    [InlineData("duplicate")]
    public void InvalidNativeMapDataFailsLoud(string corruption)
    {
        var store = BnBTestData.Store(2, 1);
        var map = JsonNode.Parse(store.Files[BnBTestData.MapPath])!.AsObject();
        var layout = JsonNode.Parse(store.Files[BnBTestData.LayoutPath])!.AsObject();
        switch (corruption)
        {
            case "cells": map["width"] = 2000; break;
            case "layer": map["data"] = JsonNode.Parse("[[0]]"); break;
            case "tile": map["data"] = JsonNode.Parse("[[0,99]]"); break;
            case "static": layout["staticLayer"] = JsonNode.Parse("[0]"); break;
            case "weight": layout["staticPresets"]![0]!["items"]![0]!["weight"] = 10001; break;
            case "duplicate": layout["staticPresets"]!.AsArray().Add(layout["staticPresets"]![0]!.DeepClone()); break;
        }
        store.Files[BnBTestData.MapPath] = JsonSerializer.SerializeToUtf8Bytes(map);
        store.Files[BnBTestData.LayoutPath] = JsonSerializer.SerializeToUtf8Bytes(layout);
        Assert.Throws<InvalidDataException>(() => BnBTestData.Load(store));
    }

    /// <summary>Resource limits and stage traversal checks apply before native bytes are parsed.</summary>
    [Fact]
    public void BoundsAndPathsCannotBeSilentlyDefaulted()
    {
        var store = BnBTestData.Store(1, 1);
        Assert.Throws<InvalidDataException>(() => BnBMapData.Load(store, "../test", 1048576, 1024, new(0, 0)));
        Assert.Empty(store.ReadLimits);
        Assert.Throws<InvalidDataException>(() => BnBMapData.Load(store, "test", 1048576, 1024, new(float.NaN, 0)));
        Assert.Throws<InvalidDataException>(() => BnBMapData.Load(store, "test", 1, 1024, new(0, 0)));
    }

    /// <summary>The native fixture pins all initial cells independently of the configured scene origin.</summary>
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData(0, "36AA420088B2E167A65D0C29E63F4A777F384F5E52777DAD24569055337F62AE")]
    [InlineData(422402801, "69ADC2900F5040D257021915EB24E33183B1389E86E0943B1D566EDCC14BAC20")]
    public void NativeVillageItemLayoutIsDeterministic(int seed, string expectedHash)
    {
        var store = new NativeStore();
        // The artificial origin is immaterial to item selection; this test makes no scene-position claim.
        var map = BnBMapData.Load(store, "rel_bnb01_village06", 1048576, 1024, new(-1000, 1000));
        Assert.Equal(15, map.Width);
        Assert.Equal(13, map.Height);
        var items = map.CreateInitialItems(seed).Select(item => (byte)item).ToArray();
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(items)));
        var rules = BnBRoundRules.Load(store, 1048576);
        Assert.Equal(new BnBRoundRules(3000, 500, 500, 6, 6, 6, 0.2f, 2f), rules);
    }

    /// <summary>Original scene bounds and independently stored spawn helpers pin grid orientation and float32 edge rounding.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void NativeVillageSceneCoordinatesMatchSpawnHelpers()
    {
        var store = new NativeStore();
        // kr-1256 sub_109A880 bounds TileLayer vertices; kr-1256 sub_7AEBB0 extends the grid before applying its offset.
        var map = BnBMapData.Load(store, "rel_bnb01_village06", 1048576, 1024,
            new(163.96731567382812f, 392.6912841796875f));
        Assert.Equal(0x4323F7A2, BitConverter.SingleToInt32Bits(map.MinimumX));
        Assert.Equal(0x43C8180A, BitConverter.SingleToInt32Bits(map.MaximumX));
        Assert.Equal(0x433BF785, BitConverter.SingleToInt32Bits(map.MinimumZ));
        Assert.Equal(0x43C4587C, BitConverter.SingleToInt32Bits(map.MaximumZ));
        var helpers = WorldBlockFile.LoadHelpers(store, "Maps/rel_bnb01_village06/track.wb", 1048576, 1024);
        var cells = new Dictionary<string, int>
        {
            ["#spawn_S001"] = 0, ["#spawn_S002"] = 90, ["#spawn_S003"] = 180, ["#spawn_S004"] = 186,
            ["#spawn_S005"] = 14, ["#spawn_S006"] = 8, ["#spawn_S007"] = 194, ["#spawn_S008"] = 104,
        };
        foreach (var (name, cell) in cells)
        {
            var position = Assert.Single(helpers, helper => helper.Name == name).Position;
            Assert.Equal(cell, map.PositionToGrid(position.X, position.Y, position.Z));
        }
        Assert.Equal(0, map.PositionToGrid(map.MinimumX, 0, map.MaximumZ));
        Assert.Equal(-1, map.PositionToGrid(map.MaximumX, 0, map.MaximumZ));
        Assert.Equal(-1, map.PositionToGrid(map.MinimumX, 0, map.MinimumZ));
    }

    /// <summary>The native specification remains mandatory and rejects alias expansion and invalid timing.</summary>
    [Fact]
    public void RulesLoadThroughBoundedReadAndRejectMalformedYaml()
    {
        var store = BnBTestData.Store(1, 1);
        store.Files[BnBRoundRules.Path] = Encoding.UTF8.GetBytes(BnBTestData.RuleYaml);
        Assert.Equal(new BnBRoundRules(3000, 500, 700, 6, 6, 6, 0.2f, 2f), BnBRoundRules.Load(store, 1048576));
        Assert.Equal(new[] { 1048576 }, store.ReadLimits);
        store.Files[BnBRoundRules.Path] = Encoding.UTF8.GetBytes(BnBTestData.RuleYaml.Replace("3000", "-1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BnBRoundRules.Load(store, 1048576));
        store.Files[BnBRoundRules.Path] = Encoding.UTF8.GetBytes("x: &x [*x]");
        Assert.Throws<InvalidDataException>(() => BnBRoundRules.Load(store, 1048576));
    }

    private sealed class NativeStore : IClientDataStore
    {
        /// <summary>Checks whether the provisioned resource has native bytes.</summary>
        public bool Exists(string path) => LocalClientData.Bytes("kr-1256", path).Length > 0;
        /// <summary>Reads the resource from the test client's existing local archive fixture.</summary>
        public byte[] Read(string path) => LocalClientData.Bytes("kr-1256", path);
    }
}

internal static class BnBTestData
{
    internal const string MapPath = "Maps/test/CustomMap.json";
    internal const string LayoutPath = "Maps/test/BnBItemLayout.json";
    internal const string RuleYaml = """
        bomber:
          baseMovementSpeed: 2.0
        metaAbility:
          increaseMovementSpeedPerLevel: 0.2
          maxExplosionRange: 6
          maxInstallBombCount: 6
          maxIncreaseMovementSpeedLevel: 6
        grid:
          bombExplodeDelayTime: 3000
          bombExplodeDurationTime: 500
          blockDestroyingTime: 700
        """;

    internal static MemoryStore Store(int width, int height, int[]? tiles = null, int[]? items = null)
    {
        var map = new
        {
            width, height, depth = 1, grid_unit = 1f, offset = new[] { 0f, 0f, 0f },
            data = new[] { tiles ?? new int[width * height] },
            tile_set = new[] { new { blockType = 2, type = 2 }, new { blockType = 2, type = 0 }, new { blockType = 0, type = 2 } },
        };
        var layout = new
        {
            groundLevel = 0, staticLayer = items ?? new int[width * height],
            staticPresets = new[] { "bomb", "fire", "roller" }.Select((item, index) => new
            {
                staticIndex = index + 1, totalWeight = 10000, items = new[] { new { itemType = item, weight = 10000 } },
            }).ToArray(),
            tilePresets = System.Array.Empty<object>(),
        };
        return new MemoryStore(new Dictionary<string, byte[]>
        {
            [MapPath] = JsonSerializer.SerializeToUtf8Bytes(map),
            [LayoutPath] = JsonSerializer.SerializeToUtf8Bytes(layout),
        });
    }

    internal static BnBMapData Load(MemoryStore store) => BnBMapData.Load(store, "test", 1048576, 1024, new(0, 100));
    internal static BnBMapData Map(int width, int height, int[]? tiles = null, int[]? items = null) => Load(Store(width, height, tiles, items));

    internal sealed class MemoryStore(Dictionary<string, byte[]> files) : IClientDataStore
    {
        internal Dictionary<string, byte[]> Files { get; } = files;
        internal List<int> ReadLimits { get; } = [];
        /// <summary>Checks the synthetic native resource map without allocating a copy.</summary>
        public bool Exists(string path) => Files.ContainsKey(path);
        /// <summary>Rejects callers that omit the required resource byte limit.</summary>
        public byte[] Read(string path) => throw new InvalidOperationException("BnB must use bounded client-data reads.");
        /// <summary>Records the requested limit and returns bytes so the loader must also enforce its bound.</summary>
        public byte[] Read(string path, int maximumBytes)
        {
            ReadLimits.Add(maximumBytes);
            return Files[path];
        }
    }
}
