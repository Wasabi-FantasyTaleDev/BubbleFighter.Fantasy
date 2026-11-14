using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

// The native ItemAttr vector (packets/ tables, list<ItemAttr>) as dialect.messages also writes it; plugins keep their own copy
// rather than reference each other.
internal static class ItemAttributeWire
{
    internal static void Apply(PacketTables tables, string build, string structure,
        Dictionary<string, object?> values, IReadOnlyList<RoomItemAttribute> attributes)
    {
        var fields = tables.Resolve(build, structure).Where(field => field.Type == "list<ItemAttr>").ToArray();
        if (fields.Length > 1 || fields.Length == 0 && attributes.Count != 0)
            throw new InvalidDataException($"Equipment structure '{structure}' has no unique native attribute vector.");
        if (fields.Length == 1) values[fields[0].Name] = Values(attributes);
    }

    internal static IReadOnlyDictionary<string, object?>[] Values(IReadOnlyList<RoomItemAttribute> attributes)
    {
        if (attributes.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new InvalidDataException("Inventory attributes exceed the bounded equipment snapshot.");
        return attributes.Select(attribute => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["unknown_4"] = attribute.ItemId,
            ["unknown_8"] = attribute.Kind,
            ["unknown_12"] = attribute.Values,
            ["unknown_28"] = attribute.Unknown28,
            ["unknown_32"] = attribute.Unknown32,
        }).ToArray();
    }
}
