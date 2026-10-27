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
/// One persisted preset page, the twelve bytes the modern preset list carries per page
/// (<c>kr-1256 sub_115C000</c> reads three <c>u32</c> words at a twelve-byte stride). The swap
/// chain has no client-to-server save packet, so a page's content is server-owned state: the
/// client only asks to activate a page (<c>kr-1256 sub_808F10</c>, <c>kr-812 sub_97A8D0</c>).
/// </summary>
/// <param name="Unknown0">The page's first word. In-match it is applied as a weapon item id of
/// the primary family (<c>kr-1256 sub_808F10</c> tests <c>word - 204000 &lt; 1000</c>;
/// <c>kr-812 sub_97A8D0</c> tests <c>word / 1000 == 204</c>); zero leaves the page unbound, the
/// same convention the derived bar's flags use.</param>
/// <param name="Unknown4">The page's second word; no consumer was traced. The server writes the
/// worn special weapon id here by its own convention, matching the derived page it ships today.</param>
/// <param name="Unknown8">The page's third word; no consumer was traced and the server writes zero.</param>
public sealed record WeaponPresetPage(int Unknown0, int Unknown4, int Unknown8);

/// <summary>
/// Builds the server-owned preset pages from a worn set and states the page-table bounds the
/// clients prove. Five pages per player is a client constant, not a tunable: <c>kr-1256 sub_808F10</c>
/// refuses slot bytes above four and <c>kr-1256 sub_5FB4F0</c> walks the five-entry slot table at
/// <c>.rdata 0x15736D4</c>; <c>kr-812 sub_97A8D0</c> gates <c>a2 &lt;= 4</c> and <c>kr-812 sub_987BA0</c>
/// materializes exactly five id/flag pairs at player+6032..6068.
/// </summary>
public static class WeaponPresetPages
{
    /// <summary>The words one modern preset record carries (<c>kr-1256 sub_115C000</c>, stride twelve).</summary>
    public const int WordsPerPage = 3;

    /// <summary>
    /// Seeds every page with the derived page the shipped bar carries: the worn primary in word 0, the
    /// worn special in word 1 and zero in word 2. Fresh pages keep the shipped look, and because they are
    /// persisted they survive later worn changes until an owner rule rewrites them.
    /// </summary>
    /// <param name="primaryWeaponId">The first worn primary (202000..204999), or zero when none is worn.</param>
    /// <param name="specialWeaponId">The first worn special (205000..205999), or zero when none is worn.</param>
    /// <returns>Five identical pages in page order.</returns>
    public static WeaponPresetPage[] SeedFromWorn(int primaryWeaponId, int specialWeaponId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(primaryWeaponId);
        ArgumentOutOfRangeException.ThrowIfNegative(specialWeaponId);
        return Enumerable.Range(0, WeaponPresetSlots.Count)
            .Select(_ => new WeaponPresetPage(primaryWeaponId, specialWeaponId, 0))
            .ToArray();
    }

    /// <summary>
    /// Validates and copies one page table. Pages are positional - the client keys them by list position
    /// (<c>kr-1256 sub_81B820</c>) - so a store accepts exactly <see cref="WeaponPresetSlots.Count"/> entries
    /// and keeps a defensive copy, never the caller's array.
    /// </summary>
    /// <param name="pages">The pages a caller wants stored.</param>
    /// <returns>A five-page defensive copy in page order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pages"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the table does not carry exactly
    /// <see cref="WeaponPresetSlots.Count"/> pages or holds a null entry.</exception>
    public static WeaponPresetPage[] CopyTable(WeaponPresetPage[] pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Length != WeaponPresetSlots.Count || pages.Any(page => page is null))
        {
            throw new ArgumentException(
                $"The preset page table carries exactly {WeaponPresetSlots.Count} pages.", nameof(pages));
        }
        return [.. pages];
    }
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
