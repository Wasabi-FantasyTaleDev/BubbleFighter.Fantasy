using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Native FinalZone item and elimination messages over the owning game connection.</summary>
internal sealed partial class FinalZoneBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IInboundMessageBinding<RoundFinalZoneItemPickupRequested>, IMessageSender<RoundFinalZoneItemsSeeded>,
      IMessageSender<RoundFinalZoneItemPicked>, IMessageSender<RoundFinalZonePlayerEliminated>,
      IMessageSender<RoundFinalZoneWaterTimerChanged>
{
    /// <summary>Gets the game connection carrying the Area32 strategy's messages.</summary>
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        RequireItemLayouts(packets, build);
        var pickup = table.C2S("gcpArea32ItemPickup_");
        var itemDrop = table.C2S("gcpArea32ItemsDrop_");
        _ = table.S2C("gspArea32ItemSeed_");
        _ = table.S2C("gspArea32ItemPicked_");
        _ = table.S2C("gspArea32PlayerEliminated_");
        _ = table.S2C("gspArea32WaterTimer_");
        _ = table.S2C("gspArea32ItemsSpawn_");
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        context.Effect(services.Get<IPacketRouter>().Register(protocol, pickup, async (session, packet, ct) =>
        {
            var request = ReadPickup(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No FinalZone item-pickup handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinalZoneItemPickupRequested>>(this));
        context.Effect(services.Get<IPacketRouter>().Register(protocol, itemDrop, async (session, packet, ct) =>
        {
            var request = ReadItemsDrop(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No FinalZone item-return handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinalZoneItemsDropped>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneItemsSeeded>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneItemPicked>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZonePlayerEliminated>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneWaterTimerChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundFinalZoneItemsSpawned>>(this));
        MountBackpacks(context);
        MountSupplies(context);
    }

    /// <summary>Publishes the deterministic map-item generation parameters.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneItemsSeeded message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32ItemSeed_"), WriteSeed(packets, build, message), ct);

    /// <summary>Publishes the sole accepted recipient of a map item.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneItemPicked message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32ItemPicked_"), WritePickup(packets, build, message), ct);

    /// <summary>Updates the native survivor display and finalizes the eliminated character.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZonePlayerEliminated message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32PlayerEliminated_"), WriteElimination(packets, build, message), ct);

    /// <summary>Starts or stops the native water phase with integer phase and millisecond values.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneWaterTimerChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspArea32WaterTimer_"), WriteWaterTimer(packets, build, message), ct);

    internal static byte[] WriteWaterTimer(PacketTables packets, string build, RoundFinalZoneWaterTimerChanged message) =>
        PacketCodec.Write(packets, build, "Area32WaterTimer", new Dictionary<string, object?>
        {
            ["active"] = message.Active, ["stage"] = message.Stage, ["duration_ms"] = message.DurationMs,
        });

    internal static RoundFinalZoneItemPickupRequested ReadPickup(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "Area32ItemPickup", body);
        return new((int)fields["object_id"], (int)fields["actor"]);
    }

    internal static byte[] WriteSeed(PacketTables packets, string build, RoundFinalZoneItemsSeeded message) =>
        PacketCodec.Write(packets, build, "Area32ItemSeed", new Dictionary<string, object?>
        {
            ["seed"] = message.Seed, ["first_object_id"] = message.FirstObjectId, ["maximum_count"] = message.MaximumCount,
        });

    internal static byte[] WritePickup(PacketTables packets, string build, RoundFinalZoneItemPicked message) =>
        PacketCodec.Write(packets, build, "Area32ItemPickup", new Dictionary<string, object?>
        {
            ["object_id"] = message.ObjectId, ["actor"] = message.Actor,
        });

    internal static byte[] WriteElimination(PacketTables packets, string build, RoundFinalZonePlayerEliminated message) =>
        PacketCodec.Write(packets, build, "Area32PlayerEliminated", new Dictionary<string, object?>
        {
            ["actor"] = message.Actor, ["survivors_remaining"] = message.SurvivorsRemaining,
        });

    internal static void RequireLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "Area32ItemSeed", "FinalZone items",
            "seed:u32,maximum_count:u32,first_object_id:u32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32ItemPickup", "FinalZone items", "object_id:u32,actor:u32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32PlayerEliminated", "FinalZone elimination",
            "actor:u32,survivors_remaining:u32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32WaterTimer", "FinalZone water phases",
            "active:bool,stage:u32,duration_ms:u32");
    }
}
