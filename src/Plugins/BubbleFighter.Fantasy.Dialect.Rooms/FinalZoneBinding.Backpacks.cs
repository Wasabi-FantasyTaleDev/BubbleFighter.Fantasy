using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class FinalZoneBinding : IInboundMessageBinding<RoundFinalZoneBackpackDropped>,
    IInboundMessageBinding<RoundFinalZoneBackpackPickupRequested>, IMessageSender<RoundFinalZoneBackpackSpawned>,
    IMessageSender<RoundFinalZoneBackpackPicked>
{
    private void MountBackpacks(IPluginContext context)
    {
        RequireBackpackLayouts(packets, build);
        var drop = table.C2S("gcpArea32BackpackDrop_");
        var pickup = table.C2S("gcpArea32BackpackPickup_");
        _ = table.S2C("gspArea32BackpackSpawn_");
        _ = table.S2C("gspArea32BackpackPicked_");
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var router = services.Get<IPacketRouter>();
        context.Effect(router.Register(protocol, drop, async (session, packet, ct) =>
        {
            var request = ReadBackpackDrop(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No FinalZone backpack-drop handler is installed.");
        }));
        context.Effect(router.Register(protocol, pickup, async (session, packet, ct) =>
        {
            var request = ReadBackpackPickup(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No FinalZone backpack-pickup handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinalZoneBackpackDropped>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinalZoneBackpackPickupRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneBackpackSpawned>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneBackpackPicked>>(this));
    }

    /// <summary>Creates the round-authorized world backpack using the native descriptor.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneBackpackSpawned message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32BackpackSpawn_"), WriteBackpackSpawn(packets, build, message), ct);

    /// <summary>Publishes the accepted entry removal to all clients displaying the backpack.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneBackpackPicked message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32BackpackPicked_"), WriteBackpackPicked(packets, build, message), ct);

    internal static void RequireBackpackLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "Area32Reinforcement", "FinalZone reinforcement",
            "unknown_4:list<u32>,unknown_20:list<u32>");
        PacketLayoutContract.RequireSignature(packets, build, "Area32BackpackEntry", "FinalZone backpack entry",
            "item_spec_id:u32,weapon_id:u32,quantity:u32,reinforcement:Area32Reinforcement");
        PacketLayoutContract.RequireSignature(packets, build, "Area32BackpackDrop", "FinalZone backpack drop",
            "owner_actor:u32,x:f32,y:f32,z:f32,unknown_16:u32,unknown_20:u32,unknown_24:u32,unknown_28:wstr,items:list<Area32BackpackEntry>");
        PacketLayoutContract.RequireSignature(packets, build, "Area32BackpackSpawn", "FinalZone backpack spawn",
            "object_id:u32,contents:Area32BackpackDrop");
        PacketLayoutContract.RequireSignature(packets, build, "Area32BackpackPickup", "FinalZone backpack pickup",
            "object_id:u32,item_spec_id:u32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32BackpackPicked", "FinalZone backpack removal",
            "user_number:u32,object_id:u32,item_spec_id:u32");
    }

    internal static RoundFinalZoneBackpackDropped ReadBackpackDrop(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(packets, build, "Area32BackpackDrop", body);
        var entries = ((object?[])fields["items"]!).Cast<IReadOnlyDictionary<string, object?>>().Select(entry =>
        {
            var reinforcement = (IReadOnlyDictionary<string, object?>)entry["reinforcement"]!;
            return new FinalZoneBackpackEntry((int)entry["item_spec_id"]!, (int)entry["weapon_id"]!, (int)entry["quantity"]!,
                new(ReadWords(reinforcement, "unknown_4"), ReadWords(reinforcement, "unknown_20")));
        }).ToArray();
        var x = (float)fields["x"]!;
        var y = (float)fields["y"]!;
        var z = (float)fields["z"]!;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new PacketFormatException("FinalZone backpack coordinates must be finite.", 4);
        return new((int)fields["owner_actor"]!, x, y, z, (int)fields["unknown_16"]!, (int)fields["unknown_20"]!,
            (int)fields["unknown_24"]!, (string)fields["unknown_28"]!, Array.AsReadOnly(entries));
    }

    internal static RoundFinalZoneBackpackPickupRequested ReadBackpackPickup(PacketTables packets, string build,
        ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "Area32BackpackPickup", body);
        return new((int)fields["object_id"], (int)fields["item_spec_id"]);
    }

    internal static byte[] WriteBackpackSpawn(PacketTables packets, string build, RoundFinalZoneBackpackSpawned message) =>
        PacketCodec.Write(packets, build, "Area32BackpackSpawn", new Dictionary<string, object?>
        {
            ["object_id"] = message.ObjectId, ["contents"] = BackpackValues(message.Contents),
        });

    internal static byte[] WriteBackpackPicked(PacketTables packets, string build, RoundFinalZoneBackpackPicked message) =>
        PacketCodec.Write(packets, build, "Area32BackpackPicked", new Dictionary<string, object?>
        {
            ["user_number"] = message.UserNumber, ["object_id"] = message.ObjectId, ["item_spec_id"] = message.ItemSpecId,
        });

    private static IReadOnlyList<int> ReadWords(IReadOnlyDictionary<string, object?> values, string key) =>
        Array.AsReadOnly(((object?[])values[key]!).Select(value => (int)value!).ToArray());

    private static Dictionary<string, object?> BackpackValues(RoundFinalZoneBackpackDropped message) => new()
    {
        ["owner_actor"] = message.OwnerActor, ["x"] = message.X, ["y"] = message.Y, ["z"] = message.Z,
        ["unknown_16"] = message.Unknown16, ["unknown_20"] = message.Unknown20, ["unknown_24"] = message.Unknown24,
        ["unknown_28"] = message.Unknown28,
        ["items"] = message.Items.Select(entry => new Dictionary<string, object?>
        {
            ["item_spec_id"] = entry.ItemSpecId, ["weapon_id"] = entry.WeaponId, ["quantity"] = entry.Quantity,
            ["reinforcement"] = new Dictionary<string, object?>
            {
                ["unknown_4"] = entry.Reinforcement.Unknown4, ["unknown_20"] = entry.Reinforcement.Unknown20,
            },
        }).ToArray(),
    };
}
