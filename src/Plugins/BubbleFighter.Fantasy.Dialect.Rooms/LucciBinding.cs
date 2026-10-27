using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Binds the legacy NPC-kill Lucci packets: the create requests (C2S <c>gcpReqCreateLucci</c>
/// and, where the build has it, <c>gcpReqCreateHordeAward</c>), the item creation
/// (<c>gspGameCreateLucci</c>), the pickup (<c>gcpEatLucci</c>) and its confirmation
/// (<c>gspLucciEaten</c>). Layouts are the <c>packets/lucci-drops.yaml</c> structures, so a
/// build differs only in its opcode numbers and in whether the award request exists.
/// </summary>
internal sealed class LucciBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundLucciDropRequested>, IInboundMessageBinding<RoundHordeAwardRequested>,
      IInboundMessageBinding<RoundLucciPickupRequested>, IMessageSender<RoundLucciCreated>, IMessageSender<RoundLucciPicked>
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <summary>Whether this build sends the horde-award request and accepts award item kinds.</summary>
    internal bool HasHordeAward { get; private set; }

    /// <summary>Mounts the packets; a build without the audited capability or opcodes is a configuration error.</summary>
    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "lucci_drops"))
            throw new ConfigException("lucci_drops requires audited Lucci create and pickup consumers.");
        // The floats and words the handlers cast and the senders write: the drop request's point, kind and
        // killer tag, the created item, and the pickup pair in both directions.
        PacketLayoutContract.RequireFields(tables, build, "LucciDropRequest", "Lucci drops",
            ("x", "f32"), ("y", "f32"), ("z", "f32"),
            ("velocity_x", "f32"), ("velocity_y", "f32"), ("velocity_z", "f32"),
            ("kind", "u32"), ("unknown_36", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "LucciCreate", "Lucci drops",
            ("object_id", "u32"), ("kind", "u32"),
            ("x", "f32"), ("y", "f32"), ("z", "f32"),
            ("velocity_x", "f32"), ("velocity_y", "f32"), ("velocity_z", "f32"),
            ("unknown_36", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "LucciPickup", "Lucci drops",
            ("character_id", "u32"), ("object_id", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "LucciPicked", "Lucci drops",
            ("character_id", "u32"), ("object_id", "u32"));
        // A build with the award request must also read award kinds 3..5 (horde_award_items), or the
        // award item would be created through a handler that ignores or mishandles it.
        var awardOpcode = table.Find(PacketDirection.ClientToServer, "gcpReqCreateHordeAward");
        if (awardOpcode is not null && !tables.CapabilityOf(build, "horde_award_items"))
            throw new ConfigException("The horde-award request requires the horde_award_items capability.");
        // Resolve every opcode first so a missing row fails the mount instead of the first kill.
        _ = table.C2S("gcpReqCreateLucci");
        _ = table.C2S("gcpEatLucci");
        _ = table.S2C("gspGameCreateLucci");
        _ = table.S2C("gspLucciEaten");
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundLucciDropRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundLucciPickupRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundLucciCreated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundLucciPicked>>(this));
        context.Effect(router.Register(protocol, table.C2S("gcpReqCreateLucci"), async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadDrop(tables, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Lucci drops have no typed handler.");
        }));
        context.Effect(router.Register(protocol, table.C2S("gcpEatLucci"), async (session, packet, ct) =>
        {
            var values = PacketCodec.ReadScalars(tables, build, "LucciPickup", packet.Payload.Span);
            var request = new RoundLucciPickupRequested((int)values["character_id"], (int)values["object_id"]);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Lucci pickups have no typed handler.");
        }));
        if (awardOpcode is not { } award)
            return;
        // The same point and killer tag without a kind; ReadAward casts them back when the request arrives.
        PacketLayoutContract.RequireFields(tables, build, "HordeAwardRequest", "Lucci drops",
            ("x", "f32"), ("y", "f32"), ("z", "f32"),
            ("velocity_x", "f32"), ("velocity_y", "f32"), ("velocity_z", "f32"),
            ("unknown_32", "u32"));
        HasHordeAward = true;
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundHordeAwardRequested>>(this));
        context.Effect(router.Register(protocol, award, async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadAward(tables, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Horde awards have no typed handler.");
        }));
    }

    internal static RoundLucciDropRequested ReadDrop(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "LucciDropRequest", body);
        return new(ReadPoint(values), (int)values["kind"], (int)values["unknown_36"]);
    }

    internal static RoundHordeAwardRequested ReadAward(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "HordeAwardRequest", body);
        return new(ReadPoint(values), (int)values["unknown_32"]);
    }

    private static RoundLucciPoint ReadPoint(IReadOnlyDictionary<string, object> values)
    {
        var point = new RoundLucciPoint((float)values["x"], (float)values["y"], (float)values["z"],
            (float)values["velocity_x"], (float)values["velocity_y"], (float)values["velocity_z"]);
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)
            || !float.IsFinite(point.VelocityX) || !float.IsFinite(point.VelocityY) || !float.IsFinite(point.VelocityZ))
            throw new PacketFormatException("Dropped Lucci position or velocity is not finite.", 0);
        return point;
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, RoundLucciCreated message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspGameCreateLucci"), PacketCodec.Write(tables, build, "LucciCreate",
            new Dictionary<string, object?>
            {
                ["object_id"] = message.ObjectId, ["kind"] = message.Kind,
                ["x"] = message.Point.X, ["y"] = message.Point.Y, ["z"] = message.Point.Z,
                ["velocity_x"] = message.Point.VelocityX, ["velocity_y"] = message.Point.VelocityY,
                ["velocity_z"] = message.Point.VelocityZ, ["unknown_36"] = message.Tag,
            }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, RoundLucciPicked message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspLucciEaten"), PacketCodec.Write(tables, build, "LucciPicked",
            new Dictionary<string, object?> { ["character_id"] = message.CharacterObjectId, ["object_id"] = message.ObjectId }), ct);
}
