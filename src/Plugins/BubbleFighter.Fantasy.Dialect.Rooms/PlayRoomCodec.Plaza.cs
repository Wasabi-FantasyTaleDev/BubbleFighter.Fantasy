using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The legacy plaza as typed messages. The plaza is the same session structure as a room (the
/// client keeps one session context for both, with members in seats), so the packets reuse the
/// seat-record writers: an entrant is announced with <c>gspRoomSetSlot</c>, a leaver with
/// <c>gspRoomLeaveSlot</c>. The burst order and the entry position walk follow
/// the kr-812 readers.
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
        "gspReceiveSessionChrPos",
    ];

    // Sends a packet of the burst only on a build that has it: cn-324 has neither the seat-record
    // refresh (gspRepNotifyChangeLevelIcon) nor gspReceiveSessionInit class.
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

    /// <summary>
    /// The entrant's burst, as recorded at s2c#1770-1800 after the leaving player's own packets:
    /// <c>gspGotoLobby</c>, their seat record twice, the scene move <c>gspRepMoveInto</c> twice,
    /// their own seat slot, their place at the plaza spawn, their session init, then one seat
    /// record per player already present. The positions streamed after that are the
    /// entrant's own reports coming back, so they are not produced here.
    /// </summary>
    public async ValueTask SendAsync(ISession session, PlazaEntered message, CancellationToken ct)
    {
        await session.SendAsync(S2C(session, "gspGotoLobby"), RoomCodec.WriteSceneTransition(packetTables, lineage, message.PartyTransition), ct).ConfigureAwait(false);
        // Delay the plaza burst until the pending lobby-stage switch has been consumed; the client constructs
        // plaza avatars while entering that stage.
        if (play.Wire.PlazaEntrySettleMs > 0)
            await Task.Delay(play.Wire.PlazaEntrySettleMs, ct).ConfigureAwait(false);
        await SendWhereNamedAsync(session, "gspRepNotifyChangeLevelIcon", () => WritePlazaSeatRecord(packetTables, lineage, schema, message.You), ct)
            .ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspRepMoveInto"), WritePlazaMoveInto(packetTables, lineage), ct).ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspRoomSetSlot"), WritePlazaSeatSlot(packetTables, lineage, schema, message.You), ct).ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspReceiveSessionChrPos"), WritePlazaWalkStep(packetTables, lineage, message.You.UserNumber, 1, message.You.EntrySeed), ct)
            .ConfigureAwait(false);
        await SendWhereNamedAsync(session, "gspReceiveSessionInit", () => WritePlazaSessionInit(packetTables, lineage, message.You.UserNumber), ct)
            .ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspRepMoveInto"), WritePlazaMoveInto(packetTables, lineage), ct).ConfigureAwait(false);
        await SendWhereNamedAsync(session, "gspRepNotifyChangeLevelIcon", () => WritePlazaSeatRecord(packetTables, lineage, schema, message.You), ct)
            .ConfigureAwait(false);
        foreach (var member in message.Members)
            await session.SendAsync(S2C(session, "gspRoomSetSlot"), WritePlazaSeatSlot(packetTables, lineage, schema, member), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An entrant as the players already present see them: their seat record, then their place at the plaza spawn so the avatar
    /// stands somewhere until the entrant's own reports arrive.
    /// </summary>
    public async ValueTask SendAsync(ISession session, PlazaMemberEntered message, CancellationToken ct)
    {
        await session.SendAsync(S2C(session, "gspRoomSetSlot"), WritePlazaSeatSlot(packetTables, lineage, schema, message.Member), ct).ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspReceiveSessionChrPos"), WritePlazaWalkStep(packetTables, lineage, message.Member.UserNumber, 1, message.Member.EntrySeed), ct)
            .ConfigureAwait(false);
    }

    /// <summary><c>gspRoomLeaveSlot</c> (reader <c>sub_8104E0</c>): the leaver's seat with the cleared record.</summary>
    public ValueTask SendAsync(ISession session, PlazaMemberLeft message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspRoomLeaveSlot"), WritePlazaLeaveSeat(packetTables, lineage, schema, message.Member), ct);

    /// <summary>
    /// <c>gspReceiveSessionChrPos</c> (reader <c>sub_92AC50</c>, handler <c>sub_806C40</c>): the
    /// sender's user number, their per-entry position sequence, then the reported position's
    /// 18-byte tail verbatim. The client skips its own user number, so echoing the sender back is
    /// safe and keeps the broadcast one shape for every recipient.
    /// </summary>
    public ValueTask SendAsync(ISession session, PlazaPositionReceived message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspReceiveSessionChrPos"),
            // A member who has not reported yet stands at the plaza spawn.
            message.Body.IsEmpty
                ? WritePlazaWalkStep(packetTables, lineage, message.UserNumber, 1, message.EntrySeed)
                : WritePlazaPositionLine(packetTables, lineage, message.UserNumber, message.Sequence, message.Body.Span), ct);

    /// <summary>
    /// <c>gspGotoMenu</c> (kr-812 sub_81FDA0): the party notice flag followed by a switch of the
    /// stage to the main menu and needs no other packet. The session the login install built stays.
    /// </summary>
    public ValueTask SendAsync(ISession session, PlazaMenuReturnRequested message, CancellationToken ct) =>
        session.SendAsync(S2C(session, "gspGotoMenu"), RoomCodec.WriteSceneTransition(packetTables, lineage, message.PartyTransition), ct);

    // The plaza spawn every entrant's first position line carries: the spawn column and height with the movement-state bytes (0x01,0x7F). The 20
    // lines after it (y sinking by 0.25 more each step, state 0x00 then 0x02) are the
    // client's own fall being reported and echoed. The start differs on every entry,
    // always inside x 697..743, z 512..551, so the column is picked per entry from that box.
    private const int PlazaWalkSteps = 21;
    private const int PlazaSpawnMinX = 697;
    private const int PlazaSpawnMaxX = 743;
    private const float PlazaWalkStartY = 179.6107177734375f;
    private const float PlazaWalkStepY = 0.25f;
    private const int PlazaSpawnMinZ = 512;
    private const int PlazaSpawnMaxZ = 551;
    private const byte PlazaWalkStateMoving = 0x01;
    private const byte PlazaWalkStateIdle = 0x00;
    private const byte PlazaWalkStateArrived = 0x02;
    private const byte PlazaWalkStateUnknown = 0x7F;

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
    /// Picks an entry's spawn column inside the recorded box. Deterministic in its inputs, so the
    /// entrant and every other receiver of the same entry place the member at the same spot.
    /// </summary>
    /// <param name="userNumber">The entrant's user number.</param>
    /// <param name="entrySeed">The value fixed for this entry.</param>
    internal static (float X, float Z) PlazaSpawnColumn(int userNumber, int entrySeed)
    {
        // A 64-bit finalizer-style mix of both inputs; x and z read independent bit ranges.
        var h = unchecked((ulong)(uint)userNumber * 0x9E3779B97F4A7C15UL + (ulong)(uint)entrySeed * 0xC2B2AE3D27D4EB4FUL);
        h ^= h >> 33;
        h = unchecked(h * 0xFF51AFD7ED558CCDUL);
        h ^= h >> 33;
        h = unchecked(h * 0xC4CEB9FE1A85EC53UL);
        h ^= h >> 33;
        var x = PlazaSpawnMinX + (int)((uint)h % (uint)(PlazaSpawnMaxX - PlazaSpawnMinX + 1));
        var z = PlazaSpawnMinZ + (int)((uint)(h >> 32) % (uint)(PlazaSpawnMaxZ - PlazaSpawnMinZ + 1));
        return (x, z);
    }

    /// <summary>Writes the recorded entry progression through the native broadcast table, retaining unresolved scalar defaults.</summary>
    /// <param name="tables">The audited packet tables.</param>
    /// <param name="build">The client build whose movement layout is written.</param>
    /// <param name="userNumber">The walking member's user number.</param>
    /// <param name="step">The 1-based step in the entry walk.</param>
    /// <param name="entrySeed">The member's per-entry value, which fixes the column every step shares.</param>
    internal static byte[] WritePlazaWalkStep(PacketTables tables, string build, int userNumber, int step, int entrySeed)
    {
        var (x, z) = PlazaSpawnColumn(userNumber, entrySeed);
        return PacketCodec.Write(tables, build, "PlazaPositionBroadcast", new Dictionary<string, object?>
        {
            ["user_number"] = userNumber, ["sequence"] = step,
            ["x"] = x, ["y"] = PlazaWalkStartY - PlazaWalkStepY * (step - 1) * (step - 1), ["z"] = z,
            ["unknown_24"] = step == 1 ? PlazaWalkStateMoving : step == PlazaWalkSteps ? PlazaWalkStateArrived : PlazaWalkStateIdle,
            ["unknown_25"] = PlazaWalkStateUnknown, ["unknown_28"] = 0f,
        });
    }

    /// <summary>Writes the scene move through the build's audited table.</summary>
    internal static byte[] WritePlazaMoveInto(PacketTables tables, string build) =>
        PacketCodec.Write(tables, build, "RoomMoveInto", new Dictionary<string, object?>
        {
            ["moved"] = true,
            ["scene"] = 60,
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
    internal static byte[] WritePlazaSeatRecord(PacketTables tables, string build, RoomSchema schema, PlazaMember member)
    {
        var slot = PlazaSlot(member);
        var values = RoomCodec.MenuSeatValues(schema, slot);
        values["level"] = RoomCodec.PlaySeatValues(schema, slot, equipment: false)["level"];
        return PacketCodec.Write(tables, build, "UserSlot", values);
    }

    /// <summary>
    /// Writes <c>gspRoomSetSlot</c> (reader <c>sub_80C1B0</c>): the seat index, the member's
    /// <c>UserSlot</c> and <c>EquippedItemList</c> — the plaza record is one of the room's.
    /// </summary>
    internal static byte[] WritePlazaSeatSlot(PacketTables tables, string build, RoomSchema schema, PlazaMember member) =>
        RoomCodec.WriteSeatEnvelope(tables, build, schema, member.Slot, PlazaSlot(member));

    /// <summary>Writes the native leave envelope while retaining the departed player's identity.</summary>
    internal static byte[] WritePlazaLeaveSeat(PacketTables tables, string build, RoomSchema schema, PlazaMember member) =>
        RoomCodec.WriteSeatEnvelope(tables, build, schema, member.Slot, PlazaSlot(member) with
        {
            Occupied = false, Departed = true, EquippedItemIds = [], Ready = false,
        }, leaving: true);

    private static RoomSlotValues PlazaSlot(PlazaMember member) =>
        new(true, false, member.UserNumber, member.Nickname, member.CharacterItemId,
            member.EquippedItemIds.Count > 0 ? member.EquippedItemIds : [member.CharacterItemId], false)
        {
            Level = Math.Max(1, member.Level), ColorIndex = member.ColorIndex,
        };
}
