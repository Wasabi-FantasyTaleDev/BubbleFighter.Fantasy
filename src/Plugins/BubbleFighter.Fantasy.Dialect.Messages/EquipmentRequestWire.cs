using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Reads wear and removal requests and writes their results; the equipped view is the <c>EquippedItemList</c> table.</summary>
internal static class EquipmentRequestWire
{
    /// <summary>Keys from this value up name an article, not a bag item (kr-1256 sub_806CE0 selects the bag below it).</summary>
    private const long FirstArticleKey = 100000000;

    /// <summary>
    /// Reads the build's <c>InventoryEquipmentRequest</c>: kr-812 sub_977E70 / sub_977F60 send one item id
    /// (reader kr-812 sub_80A910); kr-1256 sub_659850 / sub_805A60 send one i64 key whose values below
    /// <see cref="FirstArticleKey"/> are bag item ids. The key is kept as the request's reference.
    /// </summary>
    internal static EquipmentChangeRequested ReadRequest(PacketTables tables, string build, ReadOnlySpan<byte> body, bool worn)
    {
        var key = Convert.ToInt64(PacketCodec.ReadScalars(tables, build, "InventoryEquipmentRequest", body)["item_id"],
            System.Globalization.CultureInfo.InvariantCulture);
        return new EquipmentChangeRequested(key, key is > 0 and < FirstArticleKey ? (int)key : null, worn);
    }

    /// <summary>
    /// Writes the single i32 result read by kr-812 sub_80A910 for S808/S809. Consumers
    /// kr-812 sub_824AC0 and kr-812 sub_824F00 recognize zero as success; no item id follows it.
    /// </summary>
    internal static byte[] WriteResult(PacketTables tables, string build, int result, long key) =>
        PacketCodec.Write(tables, build, "InventoryEquipmentResult", new Dictionary<string, object?> { ["result"] = result, ["item_id"] = key });
}
