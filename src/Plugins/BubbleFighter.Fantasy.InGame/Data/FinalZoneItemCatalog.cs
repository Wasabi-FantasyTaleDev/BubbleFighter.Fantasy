using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using YamlDotNet.RepresentationModel;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Deployment bounds for importing native FinalZone resources.</summary>
internal sealed record FinalZoneDataLimits(int MaximumFileBytes, int MaximumRows, int MaximumItemSpecs,
    int MaximumAlternatives, int MaximumQuantity, int MaximumBonusItems)
{
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumItemSpecs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAlternatives);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumQuantity);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumBonusItems);
    }
}

/// <summary>Item identity and inventory classification read from GenItemSetting.</summary>
internal sealed record FinalZoneItemSpec(string Name, int Id, string Group, string Category, int WeaponId,
    bool IsVariant, bool IsReloadNecessary, bool IsSplittable);

/// <summary>Resolves the client's symbolic spawn candidates without embedding its item tables in the server.</summary>
internal sealed class FinalZoneItemCatalog
{
    internal const string Path = "Config/GameSpec/GenItemSetting.yaml";
    private readonly IReadOnlyDictionary<string, FinalZoneItemSpec> _byName;
    private readonly IReadOnlyDictionary<int, FinalZoneItemSpec> _byId;
    private readonly IReadOnlySet<int> _startingPacks;

    private FinalZoneItemCatalog(Dictionary<string, FinalZoneItemSpec> byName, Dictionary<int, FinalZoneItemSpec> byId,
        IReadOnlySet<int> startingPacks)
    {
        _byName = new ReadOnlyDictionary<string, FinalZoneItemSpec>(byName);
        _byId = new ReadOnlyDictionary<int, FinalZoneItemSpec>(byId);
        _startingPacks = startingPacks;
    }

    internal IReadOnlyDictionary<int, FinalZoneItemSpec> Items => _byId;

    /// <summary>Rejects optional opening packs until their native inventory-consumption acknowledgement is integrated.</summary>
    internal void ValidateOpeningEquipment(IReadOnlyList<int>? equippedItemIds)
    {
        if (_startingPacks.Count == 0) return;
        if (equippedItemIds is null)
            throw new InvalidDataException("FinalZone requires the participant's opening equipment snapshot.");
        // kr-1256 sub_CD7B20 consumes an equipped pack; kr-1256 sub_CD7D60 grants its resource entries after acknowledgement.
        if (equippedItemIds.Any(_startingPacks.Contains))
            throw new InvalidDataException("FinalZone starting packs are not supported; unequip the pack before starting.");
    }

    /// <summary>Accounts for the items the native FinalZone HUD grants before receiving any map pickup.</summary>
    internal IReadOnlyList<FinalZoneBackpackEntry> OpeningInventory()
    {
        // kr-1256 sub_CD63F0 initializes the HUD through kr-1256 sub_461360;
        // kr-1256 sub_55CEE0 grants one medipack and one dog tag to kind 3.
        return Array.AsReadOnly(new[] { Entry(580), Entry(590) });

        FinalZoneBackpackEntry Entry(int id) => _byId.TryGetValue(id, out var spec)
            ? new(id, spec.WeaponId, 1, new([], []))
            : throw new InvalidDataException($"{Path}: native FinalZone opening item {id} is missing.");
    }

    internal FinalZoneItemSpec Resolve(string name) => _byName.TryGetValue(name, out var spec) ? spec
        : throw new InvalidDataException($"{Path}: unknown generated item '{name}'.");

