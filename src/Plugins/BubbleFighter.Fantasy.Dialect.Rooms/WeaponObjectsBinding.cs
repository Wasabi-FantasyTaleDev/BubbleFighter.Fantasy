using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts audited native weapon drops and pickups to shared round messages.</summary>
internal sealed class WeaponObjectsBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundWeaponDropRequested>, IInboundMessageBinding<RoundWeaponPickupRequested>,
      IMessageSender<RoundWeaponCreated>, IMessageSender<RoundWeaponPicked>
{
    public string Protocol => protocol;

    // kr-1256 adds a weapon slot word to both the request and the create notice; a build's tables say whether it has one.
    private bool Slotted => tables.Resolve(build, "WeaponDropRequest").Any(entry => entry.Name == "weapon_slot");

    // The launch vector then the position, six words that start after the slot word when there is one.
    private string[] VectorWords => Slotted
        ? ["unknown_24", "unknown_28", "unknown_32", "unknown_36", "unknown_40", "unknown_44"]
        : ["unknown_20", "unknown_24", "unknown_28", "unknown_32", "unknown_36", "unknown_40"];

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "weapon_objects"))
            throw new ConfigException("weapon_objects requires audited native creation and pickup consumers.");
        _ = table.C2S("gcpReqCreateWeaponObject");
        _ = table.C2S("gcpPickUpWeapon");
        _ = table.S2C("gspGameCreateWeaponObject");
        _ = table.S2C("gspPickUpWeapon");
        (string, string)[] request =
        [
            ("x", "f32"), ("y", "f32"), ("z", "f32"), ("velocity_x", "f32"), ("velocity_y", "f32"), ("velocity_z", "f32"),
            ("weapon_spec", "u32"), ("unknown_32", "u32"), ("unknown_36", "u32"), ("armed", "bool"),
        ];
        (string, string)[] slot = Slotted ? [("weapon_slot", "u32")] : [];
        PacketLayoutContract.RequireFields(tables, build, "WeaponDropRequest", "Weapon objects", [.. request, .. slot]);
        PacketLayoutContract.RequireFields(tables, build, "WeaponCreateNotice", "Weapon objects",
        [
            ("unknown_4", "u32"), ("unknown_8", "u32"), ("unknown_12", "u32"), ("unknown_16", "u32"), ("armed", "bool"),
            .. slot, .. VectorWords.Select(name => (name, "u32")),
        ]);
        PacketLayoutContract.RequireFields(tables, build, "WeaponPickup", "Weapon objects",
            ("weapon_object", "u32"), ("actor", "u32"));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundWeaponDropRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundWeaponPickupRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundWeaponCreated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundWeaponPicked>>(this));
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(router.Register(protocol, table.C2S("gcpReqCreateWeaponObject"), async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadDrop(tables, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Weapon drop has no typed round handler.");
        }));
        context.Effect(router.Register(protocol, table.C2S("gcpPickUpWeapon"), async (session, packet, ct) =>
        {
            // cn-324 sub_4CB0F0 and kr-1256 sub_498F50 write the weapon object first and the character second.
            var fields = PacketCodec.ReadScalars(tables, build, "WeaponPickup", packet.Payload.Span);
            var actor = (int)fields["actor"];
            var request = new RoundWeaponPickupRequested((int)fields["weapon_object"], actor, PlayRoomCodec.PlayerSeatOf(actor));
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Weapon pickup has no typed round handler.");
        }));
    }

    internal static RoundWeaponDropRequested ReadDrop(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "WeaponDropRequest", body);
        var result = new RoundWeaponDropRequested((float)fields["x"], (float)fields["y"], (float)fields["z"],
            (float)fields["velocity_x"], (float)fields["velocity_y"], (float)fields["velocity_z"],
            (int)fields["weapon_spec"], (int)fields["unknown_32"], (int)fields["unknown_36"], (bool)fields["armed"])
        {
            WeaponSlot = fields.TryGetValue("weapon_slot", out var slot) ? (int)slot! : 0,
        };
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z)
            || !float.IsFinite(result.VelocityX) || !float.IsFinite(result.VelocityY) || !float.IsFinite(result.VelocityZ)
            || result.WeaponSpec < 0)
            throw new PacketFormatException("Weapon drop position, velocity or specification is invalid.", 0);
        return result;
    }

    public ValueTask SendAsync(ISession session, RoundWeaponCreated message, CancellationToken ct)
    {
        var drop = message.Drop;
        static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
        // cn-324 sub_471630 supplies identity/spec, launch vector and weapon words to cn-324 sub_61BD60, then sets
        // position; kr-1256 sub_6ED290 reads the same order with the request's slot word after the two weapon words.
        var values = new Dictionary<string, object?>
        {
            ["unknown_4"] = message.ObjectId, ["unknown_8"] = drop.WeaponSpec,
            ["unknown_12"] = drop.Unknown32, ["unknown_16"] = drop.Unknown36,
            ["armed"] = drop.Unknown40,
        };
        int[] vector = [Bits(drop.VelocityX), Bits(drop.VelocityY), Bits(drop.VelocityZ), Bits(drop.X), Bits(drop.Y), Bits(drop.Z)];
        foreach (var (name, word) in VectorWords.Zip(vector)) values[name] = word;
        if (Slotted) values["weapon_slot"] = drop.WeaponSlot;
        return session.SendAsync(table.S2C("gspGameCreateWeaponObject"),
            PacketCodec.Write(tables, build, "WeaponCreateNotice", values), ct);
    }

    public ValueTask SendAsync(ISession session, RoundWeaponPicked message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspPickUpWeapon"), PacketCodec.Write(tables, build, "WeaponPickup",
            new Dictionary<string, object?> { ["weapon_object"] = message.ObjectId, ["actor"] = message.Actor }), ct);
}
