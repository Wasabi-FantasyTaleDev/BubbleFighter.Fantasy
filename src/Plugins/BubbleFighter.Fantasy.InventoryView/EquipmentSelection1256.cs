using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Resolves the native character and weapon positions independently of UI category selection.</summary>
internal sealed class EquipmentSelection1256(IInventoryRepository inventory, IReadOnlyList<InventoryWearGroup>? wearableLocations = null) : IEquipmentSelection
{
    // 0x808F10 / 0x81B9C0: primary and special preset positions; ShopItem2 identifies
    // 202/203/204 as rifle/sniper/shotgun, 201 as sidearm, 205 as special, 207 as melee.
    private static readonly InventoryWearGroup[] Groups =
    [
        new(300001, 300031, 300001, RequireEquipped: true),
        new(202000, 204999, 202001, RequireEquipped: true),
        new(201000, 201999, 201001, RequireEquipped: true),
        new(205000, 205999, 205001),
        new(207000, 207999, 207001, RequireEquipped: true),
    ];

    private readonly InventoryWearGroup[] _groups = [.. Groups, .. wearableLocations ?? []];

    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
        await inventory.UpdateWornSelectionAsync(userNum, _groups, null, false, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Equipment normalization returned no inventory.");

    public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
        inventory.UpdateWornSelectionAsync(userNum, _groups, itemId, worn, ct);
}
