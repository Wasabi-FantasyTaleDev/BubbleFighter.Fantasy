using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Updates an already installed scene member through the build's dedicated couple-slot consumer.</summary>
internal sealed class CoupleSlotBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IMessageSender<RoomCoupleUpdated>, IMessageSender<PlazaCoupleUpdated>
{
    public string Protocol => protocol;

    /// <summary>Inherited layouts do not authorize a sender for a client whose update consumer was not read.</summary>
    internal static bool Supported(PacketTables tables, string build) =>
        tables.Structs.Contains("CoupleSlotUpdate", StringComparer.Ordinal)
        && tables.CapabilityOf(build, "couple")
        && tables.Resolve(build, "CoupleSlotUpdate") is { Count: > 0 } fields
        && fields.All(field => field.Origin == build);

    internal void Mount(IPluginContext context)
    {
        if (!Supported(packets, build)) return;
        RequireLayouts(packets, build);
        if (table.Profile.Id != build || table.Connection != OpcodeConnection.Game || protocol != build + "/game")
            throw new ConfigException("Couple scene updates require the audited build's game protocol and opcode table.");
        _ = table.S2C("gspUpdateCoupleSlot");
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomCoupleUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaCoupleUpdated>>(this));
    }

    internal static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireSignature(tables, build, "CoupleSlotUpdate", "Couple scene update",
            "user_slots:list<UserSlot>",
            "user_number:u32,couple_partner_user_num:u32,couple_partner_name:wstr,couple_unknown_flag_1:bool,couple_unknown_flag_2:bool");
        PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Couple scene update",
            ("user_number", "u32"), ("couple_partner_user_num", "u32"));
        var fields = tables.Resolve(build, "UserSlot");
        if (RoomRecords.UsesEquippedObjects(tables, build)
            || tables.Resolve(build, "CoupleSlotUpdate").Any(field => field.Name == "user_number"))
            PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Couple scene update",
                ("couple_partner_name", "wstr"), ("couple_unknown_flag_1", "bool"), ("couple_unknown_flag_2", "bool"));
        // cn-406 sub_736630 and id-400 sub_7BC460 only copy the partner account. Later legacy readers
        // add the wstr at +180 and a bool at +208; cn-404 sub_637E20 has no second relationship bool.
        else if (fields.Any(field => field is { Offset: 180, Type: "wstr" }))
            PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Couple scene update",
                ("couple_partner_name", "wstr"), ("couple_unknown_flag_1", "bool"));
    }

    public ValueTask SendAsync(ISession session, RoomCoupleUpdated message, CancellationToken ct) =>
        SendAsync(session, message.Member, ct);

    public ValueTask SendAsync(ISession session, PlazaCoupleUpdated message, CancellationToken ct) =>
        SendAsync(session, new RoomMemberView(message.Member.Slot, message.Member.UserNumber, message.Member.Nickname,
            message.Member.Level, 0, false, message.Member.CharacterItemId, message.Member.EquippedItemIds)
        {
            Couple = message.Member.Couple, ColorIndex = message.Member.ColorIndex,
            ItemAttributes = message.Member.ItemAttributes, Recruitment = message.Member.Recruitment,
        }, ct);

    private ValueTask SendAsync(ISession session, RoomMemberView member, CancellationToken ct)
    {
        if (session.Profile.Id != build || session.Connection.IsClosed)
            throw new InvalidOperationException("A couple scene update must use its recipient's live native game connection.");
        return session.SendAsync(table.S2C("gspUpdateCoupleSlot"), Write(packets, build, member), ct);
    }

    internal static byte[] Write(PacketTables tables, string build, RoomMemberView member)
    {
        RequireLayouts(tables, build);
        if (!Supported(tables, build))
            throw new InvalidOperationException("This build has no independently verified couple-slot update consumer.");
        if (member.UserNum <= 0 || member.Slot is < 0 or >= 36 || member.Ai is not null)
            throw new ArgumentException("A couple update requires an installed human scene member.", nameof(member));

        if (tables.Resolve(build, "CoupleSlotUpdate").Any(field => field.Name == "user_slots"))
        {
            Dictionary<string, object?> seat;
            if (RoomRecords.UsesEquippedObjects(tables, build))
                seat = PlazaRecords.Seat(tables, build, member);
            else
            {
                var schema = RoomTables.For(tables).Of(build).Schema;
                var slot = new RoomSlotValues(true, false, member.UserNum, member.Nickname,
                    member.CharacterItemId, member.EquippedItemIds, member.Ready)
                {
                    Couple = member.Couple, Level = member.Level, Team = member.Team,
                    ControllerSlot = member.Slot, ColorIndex = member.ColorIndex,
                    ItemAttributes = member.ItemAttributes, InventorySelection = member.InventorySelection,
                };
                seat = RoomCodec.PlaySeatValues(schema, slot, equipment: false);
            }
            // kr-812 sub_83B900, cn-774 sub_BC07C0 and tw-454 sub_A9D8F0 read a counted full UserSlot vector.
            return PacketCodec.Write(tables, build, "CoupleSlotUpdate", new Dictionary<string, object?>
            {
                ["user_slots"] = new[] { seat },
            });
        }

        // kr-1256 sub_9C31C0 reads a target account followed by sub_10E1B20's narrow relationship record.
        var values = new Dictionary<string, object?> { ["user_number"] = member.UserNum };
        NativeCoupleScene.Project(tables, build, values, member.Couple);
        return PacketCodec.Write(tables, build, "CoupleSlotUpdate", values);
    }
}
