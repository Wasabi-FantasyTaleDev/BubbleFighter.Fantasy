using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using MemoryPack;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>
/// One wearable position as kept in the build's compiled client data: every field of
/// <see cref="InventoryWearGroup"/>. The contract record itself stays free of the serializer.
/// </summary>
[MemoryPackable]
internal readonly partial record struct PackedWearGroup(int FirstItemId, int LastItemId, int LegacyDefaultItemId,
    bool RequireEquipped, bool RejectWhenOccupied, int[]? ItemIds);

/// <summary>
/// The wearable positions <see cref="ItemWearLocations"/> derives from the item workbook, as kept in the build's
/// compiled client data: every group in order, a group without a member list distinct from one with an empty list.
/// </summary>
internal static class WearLocationsCatalog
{
    internal static readonly ICompiledCatalog<InventoryWearGroup[]> Instance =
        new PackedCatalog<InventoryWearGroup[], PackedWearGroup[]>("wear-locations",
            groups => Array.ConvertAll(groups, group => new PackedWearGroup(group.FirstItemId, group.LastItemId,
                group.LegacyDefaultItemId, group.RequireEquipped, group.RejectWhenOccupied, group.ItemIds?.ToArray())),
            packed => Array.ConvertAll(packed, group =>
                new InventoryWearGroup(group.FirstItemId, group.LastItemId, group.LegacyDefaultItemId, group.RequireEquipped)
                {
                    RejectWhenOccupied = group.RejectWhenOccupied,
                    ItemIds = group.ItemIds,
                }));
}

/// <summary>
/// The item identities a build's client can display, as <see cref="InventoryLoadoutsPlugin.LoadScope"/> derives them
/// from the item and character workbooks: ascending, without the storage key, which is the row's own.
/// </summary>
internal static class LoadoutItemsCatalog
{
    internal static readonly ICompiledCatalog<int[]> Instance = new PackedCatalog<int[]>("loadout-items");
}
