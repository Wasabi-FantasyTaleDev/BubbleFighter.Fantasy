using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed class SupplyBinding(string protocol, IOpcodeTable table, PacketTables tables, string build, int limit)
    : IInboundMessageBinding<RoundSuppliesReported>, IInboundMessageBinding<RoundSupplyPickupRequested>, IInboundMessageBinding<RoundSupplyDropRequested>,
      IMessageSender<RoundSupplyCreated>, IMessageSender<RoundSupplyPicked>,
      IInboundMessageBinding<RoundItemUseRequested>, IMessageSender<RoundItemUsed>,
      IInboundMessageBinding<RoundItemSlotSwitchRequested>, IMessageSender<RoundItemSlotChanged>
{
    public string Protocol => protocol;

    internal Func<ISession, RoundSuppliesReported, CancellationToken, ValueTask<bool>>? MapReportFirst { get; init; }

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "map_supplies"))
            throw new ConfigException("supply_items requires audited map, drop and slot consumers.");
        foreach (var name in new[] { "SupplyMapReport", "SupplyPosition", "SupplyPickup", "SupplyCreate", "SupplyPicked", "SupplyDropRequest" })
            _ = tables.Resolve(build, name);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundSuppliesReported>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundSupplyPickupRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundSupplyDropRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundSupplyCreated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundSupplyPicked>>(this));
        var itemUse = table.Find(PacketDirection.ServerToClient, "gspItemUsed") is not null;
        if (itemUse)
        {
            _ = tables.Resolve(build, "SupplyItemUsed");
            _ = tables.Resolve(build, "SupplyItemGranted");
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundItemUseRequested>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<RoundItemUsed>>(this));
        }
        var router = context.Services.Get<IPacketRouter>();
        if (table.Find(PacketDirection.ServerToClient, "gspSwitchItemSlot") is not null)
        {
            _ = tables.Resolve(build, "SupplyItemSlotChanged");
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundItemSlotSwitchRequested>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<RoundItemSlotChanged>>(this));
            context.Effect(router.Register(protocol, table.C2S("gcpSwitchItemSlot"), async (session, packet, ct) =>
            {
                if (packet.Payload.Length != 0)
                    throw new PacketFormatException("Native item-slot switch request must be empty.", 0);
                if (!await messages.DispatchAsync(protocol, session, new RoundItemSlotSwitchRequested(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Native item-slot switch has no typed handler.");
            }));
        }
        if (itemUse)
            context.Effect(router.Register(protocol, table.C2S("gcpUseItem"), async (session, packet, ct) =>
            {
                if (packet.Payload.Length != 0)
                    throw new PacketFormatException("Native item-use request must be empty.", 0);
                if (!await messages.DispatchAsync(protocol, session, new RoundItemUseRequested(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Native item use has no typed handler.");
            }));
        context.Effect(router.Register(protocol, table.C2S("gcpReqCreateItem"), async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadDrop(tables, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Dropped items have no typed handler.");
        }));
        context.Effect(router.Register(protocol, table.C2S("gcpGameReportMapData"), async (session, packet, ct) =>
        {
            var report = ReadReport(tables, build, packet.Payload.Span, limit);
            if (MapReportFirst is not null && await MapReportFirst(session, report, ct).ConfigureAwait(false))
                return;
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Map supplies have no typed handler.");
        }));
        context.Effect(router.Register(protocol, table.C2S("gcpEatItem"), async (session, packet, ct) =>
        {
            var values = PacketCodec.ReadScalars(tables, build, "SupplyPickup", packet.Payload.Span);
            var request = new RoundSupplyPickupRequested((int)values["object_id"], (int)values["kind"]);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Supply pickup has no typed handler.");
        }));
    }

    private const int OrdinaryCreationClass = 10000;

    internal static RoundSupplyDropRequested ReadDrop(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "SupplyDropRequest", body);
        var x = (float)values["x"];
        var y = (float)values["y"];
        var z = (float)values["z"];
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new PacketFormatException("Dropped item coordinates are not finite.", 0);
        // A request without the classification word (cn-279 sub_404180 reads six floats and the kind) gets the value the
        // other builds send for an ordinary throw (kr-812 sub_5145F0); cn-279 sub_81FF70 only tests the word against 14.
        var classified = values.TryGetValue("unknown_36", out var creationClass);
        return new(new(x, y, z, (int)values["kind"], 0, 0), classified ? (int)creationClass! : OrdinaryCreationClass);
    }

    internal static RoundSuppliesReported ReadReport(PacketTables tables, string build, ReadOnlySpan<byte> body, int limit)
    {
        var values = PacketCodec.Read(tables, build, "SupplyMapReport", body,
            new PacketReadLimits(MaxCollectionElements: limit, MaxValues: checked(16 + 24 * limit), MaxDepth: 3));
        // Unknown position vectors still belong to the native report and must be finite.
        foreach (var field in tables.Resolve(build, "SupplyMapReport"))
        {
            if (field.Type != "list<SupplyPosition>") continue;
            foreach (var entry in (object?[])values[field.Name]!)
                if (((Dictionary<string, object?>)entry!).Values.Any(value => value is not float axis || !float.IsFinite(axis)))
                    throw new PacketFormatException("Supply position is not finite.", 0);
        }
        var bodyLength = body.Length;
        var positions = (object?[])values["positions"]!;
        var kinds = (object?[])values["kinds"]!;
        // A build whose report states no interval vectors reports zero bounds; the round applies its default.
        var stated = values.ContainsKey("regeneration_min_ms");
        var minimum = stated ? (object?[])values["regeneration_min_ms"]! : new object?[positions.Length];
        var maximum = stated ? (object?[])values["regeneration_max_ms"]! : new object?[positions.Length];
        // A build with team boxes reports one team word per point; one without has no such vector.
        var teams = values.TryGetValue("team", out var teamVector) ? (object?[])teamVector! : null;
        if (kinds.Length != positions.Length || minimum.Length != positions.Length || maximum.Length != positions.Length
            || (teams is not null && teams.Length != positions.Length))
            throw new PacketFormatException("Supply point vectors have different lengths.", body.Length);
        return new RoundSuppliesReported(positions.Select((entry, i) =>
        {
            var point = (Dictionary<string, object?>)entry!;
            var team = teams is null ? 0 : (int)teams[i]!;
            // The host's map classification yields only 0, 1 and 2 (kr-812 sub_50C840).
            if (team is < 0 or > 2)
                throw new PacketFormatException("Supply point team is not 0, 1 or 2.", bodyLength);
            return new RoundSupplyPoint((float)point["x"]!, (float)point["y"]!, (float)point["z"]!,
                (int)kinds[i]!, stated ? (int)minimum[i]! : 0, stated ? (int)maximum[i]! : 0, team);
        }).ToArray()) { IntervalsStated = stated };
    }

    public ValueTask SendAsync(ISession session, RoundSupplyCreated message, CancellationToken ct)
    {
        var fields = new Dictionary<string, object?> { ["object_id"] = message.ObjectId, ["kind"] = message.Point.Kind,
            ["unknown_8"] = message.CreationClass, ["x"] = message.Point.X, ["y"] = message.Point.Y, ["z"] = message.Point.Z };
        // The envelope of a build with team boxes ends in the box's team (kr-812 sub_636F20); the other builds have no such word.
        if (tables.Resolve(build, "SupplyCreate").Any(field => field.Name == "team"))
            fields["team"] = message.Point.Team;
        else if (message.Point.Team != 0)
            throw new InvalidOperationException("A team box was created for a build whose creation envelope has no team word.");
        return session.SendAsync(table.S2C("gspGameCreateGameItem"), PacketCodec.Write(tables, build, "SupplyCreate", fields), ct);
    }

    public async ValueTask SendAsync(ISession session, RoundSupplyPicked message, CancellationToken ct)
    {
        await session.SendAsync(table.S2C("gspItemEaten"), PacketCodec.Write(tables, build, "SupplyPicked",
            new Dictionary<string, object?> { ["object_id"] = message.ObjectId, ["seat"] = message.Seat }), ct).ConfigureAwait(false);
        if (message.ItemKind > 0)
            await session.SendAsync(table.S2C("gspAddItemToSlot"), PacketCodec.Write(tables, build, "SupplyItemGranted",
                new Dictionary<string, object?> { ["seat"] = message.Seat, ["kind"] = message.ItemKind, ["item_slot"] = message.ItemSlot }), ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, RoundItemUsed message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspItemUsed"), PacketCodec.Write(tables, build, "SupplyItemUsed",
            new Dictionary<string, object?> { ["seat"] = message.Seat, ["kind"] = message.Kind, ["item_slot"] = message.ItemSlot }), ct);

    public ValueTask SendAsync(ISession session, RoundItemSlotChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspSwitchItemSlot"), PacketCodec.Write(tables, build, "SupplyItemSlotChanged",
            new Dictionary<string, object?> { ["seat"] = message.Seat, ["item_slot"] = message.ItemSlot, ["kind"] = message.Kind,
                ["result"] = message.Rejected ? 1 : 0 }), ct);

}
