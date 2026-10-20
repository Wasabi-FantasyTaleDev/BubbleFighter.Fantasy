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

    /// <summary>Rejects preset routes before activation when their build lacks reviewed layouts.</summary>
    internal static void RequirePresets(PacketTables tables, string build)
    {
        if (!tables.CapabilityOf(build, "weapon_presets"))
            throw new ConfigException($"Build '{build}' has no audited weapon-preset capability.");
        _ = tables.Resolve(build, "WeaponPresetChangeRequest");
    }
}
