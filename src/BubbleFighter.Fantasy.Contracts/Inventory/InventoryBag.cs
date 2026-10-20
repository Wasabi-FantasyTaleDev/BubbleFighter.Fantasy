namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Which S2C 802/803 bag snapshot an <see cref="InventorySlotRecord"/> belongs to.
/// Cash inventory is a separate table (S2C 810 uses a different slot reader).
/// </summary>
public enum InventoryBag
{
    /// <summary>S2C 802 <c>gspInventoryUpdateAllInventorySlot</c> (<c>sub_10D8550</c>).</summary>
    CharacterBag = 0,

    /// <summary>S2C 803 container 1, same 64-byte slot reader as S2C 802.</summary>
    EquippedBag = 1,
}
