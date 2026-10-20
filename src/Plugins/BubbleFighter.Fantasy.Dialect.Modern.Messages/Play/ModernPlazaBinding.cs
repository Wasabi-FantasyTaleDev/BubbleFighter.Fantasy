using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>Adapts the native modern plaza stage to shared membership and movement ownership.</summary>
internal sealed class ModernPlazaBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<PlazaEnterRequested>, IInboundMessageBinding<PlazaMenuReturnRequested>,
      IInboundMessageBinding<PlazaPositionReported>, IMessageSender<PlazaEntered>, IMessageSender<PlazaMemberEntered>,
      IMessageSender<PlazaMemberLeft>, IMessageSender<PlazaPositionReceived>, IMessageSender<PlazaMenuReturnRequested>
{
    private static readonly string[] Structures =
        ["gspGotoRoom", "SoloPartySnapshot", "RoomSceneTransition", "RoomSeatLeave", "PlazaPositionUpload", "PlazaPositionBroadcast"];

    private static readonly string[] ServerNames =
        ["gspGotoRoom", "gspUpdateParty", "gspGotoLobby", "gspRoomLeaveSlot", "gspReceivePartyChrPos"];

    private IEquippedViewPush? _equippedView;
    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _s2c = ModernRoutes.Preflight(table, packets, build, Structures, ServerNames);
        context.Services.TryGet(out _equippedView);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaEnterRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaMenuReturnRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaPositionReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaEntered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaMemberEntered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaMemberLeft>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaPositionReceived>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaMenuReturnRequested>>(this));
        Bind(context, "gcpReqGotoLobby", body => { ModernRoutes.RequireEmpty(body, "C2S gcpReqGotoLobby"); return new PlazaEnterRequested(); });
        Bind(context, "gcpReqGotoMenu", body => { ModernRoutes.RequireEmpty(body, "C2S gcpReqGotoMenu"); return new PlazaMenuReturnRequested(); });
        Bind(context, "gcpSendPartyChrPos", body =>
        {
            _ = PacketCodec.ReadScalars(packets, build, "PlazaPositionUpload", body);
            return new PlazaPositionReported(body.ToArray());
        });
    }

    private void Bind<T>(IPluginContext context, string name, Func<ReadOnlySpan<byte>, T> decode) where T : class, IMessage =>
        ModernRoutes.Bind(context, protocol, table, logger, name, decode);

    public async ValueTask SendAsync(ISession session, PlazaEntered message, CancellationToken ct)
    {
        var bySlot = message.Members.Append(message.You).ToDictionary(member => member.Slot);
        var order = message.MemberOrder ?? message.Members.Select(member => member.Slot).Append(message.You.Slot).ToArray();
        if (order.Count != bySlot.Count || order.Distinct().Count() != order.Count || order.Any(slot => !bySlot.ContainsKey(slot)))
            throw new InvalidOperationException("The plaza's profile order must name every occupied seat exactly once.");
        var seats = order.Select(slot => Seat(bySlot[slot])).ToArray();
        // kr-1256 sub_9AC490 kind 3 installs 36 plaza seats; sub_9064B0 then creates the own party avatar.
        await session.SendAsync(S2C("gspGotoRoom"), Install(seats, message.You.Slot, 3, message.PartyTransition), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspUpdateParty"), ModernRoomRecords.Party(packets, build, Seat(message.You), session), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspGotoLobby"), PacketCodec.Write(packets, build, "RoomSceneTransition", new Dictionary<string, object?>()), ct).ConfigureAwait(false);
    }

    public async ValueTask SendAsync(ISession session, PlazaMemberEntered message, CancellationToken ct)
    {
        var member = message.Member;
        if (_equippedView is not null)
            await _equippedView.SendRemoteAsync(session, member.UserNumber, member.CharacterItemId, member.EquippedItemIds, ct).ConfigureAwait(false);
        // kr-1256 sub_9DB1E0 consumes occupied 511 records without replacing the plaza stage.
        await session.SendAsync(S2C("gspRoomLeaveSlot"), PacketCodec.Write(packets, build, "RoomSeatLeave", new Dictionary<string, object?>
        {
            ["slot"] = member.Slot, ["user"] = ModernRoomRecords.Seat(Seat(member)),
        }), ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, PlazaMemberLeft message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRoomLeaveSlot"), ModernRoomRecords.SeatLeave(packets, build, message.Member.Slot), ct);

    public ValueTask SendAsync(ISession session, PlazaPositionReceived message, CancellationToken ct)
    {
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
        await session.SendAsync(S2C("gspUpdateParty"), ModernRoomRecords.Party(packets, build, self, session), ct).ConfigureAwait(false);
        if (_equippedView is not null)
            await _equippedView.SendSelectionAsync(session, member.UserNumber, member.CharacterItemId, member.EquippedItemIds, ct).ConfigureAwait(false);
    }

    private byte[] Install(IReadOnlyList<RoomMemberView> seats, int localSlot, int kind, bool partyTransition = false) =>
        ModernRoomRecords.Install(packets, build, new Dictionary<string, object?> { ["kind"] = kind }, seats, 0, localSlot, partyTransition);

    private static RoomMemberView Seat(PlazaMember member) =>
        new(member.Slot, member.UserNumber, member.Nickname, member.Level, 0, false, member.CharacterItemId, member.EquippedItemIds);

    private ushort S2C(string name) => _s2c[name];
}
