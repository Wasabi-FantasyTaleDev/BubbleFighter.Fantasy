using System.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Writes the bag publication bodies from <c>packets/inventory.yaml</c>: the counted slot list of
/// <c>gspInventoryUpdateAllInventorySlot</c> and <c>gspInventoryUpdateInventorySlots</c>, the three
/// containers of the modern slots packet, the always-empty article table and the always-empty cash
/// inventory. The legacy slot record is mapped here; the modern family supplies its own field names
/// through <see cref="RepInventoryAllValues"/> and <see cref="RepInventorySlotsValues"/>, because the
/// eight-byte field at record +8 has no proven meaning and is named by its offset.
/// </summary>
/// <param name="tables">The packet tables.</param>
/// <param name="build">The build whose layout is written.</param>
internal sealed class InventoryWire(PacketTables tables, string build)
{
    // Whether the build's slot carries the string extension (cn-324 and cn-279 do not).
    private readonly bool _hasExtension = CheckedExtension(tables, build);

    internal byte[] RepInventoryAll(IReadOnlyList<InventorySlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        Check(slots);
        return RepInventoryAllValues([.. slots.Select(ToValues)]);
    }

    internal byte[] RepInventorySlots(IReadOnlyList<InventorySlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        Check(slots);
        return RepInventorySlotsValues([.. slots.Select(ToValues)]);
    }

    /// <summary>Writes the whole-bag packet from already named slot field values.</summary>
    /// <param name="slots">One map of field names and values per slot, in wire order.</param>
    /// <returns>The plain body.</returns>
    internal byte[] RepInventoryAllValues(IReadOnlyList<IReadOnlyDictionary<string, object?>> slots) =>
        PacketCodec.Write(tables, build, "InventoryUpdate", Slots(slots));

    /// <summary>Writes the slots packet from already named slot field values.</summary>
    /// <param name="slots">One map of field names and values per slot, in wire order.</param>
    /// <returns>The plain body.</returns>
    internal byte[] RepInventorySlotsValues(IReadOnlyList<IReadOnlyDictionary<string, object?>> slots) =>
        PacketCodec.Write(tables, build, "InventorySlots", Slots(slots));

    /// <summary>
    /// Writes the empty article table of <c>gspUpdateAllArticleSlot</c>: the server never populates it
    /// (the equipped articles ride the view record instead).
    /// </summary>
    /// <returns>The plain body.</returns>
    internal byte[] RepArticleSlots() => PacketCodec.Write(tables, build, "ArticleSlots", new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>Writes the always-empty cash inventory of <c>gspCashInvenUpdateAllInventorySlot</c>.</summary>
    /// <returns>The plain body.</returns>
    internal byte[] RepCashInventory() => PacketCodec.Write(tables, build, "CashInventoryUpdate", new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> Slots(IReadOnlyList<IReadOnlyDictionary<string, object?>> slots) =>
        new(StringComparer.Ordinal)
        {
            ["slots"] = slots.ToArray(),
        };

    private static Dictionary<string, object?> ToValues(InventorySlot slot) =>
        new(StringComparer.Ordinal)
        {
            ["item_id"] = slot.ItemId,
            ["unknown_8"] = slot.Unknown2,
            ["remaining_days"] = slot.RemainingDaysAtBaseline,
            ["equipped"] = slot.IsEquipped,
            ["unknown_25"] = slot.Unknown5,
            ["unknown_26"] = slot.Unknown6,
            ["unknown_27"] = slot.Unknown7,
            ["unknown_28"] = slot.Unknown8,
            ["unknown_48"] = slot.Unknown9,
        };

    private void Check(IReadOnlyList<InventorySlot> slots)
    {
        if (slots.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentOutOfRangeException(nameof(slots));
        if (!_hasExtension && slots.Any(slot => slot.Unknown9.Length != 0))
            throw new ArgumentException("This inventory layout has no string extension.", nameof(slots));
    }

    /// <summary>
    /// Requires the four publication structures this wire writes, then reports whether the build's slot carries
    /// the string extension. Runs once when the row constructs the wire, so a table that renames or retypes a
    /// consumed field fails at activation rather than on the first publication.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is checked.</param>
    /// <returns>Whether the build's <c>InventorySlot</c> states the string extension field.</returns>
    /// <exception cref="ConfigException">A consumed field is missing or has another type.</exception>
    private static bool CheckedExtension(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "InventoryUpdate", "Inventory wire", ("slots", "list<InventorySlot>"));
        PacketLayoutContract.RequireFields(tables, build, "InventorySlots", "Inventory wire", ("slots", "list<InventorySlot>"));
        PacketLayoutContract.RequireFields(tables, build, "ArticleSlots", "Inventory wire", ("articles", "empty_list"));
        PacketLayoutContract.RequireFields(tables, build, "CashInventoryUpdate", "Inventory wire", ("slots", "list<CashInventorySlot>"));
        return tables.Resolve(build, "InventorySlot").Any(field => field.Name == "unknown_48");
    }
}

// kr-812 sub_977AA0 keys slots by item id; sub_977E70/sub_977F60 test equipped; sub_978920 consumes days.
internal sealed record InventorySlot(
    int ItemId,
    int Unknown2,
    double RemainingDaysAtBaseline,
    bool IsEquipped,
    bool Unknown5,
    bool Unknown6,
    bool Unknown7,
    bool Unknown8,
    string Unknown9);
