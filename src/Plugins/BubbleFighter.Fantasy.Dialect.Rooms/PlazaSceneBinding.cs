using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Dialect.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts the native modern plaza stage to shared membership and movement ownership.</summary>
internal sealed class PlazaSceneBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<PlazaEnterRequested>, IInboundMessageBinding<PlazaMenuReturnRequested>,
      IInboundMessageBinding<PlazaPositionReported>, IMessageSender<PlazaEntered>, IMessageSender<PlazaMemberEntered>,
      IMessageSender<PlazaMemberLeft>, IMessageSender<PlazaPositionReceived>, IMessageSender<PlazaMenuReturnRequested>
{
    private static readonly string[] Structures =
        ["gspGotoRoom", "SoloPartySnapshot", "RoomSceneTransition", "RoomSeatLeave", "PlazaPositionUpload", "PlazaPositionBroadcast"];

    private static readonly string[] ServerNames =
        ["gspGotoRoom", "gspUpdateParty", "gspGotoLobby", "gspRoomLeaveSlot", "gspReceivePartyChrPos"];

    private IPluginContext? _context;
    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _s2c = RoomRoutes.Preflight(table, packets, build, Structures, ServerNames);
        // The install, the party snapshot and the seat records state their own contracts; the scene words this
        // binding writes or reads directly are checked here with the exact types it sizes them as: the empty
        // scene transition kr-1256 sub_9AC2C0 consumes, the occupied 511 record kr-1256 sub_9DB1E0 consumes, and
        // the seven-field upload (kr-1256 sub_913640) whose identity, place and flags the broadcast re-sends.
        PlazaRecords.RequireLayouts(packets, build);
        PartyRecords.ValidateLayouts(packets, build);
        PacketLayoutContract.RequireFields(packets, build, "RoomSceneTransition", "Plaza scene", ("notice", "bool"));
        PacketLayoutContract.RequireFields(packets, build, "RoomSeatLeave", "Plaza scene", ("slot", "u32"), ("user", "UserSlot"));
        PacketLayoutContract.RequireFields(packets, build, "PlazaPositionUpload", "Plaza scene",
            ("unknown_4", "u32"), ("x", "f32"), ("y", "f32"), ("z", "f32"),
            ("unknown_20", "u8"), ("unknown_21", "u8"), ("unknown_24", "f32"));
        PacketLayoutContract.RequireFields(packets, build, "PlazaPositionBroadcast", "Plaza scene",
            ("user_number", "u32"), ("sequence", "u32"), ("x", "f32"), ("y", "f32"), ("z", "f32"),
            ("unknown_24", "u8"), ("unknown_25", "u8"), ("unknown_28", "f32"));
        // Appearance delivery uses events so the scene codec does not wait for its view provider.
        _context = context;
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaEnterRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaMenuReturnRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaPositionReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaEntered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaMemberEntered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaMemberLeft>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaPositionReceived>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaMenuReturnRequested>>(this));
        Bind(context, "gcpReqGotoLobby", body => { RoomRoutes.RequireEmpty(body, "C2S gcpReqGotoLobby"); return new PlazaEnterRequested(); });
        Bind(context, "gcpReqGotoMenu", body => { RoomRoutes.RequireEmpty(body, "C2S gcpReqGotoMenu"); return new PlazaMenuReturnRequested(); });
        Bind(context, "gcpSendPartyChrPos", body =>
        {
            _ = PacketCodec.ReadScalars(packets, build, "PlazaPositionUpload", body);
            return new PlazaPositionReported(body.ToArray());
        });
    }

    private void Bind<T>(IPluginContext context, string name, Func<ReadOnlySpan<byte>, T> decode) where T : class, IMessage =>
        RoomRoutes.Bind(context, protocol, table, logger, name, decode);

    public async ValueTask SendAsync(ISession session, PlazaEntered message, CancellationToken ct)
    {
        var bySlot = message.Members.Append(message.You).ToDictionary(member => member.Slot);
        var order = message.MemberOrder ?? message.Members.Select(member => member.Slot).Append(message.You.Slot).ToArray();
        if (order.Count != bySlot.Count || order.Distinct().Count() != order.Count || order.Any(slot => !bySlot.ContainsKey(slot)))
            throw new InvalidOperationException("The plaza's profile order must name every occupied seat exactly once.");
        var seats = order.Select(slot => Seat(bySlot[slot])).ToArray();
        // kr-1256 sub_9AC490 kind 3 installs 36 plaza seats; sub_9064B0 then creates the own party avatar.
        await session.SendAsync(S2C("gspGotoRoom"), Install(seats, message.You.Slot, 3, message.PartyTransition), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspUpdateParty"), RoomRecords.Party(packets, build, Seat(message.You), session), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspGotoLobby"), PacketCodec.Write(packets, build, "RoomSceneTransition", new Dictionary<string, object?>()), ct).ConfigureAwait(false);
    }

    public async ValueTask SendAsync(ISession session, PlazaMemberEntered message, CancellationToken ct)
    {
        var member = message.Member;
        await PublishViewAsync(session, member.UserNumber, member.CharacterItemId, member.EquippedItemIds, true, ct).ConfigureAwait(false);
        // kr-1256 sub_9DB1E0 consumes occupied 511 records without replacing the plaza stage.
        await session.SendAsync(S2C("gspRoomLeaveSlot"), PacketCodec.Write(packets, build, "RoomSeatLeave", new Dictionary<string, object?>
        {
            ["slot"] = member.Slot, ["user"] = PlazaRecords.Seat(packets, build, Seat(member)),
        }), ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, PlazaMemberLeft message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRoomLeaveSlot"), PlazaRecords.Leave(packets, build, message.Member.Slot), ct);

    public ValueTask SendAsync(ISession session, PlazaPositionReceived message, CancellationToken ct)
    {
        // The enter replay carries a member with no reported position as an empty body (kr-1256 sub_91FB80
        // keeps such a member at its plaza spawn); there is no upload to echo, so nothing is sent.
        if (message.Body.IsEmpty)
            return ValueTask.CompletedTask;
        // kr-1256 sub_913640 supplies the sequence; sub_9D2FF0 and sub_91FB80 consume it unchanged after identity.
        var upload = PacketCodec.ReadScalars(packets, build, "PlazaPositionUpload", message.Body.Span);
        return session.SendAsync(S2C("gspReceivePartyChrPos"), PacketCodec.Write(packets, build, "PlazaPositionBroadcast", new Dictionary<string, object?>
        {
            ["user_number"] = message.UserNumber, ["sequence"] = upload["unknown_4"],
            ["x"] = upload["x"], ["y"] = upload["y"], ["z"] = upload["z"],
            ["unknown_24"] = upload["unknown_20"], ["unknown_25"] = upload["unknown_21"], ["unknown_28"] = upload["unknown_24"],
        }), ct);
    }

    public async ValueTask SendAsync(ISession session, PlazaMenuReturnRequested message, CancellationToken ct)
    {
        var member = message.You ?? throw new InvalidOperationException("Modern menu return requires the committed own appearance.");
        var self = Seat(member) with { Slot = 0 };
        await session.SendAsync(S2C("gspGotoRoom"), Install([self], 0, 1, message.PartyTransition), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspUpdateParty"), RoomRecords.Party(packets, build, self, session), ct).ConfigureAwait(false);
        await PublishViewAsync(session, member.UserNumber, member.CharacterItemId, member.EquippedItemIds, false, ct).ConfigureAwait(false);
    }

    private byte[] Install(IReadOnlyList<RoomMemberView> seats, int localSlot, int kind, bool partyTransition = false) =>
        PlazaRecords.Install(packets, build, seats, localSlot, kind, partyTransition);

    private static RoomMemberView Seat(PlazaMember member) =>
        new(member.Slot, member.UserNumber, member.Nickname, member.Level, 0, false, member.CharacterItemId, member.EquippedItemIds);

    private ushort S2C(string name) => _s2c[name];
    private async ValueTask PublishViewAsync(ISession session, int userNum, int characterId,
        IReadOnlyList<int> equippedIds, bool remote, CancellationToken ct)
    {
        var context = _context ?? throw new InvalidOperationException("Room binding is not mounted.");
        await context.Events.WaterfallAsync(new EquipmentViewRequested(protocol, session, userNum, characterId, equippedIds, remote),
            _ => ValueTask.FromResult(false), ct).ConfigureAwait(false);
    }

}
