using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class FinalZoneBinding : IMessageSender<RoundFinalZoneItemsSpawned>,
    IInboundMessageBinding<RoundFinalZoneItemsDropped>
{
    /// <summary>Creates explicitly described objects without invoking client-side seeded map generation.</summary>
    public async ValueTask SendAsync(ISession session, RoundFinalZoneItemsSpawned message, CancellationToken ct)
    {
        foreach (var body in WriteItemBatches(packets, build, message))
            await session.SendAsync(table.S2C("gspArea32ItemsSpawn_"), body, ct).ConfigureAwait(false);
    }

    internal static IEnumerable<byte[]> WriteItemBatches(PacketTables packets, string build, RoundFinalZoneItemsSpawned message)
    {
        var emptyLength = WriteItems(packets, build, new([])).Length;
        var batch = new List<FinalZoneMapItem>();
        var length = emptyLength;
        foreach (var item in message.Items)
        {
            var itemLength = PacketCodec.Write(packets, build, "Area32MapItem", ItemValues(item),
                maxBytes: ushort.MaxValue - emptyLength).Length;
            if (length > ushort.MaxValue - itemLength)
            {
                yield return WriteItems(packets, build, new(batch));
                batch.Clear();
                length = emptyLength;
            }
            batch.Add(item);
            length += itemLength;
        }
        if (batch.Count > 0 || message.Items.Count == 0)
            yield return WriteItems(packets, build, new(batch));
    }

    internal static void RequireItemLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "Area32MapItem", "FinalZone map item",
            "object_id:u32,item_spec_id:u32,quantity:u32,x:f32,y:f32,z:f32,unknown_24:u32,unknown_28:u32,unknown_32:u32,temporary:bool,reinforcement:Area32Reinforcement");
        PacketLayoutContract.RequireSignature(packets, build, "Area32ItemsSpawn", "FinalZone explicit items",
            "items:list<Area32MapItem>");
        PacketLayoutContract.RequireSignature(packets, build, "Area32ItemDrop", "FinalZone returned item",
            "item_spec_id:u32,quantity:u32,x:f32,y:f32,z:f32,unknown_20:u32,unknown_24:u32,unknown_28:u32,temporary:bool,reinforcement:Area32Reinforcement");
        PacketLayoutContract.RequireSignature(packets, build, "Area32ItemsDrop", "FinalZone returned items",
            "items:list<Area32ItemDrop>");
    }

    internal static RoundFinalZoneItemsDropped ReadItemsDrop(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.Read(packets, build, "Area32ItemsDrop", body);
        var items = ((object?[])values["items"]!).Cast<IReadOnlyDictionary<string, object?>>().Select(value =>
        {
            var reinforcement = (IReadOnlyDictionary<string, object?>)value["reinforcement"]!;
            var x = (float)value["x"]!;
            var y = (float)value["y"]!;
            var z = (float)value["z"]!;
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                throw new PacketFormatException("FinalZone returned-item positions must be finite.", 0);
            return new FinalZoneMapItem(0, (int)value["item_spec_id"]!, (int)value["quantity"]!, x, y, z,
                (int)value["unknown_20"]!, (int)value["unknown_24"]!, (int)value["unknown_28"]!, (bool)value["temporary"]!,
                new(ReadWords(reinforcement, "unknown_4"), ReadWords(reinforcement, "unknown_20")));
        }).ToArray();
        return new(Array.AsReadOnly(items));
    }

    internal static byte[] WriteItems(PacketTables packets, string build, RoundFinalZoneItemsSpawned message) =>
        PacketCodec.Write(packets, build, "Area32ItemsSpawn", new Dictionary<string, object?>
        {
            ["items"] = message.Items.Select(ItemValues).ToArray(),
        });

    private static Dictionary<string, object?> ItemValues(FinalZoneMapItem item) => new()
    {
        ["object_id"] = item.ObjectId, ["item_spec_id"] = item.ItemSpecId, ["quantity"] = item.Quantity,
        ["x"] = item.X, ["y"] = item.Y, ["z"] = item.Z,
        ["unknown_24"] = item.Unknown24, ["unknown_28"] = item.Unknown28, ["unknown_32"] = item.Unknown32,
        ["temporary"] = item.Temporary,
        ["reinforcement"] = new Dictionary<string, object?>
        {
            ["unknown_4"] = item.Reinforcement.Unknown4, ["unknown_20"] = item.Reinforcement.Unknown20,
        },
    };
}
