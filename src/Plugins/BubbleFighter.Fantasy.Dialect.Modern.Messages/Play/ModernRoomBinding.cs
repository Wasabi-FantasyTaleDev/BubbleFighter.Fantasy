using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>
/// The opt-in kr-1256 waiting-room binding. It maps the audited room requests to shared messages and
/// writes the room surface through packet tables. The default profile keeps the verified raw path.
/// </summary>
internal sealed class ModernRoomBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger,
    bool roundsBound = false)
    : IInboundMessageBinding<RoomCreateRequested>, IInboundMessageBinding<RoomJoinRequested>,
      IInboundMessageBinding<RoomLeaveRequested>, IInboundMessageBinding<RoomReadyRequested>,
      IInboundMessageBinding<RoomTeamChangeRequested>, IInboundMessageBinding<RoomStartUnavailableRequested>,
      IInboundMessageBinding<RoomUiRefreshRequested>, IInboundMessageBinding<RoomChatSent>,
      IInboundMessageBinding<RoomSeatLockRequested>, IMessageSender<RoomEntered>, IMessageSender<RoomSeatUpdated>,
      IMessageSender<RoomSeatVacated>, IMessageSender<RoomMasterChanged>, IMessageSender<RoomJoinRefused>,
      IMessageSender<RoomChatReceived>,
      IInboundMessageBinding<RoomMasterChangeRequested>, IMessageSender<RoomMasterChangeReplied>,
      IMessageSender<RoomSettingsChanged>, IInboundMessageBinding<RoomRoundSettingsChangeRequested>,
      IMessageSender<RoomSeatLockChanged>, IMessageSender<RoomUiChanged>, IMessageSender<RoomProbeAcknowledged>,
      IInboundMessageBinding<RoomSettingsChangeRequested>
{
    private static readonly string[] Structures =
    [
        "RoomCreateRequest", "RoomJoinRequest", "RoomReadyRequest", "RoomTeamChangeRequest", "RoomEnterGameRequest",
        "RoomChatRequest", "RoomSeatLock", "RoomPingRequest", "RoomWaitingDialog", "gspGotoRoom", "SessionSetting",
        "GameSetting", "SoloPartySnapshot", "RoomSeatUpdate", "RoomSeatLeave", "ZombieTeamChange", "TutorialMasterSlot",
        "RoomJoinResult", "RoomChatReceived", "gspRepWaitUserList", "gspReqPingUser", "gspRepPingUser",
        "RoomMasterChangeRequest", "RoomMasterChangeReply",
    ];

    private static readonly string[] ServerNames =
    [
        "Unknown128", "gspGotoRoom", "gspRoomSetSession", "gspUpdateParty", "gspRoomSetSlot", "gspRoomLeaveSlot",
        "gspRoomChangeTeam", "gspRoomSetMasterSlotIdx", "gspRepJoinSession", "gspRoomReceiveChatModuleMsg",
        "gspRepWaitUserList", "gspReqPingUser", "gspRepPingUser",
        "gspRoomRepChangeMaster",
    ];

    // Back from a round's result: the relay state clear and the stage reset (kr-1256 sub_9E2FE0 case 0x26C ->
    // sub_9EA690) put the client back into the waiting room it still holds, as the packet relay path does.
    private static readonly string[] RoundReturnNames = ["gspUdpStop", "Unknown620"];

    private IEquippedViewPush? _equippedView;
    private ushort? _settingsReply;
    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _s2c = ModernRoutes.Preflight(table, packets, build, Structures,
            roundsBound ? [.. ServerNames, .. RoundReturnNames] : ServerNames);
        _settingsReply = table.Find(PacketDirection.ServerToClient, "gspRoomChangeGameSetting");
        if (_settingsReply is not null)
            context.Effect(context.Services.RegisterMany<IMessageSender<RoomSettingsChanged>>(this));
        if (table.Find(PacketDirection.ClientToServer, "gcpRoomChangeRoundSetting") is not null
            && RoomRoundSettingFields.Load(packets, build) is { } roundFields)
        {
            if (_settingsReply is null)
                throw new ConfigException("Round settings require an audited settings notification opcode.");
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomRoundSettingsChangeRequested>>(this));
            Bind(context, "gcpRoomChangeRoundSetting", body =>
            {
                var value = roundFields.Read(body);
                return new RoomRoundSettingsChangeRequested(value.Index, build, body.ToArray());
            });
        }
        // The master's map change: kr-1256 sub_96CF30 fills the request (the room's mode word at +104) and
        // kr-1256 sub_5E1490 sends it; the room shows it through gspRoomChangeGameSetting.
        if (table.Find(PacketDirection.ClientToServer, "gcpRoomChangeGameSetting") is not null
            && RoomSettingChangeMap.Load(packets, build) is { } changeMap)
        {
            if (_settingsReply is null)
                throw new ConfigException("Map changes require an audited settings notification opcode.");
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomSettingsChangeRequested>>(this));
            Bind(context, "gcpRoomChangeGameSetting", body =>
            {
                var change = changeMap.Read(body);
                return new RoomSettingsChangeRequested(change.Mode, changeMap.RoomKey(change),
                    new RoomBuildSettings(build, default, body.ToArray()));
            });
        }
        if (!context.Services.TryGet(out _equippedView))
            logger.LogInformation("Modern room entry on {Protocol} omits equipped views: no IEquippedViewPush service is mounted.", protocol);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomJoinRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomLeaveRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomReadyRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomTeamChangeRequested>>(this));
        if (!roundsBound)
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomStartUnavailableRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomUiRefreshRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomChatSent>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomSeatLockRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomEntered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomSeatUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomSeatVacated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomMasterChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomJoinRefused>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomChatReceived>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomSeatLockChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomUiChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomProbeAcknowledged>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomMasterChangeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomMasterChangeReplied>>(this));

        Bind(context, "gcpReqCreateSession", body => ModernRoomProjection.Create(packets, build, body));
        Bind(context, "gcpReqJoinSession", body => ModernRoomRequests.ReadJoin(packets, build, body));
        Bind(context, "gcpReqLeaveSession", body => { ModernRoutes.RequireEmpty(body, "C2S gcpReqLeaveSession"); return new RoomLeaveRequested(); });
        Bind(context, "gcpRoomReqReadyStatus", body => ModernRoomRequests.ReadReady(packets, build, body));
        Bind(context, "gcpRoomChangeTeam", body => ModernRoomRequests.ReadTeamChange(packets, build, body));
        Bind(context, "gcpRoomSendChatModuleMsg", body => ModernRoomRequests.ReadChat(packets, build, body));
        Bind(context, "gcpRoomReqOpenCloseSlot", body => ModernRoomRequests.ReadSeatLock(packets, build, body));
        Bind(context, "gcpRoomReqChangeMaster", body => new RoomMasterChangeRequested(
            (string)PacketCodec.ReadScalars(packets, build, "RoomMasterChangeRequest", body)["nickname"]));
        // With typed rounds the round binding owns the start request.
        if (!roundsBound)
            Bind(context, "gcpRoomReqEnterGame", body =>
            {
                _ = ModernRoomRequests.ReadStart(packets, build, body);
                return new RoomStartUnavailableRequested();
            });
        // kr-1256 sub_978C80 -> sub_9E1580 -> sub_708310 writes only the local seat word: a stage probe, not a join.
        Bind(context, "gcpReqPingUser", body =>
        {
            _ = PacketCodec.ReadScalars(packets, build, "RoomPingRequest", body);
            return new RoomUiRefreshRequested();
        });
    }

    private void Bind<T>(IPluginContext context, string name, Func<ReadOnlySpan<byte>, T> decode) where T : class, IMessage =>
        ModernRoutes.Bind(context, protocol, table, logger, name, decode);

    public async ValueTask SendAsync(ISession session, RoomEntered message, CancellationToken ct)
    {
        if (message.Reason == RoomEntryReason.RoundReturned && roundsBound)
        {
            foreach (var name in RoundReturnNames)
                await session.SendAsync(S2C(name), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            return;
        }
        var room = message.Room;
        var self = room.Seats.Single(seat => seat.Slot == message.YourSlot);
        var profiles = ModernRoomProjection.Members(room);
        var setting = ModernRoomProjection.Session(packets, build, room);
        if (message.Reason == RoomEntryReason.Joined)
            await Send(session, "Unknown128", "RoomWaitingDialog", new() { ["state"] = 0 }, ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspGotoRoom"), ModernRoomRecords.Install(packets, build, setting,
            profiles, room.MasterSlot, message.YourSlot, message.PartyTransition), ct).ConfigureAwait(false);
        await Send(session, "gspRoomSetSession", "SessionSetting", setting, ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspUpdateParty"), ModernRoomRecords.Party(packets, build, self, session), ct).ConfigureAwait(false);
        if (_equippedView is not null)
        {
            await _equippedView.SendSelectionAsync(session, self.UserNum, self.CharacterItemId, self.EquippedItemIds, ct).ConfigureAwait(false);
            foreach (var peer in profiles.Where(seat => seat.Slot != self.Slot))
                await _equippedView.SendRemoteAsync(session, peer.UserNum, peer.CharacterItemId, peer.EquippedItemIds, ct).ConfigureAwait(false);
        }
        // kr-1256 sub_9DA720 announces seats without the stage teardown of sub_9AC490's kind-2 install.
        if (message.Reason is RoomEntryReason.Created or RoomEntryReason.Refreshed)
            await session.SendAsync(S2C("gspRoomSetSlot"), ModernRoomRecords.SeatUpdate(packets, build, self, profiles), ct).ConfigureAwait(false);
        if (message.Reason == RoomEntryReason.Joined)
        {
            foreach (var seat in new[] { self }.Concat(room.Seats.Where(seat => seat.Slot != self.Slot)))
                await session.SendAsync(S2C("gspRoomSetSlot"), ModernRoomRecords.SeatUpdate(packets, build, seat, profiles), ct).ConfigureAwait(false);
            foreach (var slot in room.ClosedSlots)
                await session.SendAsync(S2C("gspRoomLeaveSlot"), ModernRoomRecords.SeatLeave(packets, build, slot, true), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask SendAsync(ISession session, RoomSeatUpdated message, CancellationToken ct)
    {
        if (message.Reason == RoomSeatUpdateReason.TeamChanged)
        {
            await Send(session, "gspRoomChangeTeam", "ZombieTeamChange", new() { ["seat"] = message.Seat.Slot, ["team"] = message.Seat.Team }, ct).ConfigureAwait(false);
            return;
        }
        var room = message.Room ?? throw new InvalidOperationException("Modern seat updates require the complete committed appearance snapshot.");
        await session.SendAsync(S2C("gspRoomSetSlot"), ModernRoomRecords.SeatUpdate(packets, build, message.Seat, ModernRoomProjection.Members(room)), ct).ConfigureAwait(false);
        if (message.Reason == RoomSeatUpdateReason.Joined && _equippedView is not null)
            await _equippedView.SendRemoteAsync(session, message.Seat.UserNum, message.Seat.CharacterItemId, message.Seat.EquippedItemIds, ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, RoomSeatVacated message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRoomLeaveSlot"), ModernRoomRecords.SeatLeave(packets, build, message.Seat.Slot), ct);

    public ValueTask SendAsync(ISession session, RoomMasterChanged message, CancellationToken ct) =>
        Send(session, "gspRoomSetMasterSlotIdx", "TutorialMasterSlot", new() { ["master_slot"] = message.MasterSlot }, ct);

    public ValueTask SendAsync(ISession session, RoomMasterChangeReplied message, CancellationToken ct) =>
        Send(session, "gspRoomRepChangeMaster", "RoomMasterChangeReply",
            new() { ["nickname"] = message.Nickname, ["result"] = message.Result }, ct);

    public ValueTask SendAsync(ISession session, RoomSettingsChanged message, CancellationToken ct)
    {
        var opcode = _settingsReply
            ?? throw new InvalidOperationException("Room settings notifications require an audited outbound opcode.");
        // kr-1256 sub_9DB8A0 reads one complete GameSetting through sub_113D030 and refreshes the room UI.
        var setting = ModernRoomProjection.Session(packets, build, message.Room);
        var game = ModernRoomProjection.Map(setting["game_setting"]);
        return session.SendAsync(opcode, PacketCodec.Write(packets, build, "GameSetting", game), ct);
    }

    public ValueTask SendAsync(ISession session, RoomJoinRefused message, CancellationToken ct) =>
        Send(session, "gspRepJoinSession", "RoomJoinResult", new() { ["result"] = message.Reason switch { RoomJoinRefusal.Started => 1, RoomJoinRefusal.Full => 3, RoomJoinRefusal.WrongPassword => 4, _ => 5 } }, ct);

    // C2S 502 is the chat-module request, so the line goes back as S2C 509 (kr-1256 sub_9DE360 draws it from
    // the kr-1256 sub_10C4A50 record). The raw path writes kind 0 at the channel word and marks a team line
    // only through the colour word at +84.
    public ValueTask SendAsync(ISession session, RoomChatReceived message, CancellationToken ct) =>
        Send(session, "gspRoomReceiveChatModuleMsg", "RoomChatReceived", new()
        {
            ["slot"] = checked((ushort)message.Slot), ["nickname"] = message.Nickname, ["channel"] = 0,
            ["text"] = message.Text, ["unknown_96"] = message.Channel == TeamChatChannel ? TeamChatColour : -1,
        }, ct);

    private const int TeamChatChannel = 1;
    private const int TeamChatColour = unchecked((int)0xFFFFA1A1);

    public ValueTask SendAsync(ISession session, RoomSeatLockChanged message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRoomLeaveSlot"), ModernRoomRecords.SeatLeave(packets, build, message.Slot, message.Closed), ct);

    public ValueTask SendAsync(ISession session, RoomUiChanged message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRepWaitUserList"), WaitList(message.Room), ct);

    public async ValueTask SendAsync(ISession session, RoomProbeAcknowledged message, CancellationToken ct)
    {
        await Send(session, "gspReqPingUser", "gspReqPingUser", new() { ["token"] = message.Challenge }, ct).ConfigureAwait(false);
        await Send(session, "gspRepPingUser", "gspRepPingUser", new() { ["slot"] = message.Slot }, ct).ConfigureAwait(false);
    }

    private ushort S2C(string name) => _s2c[name];
    private ValueTask Send(ISession session, string name, string structure, Dictionary<string, object?> values, CancellationToken ct) =>
        session.SendAsync(S2C(name), PacketCodec.Write(packets, build, structure, values), ct);
    private byte[] WaitList(RoomView room) => PacketCodec.Write(packets, build, "gspRepWaitUserList", new Dictionary<string, object?> { ["users"] = room.Seats.Select(seat => new Dictionary<string, object?> { ["nickname"] = seat.Nickname, ["level"] = seat.Level }).ToArray() });
}
