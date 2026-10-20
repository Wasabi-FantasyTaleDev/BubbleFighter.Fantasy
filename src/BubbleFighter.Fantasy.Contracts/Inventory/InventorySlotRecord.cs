namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// One bag entry, sent as an S2C 802 / S2C 803 container-1 slot (<c>sub_10D8550</c>).
/// </summary>
/// <param name="UserNum">Login <c>players.user_num</c>.</param> <param name="Bag">Which slot vector the row
/// belongs to.</param> <param name="SlotKey">The slot's <c>i64</c> key (802 <c>unknown3</c>).</param> <param
/// name="ItemId">The item id (802 <c>unknown1</c>): the client's bag map is keyed by it (<c>sub_8046E0,99</c>)
/// and C2S 804 / 805 name the item by it.</param> <param name="Quantity">The item count (802 <c>unknown2</c>):
/// stackable items count 1, 2, 3..., consumables counting down from 999 and permanent
/// gear at <c>int.MaxValue</c>.</param> <param name="Flags">802 <c>unknown6</c> packed <c>u8</c>.</param>
public sealed record InventorySlotRecord(
    long UserNum,
    InventoryBag Bag,
    long SlotKey,
    int ItemId,
    int Quantity,
    byte Flags)
{
    /// <summary>Gets the durable lifetime; existing rows remain unspecified until their terms are established.</summary>
    public InventoryLifetime Lifetime { get; init; }

    /// <summary>
    /// The worn bit of the packed flags <c>u8</c>. The client's slot parse expands
    /// wire <c>flags &amp; 1</c> into the parsed record byte that
    /// <c>Inventory::IsEquipped</c> returns (<c>sub_10D8550</c> / <c>sub_80F6B0</c>),
    /// so this bit is the durable worn state of a bag entry. Every other bit is
    /// unproven and must be preserved, never synthesized.
    ///
    /// </summary>
    public const byte WornFlagBit = 0x01;
}
