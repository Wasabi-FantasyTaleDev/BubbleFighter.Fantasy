namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Bounds enforced by inventory repositories and their callers before allocating snapshots.</summary>
public static class InventoryLimits
{
    /// <summary>Maximum rows accepted or returned for one player and bag.</summary>
    public const int MaxInventorySlotsPerBag = 512;

    /// <summary>Maximum equipped article rows accepted or returned for one player.</summary>
    public const int MaxEquippedSlots = 512;

    /// <summary>Maximum character item identities accepted or returned for one player.</summary>
    public const int MaxCharacterItemIds = 512;

    /// <summary>Maximum cash inventory rows accepted or returned for one player.</summary>
    public const int MaxCashInventorySlots = 512;

    /// <summary>Maximum position rules accepted by one atomic worn-state update.</summary>
    public const int MaxWearGroups = 64;

    /// <summary>Maximum explicit catalog identities in one position or one set of additional positions.</summary>
    public const int MaxItemsPerWearGroup = 65_536;

    /// <summary>
    /// Largest weapon preset index: the client builder rejects values above four
    /// and its hotkey table has five entries (sub_808F10, sub_B033E0).
    /// </summary>
    public const int MaxWeaponPresetSlot = 4;
}
