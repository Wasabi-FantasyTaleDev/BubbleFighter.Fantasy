using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The legacy plaza as typed messages. The plaza is the same session structure as a room (the
/// client keeps one session context for both, with members in seats), so the packets reuse the
/// seat-record writers. A complete install replaces the previous scene, while occupied and
/// vacant <c>gspRoomLeaveSlot</c> records reconcile the actors of players already present.
/// </summary>
internal sealed partial class PlayRoomCodec
    : IInboundMessageBinding<PlazaEnterRequested>, IInboundMessageBinding<PlazaPositionReported>,
      IInboundMessageBinding<PlazaMenuReturnRequested>,
      IMessageSender<PlazaEntered>, IMessageSender<PlazaMemberEntered>, IMessageSender<PlazaMemberLeft>,
      IMessageSender<PlazaPositionReceived>, IMessageSender<PlazaMenuReturnRequested>
{
    /// <summary>
    /// Every packet the plaza family needs. The plaza is one feature: a build whose table cannot
    /// name all of them gets none of it, rather than a burst that dies half way through.
    /// </summary>
    private static readonly string[] PlazaPacketNames =
    [
        "gcpReqGotoLobby", "gspGotoLobby", "gspRepMoveInto", "gspRoomSetSlot", "gspRoomLeaveSlot",
        "gspReceiveSessionChrPos", "gspGotoRoom",
    ];

    // Optional room frames are emitted only when the build's own table names them.
    private ValueTask SendWhereNamedAsync(ISession session, string name, Func<byte[]> body, CancellationToken ct) =>
        tables.Get(session.Profile.Id).Find(PacketDirection.ServerToClient, name) is { } code
            ? session.SendAsync(code, body(), ct)
            : ValueTask.CompletedTask;

    // Whether this build's table named the whole plaza family and the plaza is bound.
    private bool _plazaMounted;

    /// <summary>Registers the plaza request opcodes; only builds whose table names the whole family get them.</summary>
    /// <param name="context">The dialect row's context.</param>
    private void MountPlaza(IPluginContext context)
    {
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var router = services.Get<IPacketRouter>();
        var table = tables.ForProtocol(protocol);
        var missing = PlazaPacketNames.Where(name => table.Find(PacketDirection.ClientToServer, name) is null
            && table.Find(PacketDirection.ServerToClient, name) is null).ToArray();
        if (missing.Length > 0)
        {
            logger.LogInformation("dialect.rooms: {Protocol} has no {Names}; the plaza is not bound.", protocol,
                string.Join(", ", missing));
            // The client still waits for its move to the lobby: answer the request with the one
            // packet that switches its stage (gspGotoLobby, notice clear) and nothing else.
            if (table.Find(PacketDirection.ClientToServer, "gcpReqGotoLobby") is { } request
                && table.Find(PacketDirection.ServerToClient, "gspGotoLobby") is { } reply)
                context.Effect(router.Register(protocol, request, (session, _, ct) => session.SendAsync(reply, RoomCodec.WriteSceneTransition(packetTables, lineage), ct)));
            return;
        }
        _plazaMounted = true;
        context.Effect(services.RegisterMany<IInboundMessageBinding<PlazaEnterRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<PlazaPositionReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<PlazaEntered>>(this));
        context.Effect(services.RegisterMany<IMessageSender<PlazaMemberEntered>>(this));
        context.Effect(services.RegisterMany<IMessageSender<PlazaMemberLeft>>(this));
        context.Effect(services.RegisterMany<IMessageSender<PlazaPositionReceived>>(this));

        void Bind<T>(ushort opcode, string name, Func<ReadOnlySpan<byte>, T> decode) where T : IMessage
        {
            context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
            {
                T message;
                try
                {
                    message = decode(packet.Payload.Span);
                }
                catch (PacketFormatException ex)
                {
                    logger.LogError("Session {Id} {Name} does not match the expected layout ({Reason}); closing.",
                        session.Id, name, ex.Message);
                    await session.Connection.CloseAsync($"malformed {name}").ConfigureAwait(false);
                    return;
                }
                if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                    logger.LogWarning("Session {Id} {Name} has no handler on {Protocol}.", session.Id, name, protocol);
            }));
        }

        Bind(table.C2S("gcpReqGotoLobby"), "gcpReqGotoLobby", body =>
        {
            if (body.Length != 0)
                throw new PacketFormatException($"gcpReqGotoLobby must be empty; got {body.Length} byte(s).", 0);
            return new PlazaEnterRequested();
        });
        if (table.Find(PacketDirection.ClientToServer, "gcpSendSessionChrPos") is { } position)
        {
            _ = packetTables.Resolve(lineage, "PlazaPositionUpload");
            _ = packetTables.Resolve(lineage, "PlazaPositionBroadcast");
            Bind(position, "gcpSendSessionChrPos", body => ReadPositionUpload(packetTables, lineage, body));
        }
        if (table.Find(PacketDirection.ClientToServer, "gcpReqGotoMenu") is { } gotoMenu
            && table.Find(PacketDirection.ServerToClient, "gspGotoMenu") is not null)
        {
            context.Effect(services.RegisterMany<IInboundMessageBinding<PlazaMenuReturnRequested>>(this));
            context.Effect(services.RegisterMany<IMessageSender<PlazaMenuReturnRequested>>(this));
            Bind(gotoMenu, "gcpReqGotoMenu", body =>
            {
                // Empty (senders sub_5EC0D0 / sub_5EDD80 / sub_68F010, no payload fields).
                if (body.Length != 0)
                    throw new PacketFormatException($"gcpReqGotoMenu must be empty; got {body.Length} byte(s).", 0);
                return new PlazaMenuReturnRequested();
            });
        }
    }

    /// <summary>Validates the upload through its table; kr-812 sub_934030 writes XYZ and unresolved scalar fields.</summary>
    internal static PlazaPositionReported ReadPositionUpload(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        _ = PacketCodec.ReadScalars(tables, build, "PlazaPositionUpload", body);
        return new PlazaPositionReported(body.ToArray());
    }

    /// <summary>Installs the complete plaza session before the native stage creates its actors and entry movement.</summary>
    public async ValueTask SendAsync(ISession session, PlazaEntered message, CancellationToken ct)
    {
        // cn-279 sub_6FF6C0 and kr-812 sub_820060 replace the local slot and clear the previous scene's actors.
        await session.SendAsync(S2C(session, "gspGotoRoom"), WritePlazaInstall(packetTables, lineage, schema, message, session), ct)
            .ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspGotoLobby"), RoomCodec.WriteSceneTransition(packetTables, lineage), ct).ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspRepMoveInto"), WritePlazaMoveInto(packetTables, lineage, PlayerChannel), ct).ConfigureAwait(false);
    }

    /// <summary>Installs an entrant's equipment before reconciling its occupied seat into the existing plaza scene.</summary>
    public async ValueTask SendAsync(ISession session, PlazaMemberEntered message, CancellationToken ct)
    {
        // cn-279 sub_708960 caches equipment but only updates existing actors; cn-279 sub_708040 creates missing actors.
        var allowed = NativePlazaRecruitment.Allowed(session, message.Member.UserNumber);
        await session.SendAsync(S2C(session, "gspRoomSetSlot"), WritePlazaSeatSlot(packetTables, lineage, schema, message.Member, allowed), ct)
            .ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspRoomLeaveSlot"), WritePlazaPresence(packetTables, lineage, schema, message.Member, allowed), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Clears the departed account from the native seat so the plaza removes its actor.</summary>
    public ValueTask SendAsync(ISession session, PlazaMemberLeft message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomLeaveSlot"), WritePlazaLeaveSeat(packetTables, lineage, schema, message.Member), ct);

    /// <summary>Forwards actual client movement; the native scene owns the entry transform and movement.</summary>
    public ValueTask SendAsync(ISession session, PlazaPositionReceived message, CancellationToken ct) =>
        message.Body.IsEmpty ? ValueTask.CompletedTask
            : session.SendAsync(S2C(session, "gspReceiveSessionChrPos"),
                WritePlazaPositionLine(packetTables, lineage, message.UserNumber, message.Sequence, message.Body.Span), ct);

    /// <summary>Replaces the plaza or tutorial session with the local player's one-seat menu before switching stages.</summary>
    public ValueTask SendAsync(ISession session, PlazaMenuReturnRequested message, CancellationToken ct)
    {
        var member = message.You ?? throw new InvalidOperationException("Menu return requires the committed own appearance.");
        return session.SendAsync(S2C(session, "gspGotoRoom"), RoomCodec.WriteRoomEnvelope(packetTables, lineage, schema,
            new RoomValues(RoomCodec.MainMenuKind, [PlazaSlot(member)]) { PartyTransition = message.PartyTransition }), ct);
    }

    /// <summary>Writes a complete plaza session with indexed vacant gaps and an explicit local seat.</summary>
    internal static byte[] WritePlazaInstall(PacketTables tables, string build, RoomSchema schema, PlazaEntered message,
        ISession? recipient = null)
    {
        var members = message.Members.Append(message.You).ToArray();
        if (members.Any(member => member.Slot is < 0 or >= 36)
            || members.Select(member => member.Slot).Distinct().Count() != members.Length)
            throw new ArgumentException("A plaza install requires unique native seats below 36.", nameof(message));
        var bySlot = members.ToDictionary(member => member.Slot);
        var slots = Enumerable.Range(0, members.Max(member => member.Slot) + 1)
            .Select(slot => bySlot.TryGetValue(slot, out var member) ? PlazaSlot(member) : RoomSlotValues.Empty(false)).ToArray();
        var users = slots.Select(slot => RoomCodec.PlaySeatValues(schema, slot, equipment: false)).ToArray();
        foreach (var member in members)
        {
            NativePlazaRecruitment.Project(tables, build, users[member.Slot], NativePlazaRecruitment.Allowed(recipient, member.UserNumber));
            NativePlazaRecruitment.Project(tables, build, users[member.Slot], member.Recruitment);
        }
        var setting = RoomCodec.PlaySessionValues(schema, new RoomValues(3, slots) { Play = true });
        // cn-279 sub_6FF6C0 and kr-812 sub_820060 install both indexed vectors before selecting kind 3's plaza stage.
        return PacketCodec.Write(tables, build, "gspGotoRoom", new Dictionary<string, object?>
        {
            ["session_setting"] = setting,
            ["game_setting"] = setting["game_setting"],
            ["local_slot"] = message.You.Slot, ["master_slot"] = message.You.Slot,
            ["user_slots"] = users,
            ["equipped_item_lists"] = slots.Select(slot => RoomCodec.PlaySeatValues(schema, slot, equipment: true)).ToArray(),
            ["party_transition"] = message.PartyTransition,
        });
    }

    /// <summary>Writes an occupied seat through the native actor reconciliation envelope.</summary>
    internal static byte[] WritePlazaPresence(PacketTables tables, string build, RoomSchema schema, PlazaMember member, bool canRecruit = true) =>
        RoomCodec.WriteSeatEnvelope(tables, build, schema, member.Slot, PlazaSlot(member), leaving: true,
            canRecruit: canRecruit, recruitment: member.Recruitment);

    /// <summary>Writes <c>gspReceiveSessionChrPos</c>: u32 user number, u32 sequence, the reported 18-byte tail verbatim.</summary>
    internal static byte[] WritePlazaPositionLine(PacketTables tables, string build, int userNumber, int sequence, ReadOnlySpan<byte> reported)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "PlazaPositionUpload", reported);
        return PacketCodec.Write(tables, build, "PlazaPositionBroadcast", new Dictionary<string, object?>
        {
            ["user_number"] = userNumber, ["sequence"] = sequence,
            ["x"] = fields["x"], ["y"] = fields["y"], ["z"] = fields["z"],
            ["unknown_24"] = fields["unknown_20"], ["unknown_25"] = fields["unknown_21"],
            ["unknown_28"] = fields["unknown_24"],
        });
    }

    /// <summary>
    /// Writes the scene move through the build's audited table. Its <c>scene</c> word is the player's channel:
    /// kr-812 <c>sub_81C6F0</c> stores it at <c>ServerConnection+2432</c>, the slot the round start reads its
    /// <c>FighterConfigChannel</c> column from (kr-812 <c>sub_587690</c>, <c>sub_584320</c>).
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The build whose table is written.</param>
    /// <param name="channel">The row's player channel (<see cref="RoomsOptions.PlayerChannel"/>).</param>
    /// <returns>The body.</returns>
    internal static byte[] WritePlazaMoveInto(PacketTables tables, string build, int channel = RoomsOptions.FreeChannel) =>
        PacketCodec.Write(tables, build, "RoomMoveInto", new Dictionary<string, object?>
        {
            ["moved"] = true,
            ["scene"] = channel,
            ["unknown_12"] = 1,
        });

    /// <summary>Writes the optional session initialization through the build's one-word table.</summary>
    internal static byte[] WritePlazaSessionInit(PacketTables tables, string build, int userNumber) =>
        PacketCodec.Write(tables, build, "RoomSessionInit", new Dictionary<string, object?>
        {
            ["user_number"] = userNumber,
        });

    /// <summary>
    /// Writes the standalone UserSlot in <c>gspRepNotifyChangeLevelIcon</c> through its native table.
    /// kr-812 sub_834FE0 constructs UserSlot at +8; kr-812 sub_83C040 reads only that nested object, and
    /// sub_691CD0 copies it over the member's seat. The recorded plaza entry carries the main-menu record
    /// (closed, seated and the three flags after ready set),
    /// with the member's own level where the menu record holds a constant.
    /// </summary>
    internal static byte[] WritePlazaSeatRecord(PacketTables tables, string build, RoomSchema schema, PlazaMember member, bool canRecruit = true)
    {
        var slot = PlazaSlot(member);
        var values = RoomCodec.MenuSeatValues(schema, slot);
        values["level"] = RoomCodec.PlaySeatValues(schema, slot, equipment: false)["level"];
        NativePlazaRecruitment.Project(tables, build, values, canRecruit);
        NativePlazaRecruitment.Project(tables, build, values, member.Recruitment);
        return PacketCodec.Write(tables, build, "UserSlot", values);
    }

    /// <summary>
    /// Writes <c>gspRoomSetSlot</c> (reader <c>sub_80C1B0</c>): the seat index, the member's
    /// <c>UserSlot</c> and <c>EquippedItemList</c> — the plaza record is one of the room's.
    /// </summary>
    internal static byte[] WritePlazaSeatSlot(PacketTables tables, string build, RoomSchema schema, PlazaMember member, bool canRecruit = true) =>
        RoomCodec.WriteSeatEnvelope(tables, build, schema, member.Slot, PlazaSlot(member),
            canRecruit: canRecruit, recruitment: member.Recruitment);

    /// <summary>Clears the account identity as well as occupancy so native plaza actor lookup no longer finds the departed player.</summary>
    internal static byte[] WritePlazaLeaveSeat(PacketTables tables, string build, RoomSchema schema, PlazaMember member) =>
        // cn-279 sub_703FF0 and kr-812 sub_6CC6F0 scan account numbers without checking occupancy.
        RoomCodec.WriteSeatEnvelope(tables, build, schema, member.Slot, RoomSlotValues.Empty(false), leaving: true);

    private static RoomSlotValues PlazaSlot(PlazaMember member) =>
        new(true, false, member.UserNumber, member.Nickname, member.CharacterItemId,
            member.EquippedItemIds.Count > 0 ? member.EquippedItemIds : [member.CharacterItemId], false)
        {
            Level = Math.Max(1, member.Level), ColorIndex = member.ColorIndex,
            ItemAttributes = member.ItemAttributes,
        };
}
