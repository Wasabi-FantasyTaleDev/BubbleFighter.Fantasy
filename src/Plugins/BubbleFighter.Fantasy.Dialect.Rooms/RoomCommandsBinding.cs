using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Dialect.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The modern waiting-room binding. It maps each build's audited room requests to shared messages and
/// writes the room surface through packet tables; with <c>roundsBound</c> it also resolves the round-return
/// names a result hands the waiting room.
/// </summary>
internal sealed class RoomCommandsBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger,
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
        "gspGotoRoom", "gspRoomSetSession", "gspUpdateParty", "gspRoomSetSlot", "gspRoomLeaveSlot",
        "gspRoomChangeTeam", "gspRoomSetMasterSlotIdx", "gspRepJoinSession", "gspRoomReceiveChatModuleMsg",
        "gspRepWaitUserList", "gspReqPingUser", "gspRepPingUser",
        "gspRoomRepChangeMaster",
    ];

    // Back from a round's result: the relay state clear and the stage reset (kr-1256 sub_9E2FE0 case 0x26C ->
    // sub_9EA690) put the client back into the waiting room it still holds, as the packet relay path does.
    private string[] _roundReturnNames = [];

    private IPluginContext? _context;
    private IGameText? _texts;
    private ushort? _settingsReply;
    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _texts = context.Services.TryGet<IGameText>(out var texts) ? texts : null;
        if (roundsBound)
        {
            _roundReturnNames = ["gspUdpStop", table.Find(PacketDirection.ServerToClient, "gspRoundStageReset_") is not null
                ? "gspRoundStageReset_" : "Unknown620"];
            PacketLayoutContract.RequireEmpty(packets, build, "gspUdpStop", "Round return");
            PacketLayoutContract.RequireEmpty(packets, build, "RoundStageReset", "Round return");
        }
        _s2c = RoomRoutes.Preflight(table, packets, build, Structures,
            roundsBound ? [.. ServerNames, .. _roundReturnNames] : ServerNames);
        // The waiting-room requests and the session projection state their own contracts; the structures this
        // binding writes directly are checked here, field by field with the exact types their writers size:
        // the waiting dialog's state word (kr-1256 sub_858250 opens the wall on 0), the team label's seat and
        // team byte (kr-1256 sub_9DAE50), the master-slot and master-change replies, the join result word the
        // client switches on (kr-1256 sub_857990), the chat record kr-1256 sub_10C4A50 parses, both ping words,
        // the wait-user list (kr-1256 sub_9ACD00) and the game settings block kr-1256 sub_9DB8A0 refreshes.
        RoomRequests.ValidateLayouts(packets, build);
        RoomProjection.ValidateLayouts(packets, build);
        PacketLayoutContract.RequireFields(packets, build, "RoomMasterChangeRequest", "Room commands", ("nickname", "wstr"));
        PacketLayoutContract.RequireFields(packets, build, "RoomPingRequest", "Room commands", ("unknown_n0", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoomWaitingDialog", "Room commands", ("state", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "ZombieTeamChange", "Room commands", ("seat", "u32"), ("team", "u8"));
        PacketLayoutContract.RequireFields(packets, build, "TutorialMasterSlot", "Room commands", ("master_slot", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoomMasterChangeReply", "Room commands",
            ("nickname", "wstr"), ("result", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoomJoinResult", "Room commands", ("result", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoomChatReceived", "Room commands",
            ("slot", "u16"), ("nickname", "wstr"), ("channel", "u32"), ("text", "wstr"), ("unknown_96", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "gspReqPingUser", "Room commands", ("token", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "gspRepPingUser", "Room commands", ("slot", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "gspRepWaitUserList", "Room commands",
            ("users", "list<RoomWaitUser>"));
        PacketLayoutContract.RequireFields(packets, build, "RoomWaitUser", "Room commands",
            ("nickname", "wstr"), ("level", "u32"));
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
        // Appearance delivery uses events so the scene codec does not wait for its view provider.
        _context = context;
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

        Bind(context, "gcpReqCreateSession", body => RoomProjection.Create(packets, build, body));
        Bind(context, "gcpReqJoinSession", body => RoomRequests.ReadJoin(packets, build, body));
        Bind(context, "gcpReqLeaveSession", body => { RoomRoutes.RequireEmpty(body, "C2S gcpReqLeaveSession"); return new RoomLeaveRequested(); });
        Bind(context, "gcpRoomReqReadyStatus", body => RoomRequests.ReadReady(packets, build, body));
        Bind(context, "gcpRoomChangeTeam", body => RoomRequests.ReadTeamChange(packets, build, body));
        Bind(context, "gcpRoomSendChatModuleMsg", body => RoomRequests.ReadChat(packets, build, body));
        Bind(context, "gcpRoomReqOpenCloseSlot", body => RoomRequests.ReadSeatLock(packets, build, body));
        Bind(context, "gcpRoomReqChangeMaster", body => new RoomMasterChangeRequested(
            (string)PacketCodec.ReadScalars(packets, build, "RoomMasterChangeRequest", body)["nickname"]));
        // With typed rounds the round binding owns the start request.
        if (!roundsBound)
            Bind(context, "gcpRoomReqEnterGame", body =>
            {
                _ = RoomRequests.ReadStart(packets, build, body);
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
        RoomRoutes.Bind(context, protocol, table, logger, name, decode);

    public async ValueTask SendAsync(ISession session, RoomEntered message, CancellationToken ct)
    {
        if (message.Reason == RoomEntryReason.RoundReturned && roundsBound)
        {
            foreach (var name in _roundReturnNames)
                await session.SendAsync(S2C(name), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            return;
        }
        var room = message.Room;
        var self = room.Seats.Single(seat => seat.Slot == message.YourSlot);
        var profiles = RoomProjection.Members(room);
        var setting = RoomProjection.Session(packets, build, room);
        if (message.Reason == RoomEntryReason.Joined && (table.Find(PacketDirection.ServerToClient, "gspRoomWaitingDialog_")
            ?? table.Find(PacketDirection.ServerToClient, "Unknown128")) is { } waitingDialog)
            await session.SendAsync(waitingDialog, PacketCodec.Write(packets, build, "RoomWaitingDialog", new Dictionary<string, object?> { ["state"] = 0 }), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspGotoRoom"), RoomRecords.Install(packets, build, setting,
            profiles, room.MasterSlot, message.YourSlot, message.PartyTransition, session, _texts), ct).ConfigureAwait(false);
        await Send(session, "gspRoomSetSession", "SessionSetting", setting, ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspUpdateParty"), RoomRecords.Party(packets, build, self, session), ct).ConfigureAwait(false);
        await PublishViewAsync(session, self.UserNum, self.CharacterItemId, self.EquippedItemIds, false, ct).ConfigureAwait(false);
        foreach (var peer in profiles.Where(seat => seat.Slot != self.Slot))
            await PublishViewAsync(session, peer.UserNum, peer.CharacterItemId, peer.EquippedItemIds, true, ct).ConfigureAwait(false);
        // kr-1256 sub_9DA720 announces seats without the stage teardown of sub_9AC490's kind-2 install.
        if (message.Reason is RoomEntryReason.Created or RoomEntryReason.Refreshed)
            await session.SendAsync(S2C("gspRoomSetSlot"), RoomRecords.SeatUpdate(packets, build, self, profiles, session, _texts), ct).ConfigureAwait(false);
        if (message.Reason == RoomEntryReason.Joined)
        {
            foreach (var seat in new[] { self }.Concat(room.Seats.Where(seat => seat.Slot != self.Slot)))
                await session.SendAsync(S2C("gspRoomSetSlot"), RoomRecords.SeatUpdate(packets, build, seat, profiles, session, _texts), ct).ConfigureAwait(false);
            foreach (var slot in room.ClosedSlots)
                await session.SendAsync(S2C("gspRoomLeaveSlot"), RoomRecords.SeatLeave(packets, build, slot, true), ct).ConfigureAwait(false);
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
        await session.SendAsync(S2C("gspRoomSetSlot"), RoomRecords.SeatUpdate(packets, build, message.Seat, RoomProjection.Members(room), session, _texts), ct).ConfigureAwait(false);
        if (message.Reason == RoomSeatUpdateReason.Joined)
            await PublishViewAsync(session, message.Seat.UserNum, message.Seat.CharacterItemId, message.Seat.EquippedItemIds, true, ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, RoomSeatVacated message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRoomLeaveSlot"), RoomRecords.SeatLeave(packets, build, message.Seat.Slot), ct);

    public async ValueTask SendAsync(ISession session, RoomMasterChanged message, CancellationToken ct)
    {
        await Send(session, "gspRoomSetMasterSlotIdx", "TutorialMasterSlot", new() { ["master_slot"] = message.MasterSlot }, ct).ConfigureAwait(false);
        if (message.PreviousAiControllerUserNum is { } previous)
        {
            var opcode = table.Find(PacketDirection.ServerToClient, "gspRoomNpcBotControllerChanged_")
                ?? throw new ConfigException("AI ownership transfer requires the native controller-change notification.");
            await session.SendAsync(opcode, PacketCodec.Write(packets, build, "RoomAiControllerChange", new Dictionary<string, object?>
            {
                ["previous_user_number"] = previous, ["controller_slot"] = message.MasterSlot,
            }), ct).ConfigureAwait(false);
        }
    }

    public ValueTask SendAsync(ISession session, RoomMasterChangeReplied message, CancellationToken ct) =>
        Send(session, "gspRoomRepChangeMaster", "RoomMasterChangeReply",
            new() { ["nickname"] = message.Nickname, ["result"] = message.Result }, ct);

    public ValueTask SendAsync(ISession session, RoomSettingsChanged message, CancellationToken ct)
    {
        var opcode = _settingsReply
            ?? throw new InvalidOperationException("Room settings notifications require an audited outbound opcode.");
        // kr-1256 sub_9DB8A0 reads one complete GameSetting through sub_113D030 and refreshes the room UI.
        var setting = RoomProjection.Session(packets, build, message.Room);
        var game = RoomProjection.Map(setting["game_setting"]);
        return session.SendAsync(opcode, PacketCodec.Write(packets, build, "GameSetting", game), ct);
    }

    public ValueTask SendAsync(ISession session, RoomJoinRefused message, CancellationToken ct) =>
        Send(session, "gspRepJoinSession", "RoomJoinResult", new() { ["result"] = message.Reason switch { RoomJoinRefusal.Started => 1, RoomJoinRefusal.Full => 3, RoomJoinRefusal.WrongPassword => 4, _ => 5 } }, ct);

    // C2S 502 is the chat-module request, so the line goes back as S2C 509 (kr-1256 sub_9DE360 draws it from
    // the kr-1256 sub_10C4A50 record). The raw path writes kind 0 at the channel word and marks a team line
    // only through the colour word at +84.
    public ValueTask SendAsync(ISession session, RoomChatReceived message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRoomReceiveChatModuleMsg"), RoomChatBinding.Write(packets, build, message), ct);

    public ValueTask SendAsync(ISession session, RoomSeatLockChanged message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRoomLeaveSlot"), RoomRecords.SeatLeave(packets, build, message.Slot, message.Closed), ct);

    public ValueTask SendAsync(ISession session, RoomUiChanged message, CancellationToken ct) =>
        session.SendAsync(S2C("gspRepWaitUserList"), WaitList(message.Room, session), ct);

    public async ValueTask SendAsync(ISession session, RoomProbeAcknowledged message, CancellationToken ct)
    {
        await Send(session, "gspReqPingUser", "gspReqPingUser", new() { ["token"] = message.Challenge }, ct).ConfigureAwait(false);
        await Send(session, "gspRepPingUser", "gspRepPingUser", new() { ["slot"] = message.Slot }, ct).ConfigureAwait(false);
    }

    private ushort S2C(string name) => _s2c[name];
    private ValueTask Send(ISession session, string name, string structure, Dictionary<string, object?> values, CancellationToken ct) =>
        session.SendAsync(S2C(name), PacketCodec.Write(packets, build, structure, values), ct);
    private byte[] WaitList(RoomView room, ISession recipient) => PacketCodec.Write(packets, build, "gspRepWaitUserList", new Dictionary<string, object?>
    {
        ["users"] = room.Seats.Select(seat => new Dictionary<string, object?>
        {
            ["nickname"] = RoomNames.Resolve(seat.Nickname, seat.Ai?.NicknameKey ?? "", recipient, _texts),
            ["level"] = seat.Level,
        }).ToArray(),
    });
    private async ValueTask PublishViewAsync(ISession session, int userNum, int characterId,
        IReadOnlyList<int> equippedIds, bool remote, CancellationToken ct)
    {
        var context = _context ?? throw new InvalidOperationException("Room binding is not mounted.");
        await context.Events.WaterfallAsync(new EquipmentViewRequested(protocol, session, userNum, characterId, equippedIds, remote),
            _ => ValueTask.FromResult(false), ct).ConfigureAwait(false);
    }

}
