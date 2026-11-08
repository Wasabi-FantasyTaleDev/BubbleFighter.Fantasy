using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class FinalZoneBinding : IInboundMessageBinding<RoundFinalZoneDroneDestroyed>,
    IInboundMessageBinding<RoundFinalZoneGoldenSupplyRequested>, IMessageSender<RoundFinalZoneDroneSpawned>,
    IMessageSender<RoundFinalZoneDroneBoxSpawned>, IMessageSender<RoundFinalZoneGoldenSupplySpawned>
{
    private void MountSupplies(IPluginContext context)
    {
        RequireSupplyLayouts(packets, build);
        var destroyed = table.C2S("gcpArea32DroneDestroyed_");
        var golden = table.C2S("gcpArea32GoldenSupply_");
        _ = table.S2C("gspArea32DroneSpawn_");
        _ = table.S2C("gspArea32DroneBoxSpawn_");
        _ = table.S2C("gspArea32GoldenSupplySpawn_");
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var router = services.Get<IPacketRouter>();
        context.Effect(router.Register(protocol, destroyed, async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadDroneDestroyed(packets, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("No FinalZone drone-destruction handler is installed.");
        }));
        context.Effect(router.Register(protocol, golden, async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadGoldenSupply(packets, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("No FinalZone golden-supply handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinalZoneDroneDestroyed>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinalZoneGoldenSupplyRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneDroneSpawned>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneDroneBoxSpawned>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneGoldenSupplySpawned>>(this));
    }

    /// <summary>Creates the native drone with a shared flight seed.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneDroneSpawned message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32DroneSpawn_"), WriteDroneSpawn(packets, build, message), ct);

    /// <summary>Publishes the accepted destruction and the resulting seeded supply box.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneDroneBoxSpawned message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32DroneBoxSpawn_"), WriteDroneBox(packets, build, message), ct);

    /// <summary>Acknowledges the script's golden supply with its issued box handle and shared reward seed.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneGoldenSupplySpawned message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32GoldenSupplySpawn_"), WriteGoldenSupply(packets, build, message), ct);

    internal static RoundFinalZoneDroneDestroyed ReadDroneDestroyed(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "Area32DroneDestroyed", body);
        return new((int)fields["object_id"], SupplyCoordinate(fields, "x"), SupplyCoordinate(fields, "y"),
            SupplyCoordinate(fields, "z"), (int)fields["unknown_16"]);
    }

    internal static RoundFinalZoneGoldenSupplyRequested ReadGoldenSupply(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "Area32GoldenSupplyRequest", body);
        return new((int)fields["spawn_index"], SupplyCoordinate(fields, "x"), SupplyCoordinate(fields, "y"), SupplyCoordinate(fields, "z"));
    }

    private static float SupplyCoordinate(IReadOnlyDictionary<string, object> fields, string key) =>
        fields[key] is float value && float.IsFinite(value) ? value
            : throw new PacketFormatException("FinalZone supply position must be finite.", 0);

    internal static byte[] WriteDroneSpawn(PacketTables packets, string build, RoundFinalZoneDroneSpawned message) =>
        PacketCodec.Write(packets, build, "Area32DroneSpawn", new Dictionary<string, object?>
        {
            ["object_id"] = message.ObjectId, ["seed"] = message.Seed,
        });

    internal static byte[] WriteDroneBox(PacketTables packets, string build, RoundFinalZoneDroneBoxSpawned message) =>
        PacketCodec.Write(packets, build, "Area32DroneBoxSpawn", new Dictionary<string, object?>
        {
            ["drone"] = new Dictionary<string, object?>
            {
                ["object_id"] = message.Drone.ObjectId, ["x"] = message.Drone.X, ["y"] = message.Drone.Y,
                ["z"] = message.Drone.Z, ["unknown_16"] = message.Drone.Unknown16,
            },
            ["box_object_id"] = message.BoxObjectId, ["seed"] = message.Seed,
        });

    internal static byte[] WriteGoldenSupply(PacketTables packets, string build, RoundFinalZoneGoldenSupplySpawned message) =>
        PacketCodec.Write(packets, build, "Area32GoldenSupplySpawn", new Dictionary<string, object?>
        {
            ["spawn"] = new Dictionary<string, object?>
            {
                ["spawn_index"] = message.Spawn.SpawnIndex, ["x"] = message.Spawn.X,
                ["y"] = message.Spawn.Y, ["z"] = message.Spawn.Z,
            },
            ["box_object_id"] = message.BoxObjectId, ["seed"] = message.Seed,
        });

    internal static void RequireSupplyLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "Area32DroneSpawn", "FinalZone drone", "object_id:u32,seed:u32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32DroneDestroyed", "FinalZone drone",
            "object_id:u32,x:f32,y:f32,z:f32,unknown_16:u32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32DroneBoxSpawn", "FinalZone drone box",
            "drone:Area32DroneDestroyed,box_object_id:u32,seed:u32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32GoldenSupplyRequest", "FinalZone golden supply",
            "spawn_index:u32,x:f32,y:f32,z:f32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32GoldenSupplySpawn", "FinalZone golden supply",
            "spawn:Area32GoldenSupplyRequest,box_object_id:u32,seed:u32");
    }
}
