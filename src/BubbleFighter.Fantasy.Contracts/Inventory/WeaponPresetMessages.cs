using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Requests a switch of the active weapon preset slot. The legacy client builder
/// (<c>sub_97A8D0</c>, kr-812) writes a five-byte body and latches its sender: no further
/// switch is emitted until the codec's status reply arrives, so the request must always be
/// answered.
/// </summary>
/// <param name="Unknown0">The opaque context field the client emits (observed as -1 in the
/// menu and 1000000 in-match); it is not interpreted.</param>
/// <param name="Slot">The requested preset slot, validated against
/// <see cref="InventoryLimits.MaxWeaponPresetSlot"/>.</param>
public sealed record WeaponPresetChangeRequested(int Unknown0, byte Slot) : IMessage;

/// <summary>
/// The codec's status reply to a preset switch. Every status clears the client's send latch;
/// only a zero status applies the carried slot to the active preset.
/// </summary>
/// <param name="Request">The request being answered.</param>
/// <param name="Slot">The granted slot, or the still-active slot when the request was refused.</param>
public sealed record WeaponPresetChangeReplied(WeaponPresetChangeRequested Request, byte Slot) : IMessage
{
    /// <summary>
    /// Gets the native result: zero applies the slot; any nonzero value refuses it without
    /// changing the active preset. kr-812 sub_832BD0 and kr-1256 sub_9D2FF0 clear the latch first.
    /// </summary>
    public int Status { get; init; }
}

/// <summary>
/// Maps the worn weapon set onto the client's five-slot preset hotbar. The published bar is
/// derived from ownership: primary (202-204) first, then sidearm (201), special (205) and
/// melee (207); position 4 stays unbound, matching the recorded bar that leaves the last
/// hotkey empty.
/// </summary>
public static class WeaponPresetSlots
{
    /// <summary>The number of slots the preset bar carries.</summary>
    public const int Count = InventoryLimits.MaxWeaponPresetSlot + 1;

    /// <summary>
    /// Builds the five preset slots from the worn item ids. A worn family contributes its
    /// first worn member; a family with nothing worn leaves its slot zero (unbound), and the
    /// client will not emit a switch request for it (<c>sub_956300</c> gates the sender on the
    /// slot's bound flag).
    /// </summary>
    /// <param name="wornItemIds">The worn item ids; the character id is ignored.</param>
    /// <returns>Five slot item ids in bar order, zero meaning unbound.</returns>
    public static int[] FromWorn(IEnumerable<int> wornItemIds)
    {
        ArgumentNullException.ThrowIfNull(wornItemIds);
        var worn = wornItemIds as IReadOnlyCollection<int> ?? wornItemIds.ToArray();
        return
        [
            worn.FirstOrDefault(id => id is >= 202000 and < 205000),
            worn.FirstOrDefault(id => id is >= 201000 and < 202000),
            worn.FirstOrDefault(id => id is >= 205000 and < 206000),
            worn.FirstOrDefault(id => id is >= 207000 and < 208000),
            0,
        ];
    }
}