    internal static FinalZoneItemCatalog Load(IClientDataStore store, FinalZoneDataLimits limits)
    {
        limits.Validate();
        var bytes = ReadBounded(store, Path, limits);
        using var text = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var yaml = new YamlStream();
        yaml.Load(text);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"{Path}: expected one configuration mapping.");
        var meta = Mapping(root, "GenItemMeta");
        var categories = new Dictionary<string, (string Group, bool Variant, bool Reload, bool Splittable)>(StringComparer.Ordinal);
        foreach (var group in meta.Children)
        {
            if (group.Value is not YamlMappingNode rows)
                throw new InvalidDataException($"{Path}: item group must be a mapping.");
            foreach (var category in rows.Children)
            {
                if (category.Value is not YamlMappingNode fields || categories.Count >= limits.MaximumItemSpecs
                    || !categories.TryAdd(Scalar(category.Key), (Scalar(group.Key), Boolean(fields, "isVariant"),
                        Boolean(fields, "isReloadNecessary"), Boolean(fields, "isSplittable"))))
                    throw new InvalidDataException($"{Path}: duplicate, malformed or excessive categories.");
            }
        }
        var byName = new Dictionary<string, FinalZoneItemSpec>(StringComparer.Ordinal);
        var byId = new Dictionary<int, FinalZoneItemSpec>();
        foreach (var row in Mapping(root, "GenItems").Children)
        {
            if (row.Value is not YamlMappingNode fields || byId.Count >= limits.MaximumItemSpecs)
                throw new InvalidDataException($"{Path}: malformed or excessive item specifications.");
            var name = Scalar(row.Key);
            var category = Scalar(Field(fields, "genItemCategory"));
            if (!categories.TryGetValue(category, out var metaRow))
                throw new InvalidDataException($"{Path}: item '{name}' has unknown category '{category}'.");
            var id = Integer(fields, "genItemId");
            var weapon = Integer(fields, "weaponId");
            // kr-1256 sub_CA54B0 stores weaponId as a sign-extended native 16-bit value in GenItemSpec+32.
            var spec = new FinalZoneItemSpec(name, id, metaRow.Group, category, unchecked((short)weapon),
                metaRow.Variant, metaRow.Reload, metaRow.Splittable);
            if (id <= 0 || weapon is < -1 or > ushort.MaxValue || !byName.TryAdd(name, spec) || !byId.TryAdd(id, spec))
                throw new InvalidDataException($"{Path}: invalid or duplicate item '{name}' ({id}).");
        }
        if (byId.Count == 0) throw new InvalidDataException($"{Path}: no generated items.");
        var packs = new HashSet<int>();
        if (root.Children.TryGetValue(new YamlScalarNode("SurvivalPacks"), out var packNode))
        {
            if (packNode is not YamlMappingNode packRows || packRows.Children.Count > limits.MaximumItemSpecs)
                throw new InvalidDataException($"{Path}: malformed or excessive starting packs.");
            foreach (var row in packRows.Children)
                if (!int.TryParse(Scalar(row.Key), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id <= 0 || row.Value is not YamlMappingNode || !packs.Add(id))
                    throw new InvalidDataException($"{Path}: invalid starting pack.");
        }
        return new(byName, byId, packs);
    }

    internal static byte[] ReadBounded(IClientDataStore store, string path, FinalZoneDataLimits limits)
    {
        var bytes = store.Read(path);
        if (bytes.Length > limits.MaximumFileBytes)
            throw new InvalidDataException($"{path}: resource exceeds the FinalZone file-size limit.");
        return bytes;
    }

    private static YamlNode Field(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value)
        ? value : throw new InvalidDataException($"{Path}: missing '{key}'.");

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) => Field(node, key) as YamlMappingNode
        ?? throw new InvalidDataException($"{Path}: '{key}' must be a mapping.");

    private static string Scalar(YamlNode node) => node is YamlScalarNode { Value: { Length: > 0 } value } ? value
        : throw new InvalidDataException($"{Path}: expected a nonempty scalar.");

    private static bool Boolean(YamlMappingNode node, string key) => bool.TryParse(Scalar(Field(node, key)), out var value) ? value
        : throw new InvalidDataException($"{Path}: '{key}' must be boolean.");

    private static int Integer(YamlMappingNode node, string key) =>
        int.TryParse(Scalar(Field(node, key)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
        : throw new InvalidDataException($"{Path}: '{key}' must be an integer.");
}
