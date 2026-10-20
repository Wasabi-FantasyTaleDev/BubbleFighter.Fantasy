using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Publishes the local player's selected appearance and active weapon preset from one equipment snapshot.</summary>
/// <param name="UserNum">The player whose appearance is being published.</param>
/// <param name="CharacterId">The selected character item.</param>
/// <param name="EquippedItemIds">An owned snapshot of the worn item ids, with the character first.</param>
/// <param name="ActiveWeaponPresetSlot">The stored active preset slot.</param>
/// <param name="PrimaryWeaponId">The selected primary weapon, or zero when none is worn.</param>
/// <param name="SpecialWeaponId">The selected special weapon, or zero when none is worn.</param>
public sealed record LocalEquipmentViewUpdated(
    int UserNum,
    int CharacterId,
    IReadOnlyList<int> EquippedItemIds,
    byte ActiveWeaponPresetSlot,
    int PrimaryWeaponId,
    int SpecialWeaponId) : IMessage;

/// <summary>Publishes another player's committed appearance without changing the recipient's local weapon presets.</summary>
/// <param name="UserNum">The observed player's identity.</param>
/// <param name="CharacterId">The observed player's selected character item.</param>
/// <param name="EquippedItemIds">An owned snapshot of the observed player's worn item ids.</param>
public sealed record RemoteEquipmentViewUpdated(
    int UserNum,
    int CharacterId,
    IReadOnlyList<int> EquippedItemIds) : IMessage;
