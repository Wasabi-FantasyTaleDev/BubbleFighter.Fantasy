using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Codecs for the legacy weapon preset family: C2S 830 switch request, S2C 832 status
/// reply, and the S2C 833 preset bar. Audited response layouts are shared through
/// packets/weapon-presets.yaml; builds without reviewed layouts cannot emit an empty placeholder.
/// </summary>
internal static class PresetWire
{
    /// <summary>
    /// Reads C2S 830 (<c>gcpReqInventoryChangeWeaponPresetIdx</c>): exactly one
    /// <c>i32</c> context plus one <c>u8</c> slot. The builder <c>kr-812 sub_97A8D0</c>
    /// emits the context as <c>a3</c> (-1 in the menu, 1000000 in-match) and refuses
    /// slots above four before sending.
    /// </summary>
    internal static WeaponPresetChangeRequested ReadChangeRequest(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        RequirePresets(tables, build);
        var values = PacketCodec.ReadScalars(tables, build, "WeaponPresetChangeRequest", body);
        return new WeaponPresetChangeRequested((int)values["context"], (byte)values["slot"]);
    }

    /// <summary>
    /// Writes S2C 832 (<c>gspReqInventoryChangeWeaponPresetIdx</c>): <c>u8</c> slot plus
    /// <c>i32</c> status. kr-812 sub_83B880 reads the fields; kr-812 sub_832BD0
    /// clears the send latch for every status and applies the slot only when the status is zero.
    /// </summary>
    internal static byte[] WriteChangeReply(PacketTables tables, string build, byte slot, int status)
    {
        RequirePresets(tables, build);
        return PacketCodec.Write(tables, build, "WeaponPresetChangeReply", new Dictionary<string, object?>
        {
            ["active_slot"] = slot, ["status"] = status,
        });
    }

    /// <summary>
    /// Writes S2C 833 (<c>gspReqWeaponPreSetUpdate</c>): a <c>u8</c> active slot followed by
    /// <c>WeaponPreSetList</c> — <c>i32</c> user number, then three counted vectors of slot
    /// records: <c>i32</c> item ids, <c>u8</c> bound flags, and a second <c>u8</c> vector.
    /// Readers: packet <c>kr-812 sub_83B820</c>, list <c>kr-812 sub_812190</c>; the consumer
    /// <c>kr-812 sub_987BA0</c> stores the u8 at player+6072 and each record at +6032, where the
    /// first flag vector decides whether the C2S 830 sender may offer that hotkey
    /// (<c>kr-812 sub_956300</c> tests record+4 == 1). The second flag vector lands at +6116; its
    /// consumer is unproven and the server writes zeros there.
    /// </summary>
    /// <remarks>
    /// kr-1256 reads a list of three-word records instead (kr-1256 sub_9C3880, entries kr-1256 sub_115C000): one per
    /// preset page, written as the worn primary and special weapons and a zero word for <paramref name="presetPages"/>
    /// pages. Values are supplied for both shapes; the build's table takes the ones it has.
    /// </remarks>
    internal static byte[] WriteUpdate(PacketTables tables, string build, byte activeSlot, int userNumber, IReadOnlyList<int> slotItemIds,
        int primaryWeaponId = 0, int specialWeaponId = 0, int presetPages = 0)
    {
        ArgumentNullException.ThrowIfNull(slotItemIds);
        if (slotItemIds.Count != WeaponPresetSlots.Count)
            throw new ArgumentException(
                $"The preset bar carries exactly {WeaponPresetSlots.Count} slots.", nameof(slotItemIds));
        RequirePresets(tables, build);
        return PacketCodec.Write(tables, build, "WeaponPresetUpdate", new Dictionary<string, object?>
        {
            ["active_slot"] = activeSlot,
            ["user_number"] = userNumber,
            ["item_ids"] = slotItemIds,
            ["bound_flags"] = slotItemIds.Select(itemId => itemId != 0 ? (byte)1 : (byte)0),
            ["unknown_40"] = new byte[slotItemIds.Count],
            ["entries"] = Enumerable.Range(0, presetPages).Select(_ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unknown_0"] = primaryWeaponId, ["unknown_4"] = specialWeaponId, ["unknown_8"] = 0,
            }).ToArray(),
        });
    }

    /// <summary>
    /// Writes S2C 833/837 (<c>gspReqWeaponPreSetUpdate</c>) from the player's persisted preset pages, the
    /// form the <c>persist_preset_pages</c> switch publishes. The build's table picks its own shape for the
    /// same five pages: kr-1256 carries every page as its three-word record (<c>kr-1256 sub_115C000</c>,
    /// keyed by list position in <c>kr-1256 sub_81B820</c>), so a record exists for pages 3-4 and
    /// <c>kr-1256 sub_5FB4F0</c> enables them; the legacy parallel vectors carry each page's first word as
    /// its item id with flag one when it is nonzero, the projection <c>kr-812 sub_97A8D0</c>'s
    /// <c>/1000 == 204</c> gate reads and <c>kr-812 sub_987BA0</c> materializes at player+6032..6068.
    /// </summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose layout is written.</param>
    /// <param name="activeSlot">The persisted active slot; the leading byte every reader applies.</param>
    /// <param name="userNumber">The player the bar belongs to.</param>
    /// <param name="pages">The persisted pages; exactly <see cref="WeaponPresetSlots.Count"/> entries.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pages"/> does not carry exactly
    /// <see cref="WeaponPresetSlots.Count"/> pages or holds a null entry.</exception>
    internal static byte[] WriteUpdate(PacketTables tables, string build, byte activeSlot, int userNumber,
        IReadOnlyList<WeaponPresetPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count != WeaponPresetSlots.Count || pages.Any(page => page is null))
            throw new ArgumentException(
                $"The persisted preset bar carries exactly {WeaponPresetSlots.Count} pages.", nameof(pages));
        RequirePresets(tables, build);
        var owned = pages.ToArray();
        return PacketCodec.Write(tables, build, "WeaponPresetUpdate", new Dictionary<string, object?>
        {
            ["active_slot"] = activeSlot,
            ["user_number"] = userNumber,
            // The legacy page is the record's first word alone; a page without a weapon stays unbound,
            // the same nonzero-means-bound flag the derived bar writes.
            ["item_ids"] = owned.Select(page => page.Unknown0).ToArray(),
            ["bound_flags"] = owned.Select(page => page.Unknown0 != 0 ? (byte)1 : (byte)0).ToArray(),
            ["unknown_40"] = new byte[owned.Length],
            ["entries"] = owned.Select(page => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unknown_0"] = page.Unknown0, ["unknown_4"] = page.Unknown4, ["unknown_8"] = page.Unknown8,
            }).ToArray(),
        });
    }

    /// <summary>Rejects preset routes before activation when their build lacks reviewed layouts.</summary>
    internal static void RequirePresets(PacketTables tables, string build)
    {
        if (!tables.CapabilityOf(build, "weapon_presets"))
            throw new ConfigException($"Build '{build}' has no audited weapon-preset capability.");
        PacketLayoutContract.RequireFields(tables, build, "WeaponPresetChangeRequest", "Weapon presets",
            ("context", "u32"), ("slot", "u8"));
        PacketLayoutContract.RequireFields(tables, build, "WeaponPresetChangeReply", "Weapon presets",
            ("active_slot", "u8"), ("status", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "WeaponPresetUpdate", "Weapon presets",
            ("active_slot", "u8"), ("presets", "WeaponPresetList"));
    }
}
