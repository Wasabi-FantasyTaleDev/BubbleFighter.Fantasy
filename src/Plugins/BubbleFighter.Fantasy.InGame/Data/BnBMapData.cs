using System.Collections.Frozen;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>The item names declared by the client's BnBItemType enumeration.</summary>
internal enum BnBItemKind { None, Bomb, Fire, Roller }

/// <summary>The native cell obstruction state, independent of its bomb and hidden item.</summary>
internal enum BnBBlockState { Empty, Destroying, Destructible, Solid }

/// <summary>The TileLayer scene bounds before CustomMap.json applies its offset.</summary>
internal readonly record struct BnBMapBounds(float MinimumX, float MaximumZ);

/// <summary>Immutable, bounded native map geometry and ordered item presets for one BnB stage.</summary>
internal sealed class BnBMapData
{
    private readonly BnBBlockState[] _blocks;
    private readonly int[] _tiles;
    private readonly int[] _staticLayer;
    private readonly FrozenDictionary<int, Preset> _staticPresets;
    private readonly FrozenDictionary<int, Preset> _tilePresets;

    private BnBMapData(int width, int height, float unit, float minimumX, float maximumX, float minimumZ, float maximumZ,
        BnBBlockState[] blocks, int[] tiles, int[] staticLayer,
        Dictionary<int, Preset> staticPresets, Dictionary<int, Preset> tilePresets)
    {
        Width = width;
        Height = height;
        GridUnit = unit;
        MinimumX = minimumX;
        MaximumZ = maximumZ;
        MaximumX = maximumX;
        MinimumZ = minimumZ;
        if (!float.IsFinite(MaximumX) || !float.IsFinite(MinimumZ)
            || MaximumX <= minimumX || MinimumZ >= maximumZ)
            throw new InvalidDataException("BnB map bounds are not finite and ordered.");
        _blocks = blocks;
        _tiles = tiles;
        _staticLayer = staticLayer;
        _staticPresets = staticPresets.ToFrozenDictionary();
        _tilePresets = tilePresets.ToFrozenDictionary();
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int CellCount => _blocks.Length;
    internal float GridUnit { get; }
    internal float MinimumX { get; }
    internal float MaximumX { get; }
    internal float MinimumZ { get; }
    internal float MaximumZ { get; }

    /// <summary>Reads both required map resources; the scene origin must come from that map's TileLayer bounds.</summary>
    internal static BnBMapData Load(IClientDataStore store, string stageDirectory, int maximumFileBytes,
        int maximumCells, BnBMapBounds tileLayerBounds)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCells, 1);
        if (string.IsNullOrWhiteSpace(stageDirectory) || stageDirectory.Contains(':') || stageDirectory.Contains('\\')
            || stageDirectory.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("BnB requires a relative map directory without traversal.");
        if (!float.IsFinite(tileLayerBounds.MinimumX) || !float.IsFinite(tileLayerBounds.MaximumZ))
            throw new InvalidDataException("BnB requires finite TileLayer scene bounds.");
        try
        {
            using var mapDocument = ReadJson(store, $"Maps/{stageDirectory}/CustomMap.json", maximumFileBytes);
            using var itemDocument = ReadJson(store, $"Maps/{stageDirectory}/BnBItemLayout.json", maximumFileBytes);
            var map = Object(mapDocument.RootElement);
            var layout = Object(itemDocument.RootElement);
            var width = Integer(map, "width", 1, maximumCells);
            var height = Integer(map, "height", 1, maximumCells);
            if ((long)width * height > maximumCells) throw new InvalidDataException("BnB map exceeds its cell limit.");
            var count = width * height;
            var depth = Integer(map, "depth", 1, maximumFileBytes);
            var ground = Integer(layout, "groundLevel", 0, depth - 1);
            var unit = Single(map, "grid_unit");
            if (unit <= 0) throw new InvalidDataException("BnB grid_unit must be positive.");
            var offsets = Array(map, "offset");
            if (offsets.GetArrayLength() != 3) throw new InvalidDataException("BnB offset requires three coordinates.");
            var offsetX = Single(offsets[0]);
            _ = Single(offsets[1]);
            var offsetZ = Single(offsets[2]);
            var tileRows = Array(map, "tile_set");
            var blockTypes = new BnBBlockState[tileRows.GetArrayLength() + 1];
            var tileIndex = 1;
            // kr-1256 sub_1157BE0 inserts tile zero; kr-1256 sub_BFE1F0 classifies type and blockType.
            foreach (var row in tileRows.EnumerateArray())
            {
                var tile = Object(row);
                var type = Integer(tile, "type", 0, ushort.MaxValue);
                var block = Integer(tile, "blockType", 0, byte.MaxValue);
                blockTypes[tileIndex++] = block == 0 ? BnBBlockState.Empty : type == 0 ? BnBBlockState.Solid
                    : (type & 2) != 0 ? BnBBlockState.Destructible : BnBBlockState.Empty;
            }
            var layers = Array(map, "data");
            if (layers.GetArrayLength() != depth) throw new InvalidDataException("BnB map depth disagrees with its layers.");
            var tiles = new int[count];
            var layerIndex = 0;
            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Array || layer.GetArrayLength() != count)
                    throw new InvalidDataException("BnB map layer does not cover every cell.");
                var index = 0;
                foreach (var value in layer.EnumerateArray())
                {
                    var id = Integer(value, 0, blockTypes.Length - 1);
                    if (layerIndex == ground) tiles[index] = id;
                    index++;
                }
                layerIndex++;
            }
            var staticValues = Array(layout, "staticLayer");
            // kr-1256 sub_BFE920 skips a mismatched static layer; malformed resources fail before a server round starts.
            if (staticValues.GetArrayLength() != count)
                throw new InvalidDataException("BnB staticLayer does not cover every cell.");
            var staticLayer = staticValues.EnumerateArray().Select(value => Integer(value, 0, int.MaxValue)).ToArray();
            // kr-1256 sub_7AEBB0 extends the original bounds by grid size before applying the JSON offset.
            return new(width, height, unit, tileLayerBounds.MinimumX + offsetX,
                (tileLayerBounds.MinimumX + width * unit) + offsetX,
                (tileLayerBounds.MaximumZ - height * unit) + offsetZ, tileLayerBounds.MaximumZ + offsetZ,
                tiles.Select(id => blockTypes[id]).ToArray(), tiles, staticLayer,
                ReadPresets(Array(layout, "staticPresets"), "staticIndex", int.MaxValue),
                ReadPresets(Array(layout, "tilePresets"), "tileIndex", blockTypes.Length - 1));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException($"BnB map '{stageDirectory}' has malformed native JSON.", exception);
        }
    }

    /// <summary>Returns a fresh item array using both native passes, including guaranteed-preset RNG draws.</summary>
    internal BnBItemKind[] CreateInitialItems(int seed)
    {
        var random = new BnBRandomSource(seed);
        var items = new BnBItemKind[CellCount];
        // kr-1256 sub_BFE920 completes the static pass before drawing for still-empty cells in the tile pass.
        for (var index = 0; index < items.Length; index++)
            if (_staticPresets.TryGetValue(_staticLayer[index], out var preset)) items[index] = preset.Select(random);
        for (var index = 0; index < items.Length; index++)
            if (items[index] == BnBItemKind.None && _tilePresets.TryGetValue(_tiles[index], out var preset))
                items[index] = preset.Select(random);
        return items;
    }

    internal BnBBlockState InitialBlock(int gridIndex) => _blocks[gridIndex];
    internal bool Contains(int gridIndex) => (uint)gridIndex < (uint)CellCount;

    /// <summary>Converts a finite scene position to a cell, returning -1 outside the native x/z grid.</summary>
    internal int PositionToGrid(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
            || x < MinimumX || x > MaximumX || z < MinimumZ || z > MaximumZ) return -1;
        // kr-1256 sub_BFF250 and kr-1256 sub_1230830 truncate nonnegative float quotients toward zero.
        var column = (int)((x - MinimumX) / GridUnit);
        var row = (int)((MaximumZ - z) / GridUnit);
        return (uint)column < (uint)Width && (uint)row < (uint)Height ? row * Width + column : -1;
    }

    private static Dictionary<int, Preset> ReadPresets(JsonElement rows, string key, int maximumIndex)
    {
        var result = new Dictionary<int, Preset>();
        foreach (var row in rows.EnumerateArray())
        {
            var fields = Object(row);
            var index = Integer(fields, key, 0, maximumIndex);
            var total = Integer(fields, "totalWeight", 1, int.MaxValue);
            var choices = new List<Choice>();
            long cumulative = 0;
            foreach (var value in Array(fields, "items").EnumerateArray())
            {
                var item = Object(value);
                var kind = item.GetProperty("itemType").GetString() switch
                {
                    "bomb" => BnBItemKind.Bomb,
                    "fire" => BnBItemKind.Fire,
                    "roller" => BnBItemKind.Roller,
                    _ => throw new InvalidDataException("BnB preset contains an unknown itemType."),
                };
                cumulative += Integer(item, "weight", 0, total);
                if (cumulative > total) throw new InvalidDataException("BnB preset weights exceed totalWeight.");
                choices.Add(new(kind, (int)cumulative));
            }
            if (!result.TryAdd(index, new(total, choices.ToArray())))
                throw new InvalidDataException($"BnB presets repeat {key} {index}.");
        }
        return result;
    }

    private static JsonDocument ReadJson(IClientDataStore store, string path, int maximumBytes)
    {
        var bytes = store.Read(path, maximumBytes);
        if (bytes.Length == 0 || bytes.Length > maximumBytes) throw new InvalidDataException($"'{path}' is empty or oversized.");
        return JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0));
    }

    private static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("BnB resource requires a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException($"BnB resource repeats '{property.Name}'.");
        return value;
    }

    private static JsonElement Array(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Array ? value : throw new InvalidDataException($"BnB '{name}' must be an array.");
    }

    private static int Integer(JsonElement parent, string name, int minimum, int maximum) => Integer(parent.GetProperty(name), minimum, maximum);
    private static int Integer(JsonElement value, int minimum, int maximum) => value.TryGetInt32(out var number)
        && number >= minimum && number <= maximum ? number : throw new InvalidDataException("BnB integer is outside its bounds.");
    private static float Single(JsonElement parent, string name) => Single(parent.GetProperty(name));
    private static float Single(JsonElement value) => value.TryGetSingle(out var number) && float.IsFinite(number) ? number
        : throw new InvalidDataException("BnB coordinate is not a finite float.");

    private sealed record Choice(BnBItemKind Kind, int CumulativeWeight);
    private sealed record Preset(int TotalWeight, Choice[] Choices)
    {
        internal BnBItemKind Select(BnBRandomSource random)
        {
            var draw = random.Next(TotalWeight);
            foreach (var choice in Choices)
                if (draw < choice.CumulativeWeight) return choice.Kind;
            return BnBItemKind.None;
        }
    }
}
