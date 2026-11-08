using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class PlayRoomCodec
{
    // Waiting-room controls share the same typed flow even when a build's rounds are not mounted.
    private void MountWaitingControls(IPluginContext context)
    {
        var services = context.Services;
        var table = tables.ForProtocol(protocol);
        foreach (var structure in new[] { "RoomJoinRequest", "RoomJoinResult", "RoomReadyRequest", "RoomGameSettingChangeRequest", "RoomChatRequest", "RoomChatReceived", "RoomSeatLock", "RoomKickRequest", "RoomMemberKicked" })
            _ = packetTables.Resolve(lineage, structure);
        _ = SettingChangeFields.Require(packetTables, lineage);
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomJoinRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomReadyRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomTeamChangeRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomSettingsChangeRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSettingsChanged>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomChatSent>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomChatReceived>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomSeatLockRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSeatLockChanged>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomKickRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomMemberKicked>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomJoinRefused>>(this));

        void On<T>(string name, Func<ISession, ReadOnlySpan<byte>, T> decode, bool optional = false) where T : IMessage
        {
            if (table.Find(PacketDirection.ClientToServer, name) is { } opcode)
                BindRequest(context, opcode, name, decode);
            else if (optional)
                logger.LogInformation("dialect.rooms: {Protocol} has no {Name}; not bound.", protocol, name);
            else
                logger.LogWarning("dialect.rooms: {Protocol} does not name {Name}; its rooms cannot use it until the table does.", protocol, name);
        }

        // The waiting-room requests share the modern family's decoders (RoomRequests): the layouts are each build's
        // own tables, and every legacy writer sends canonical, mutually exclusive ready/shop/inventory flags.
        On("gcpReqJoinSession", (_, body) => RoomRequests.ReadJoin(packetTables, lineage, body));
        On("gcpRoomReqReadyStatus", (_, body) => RoomRequests.ReadReady(packetTables, lineage, body));
        On("gcpRoomChangeGameSetting", (_, body) => ReadSettingsChange(packetTables, lineage, body));
        if (table.Find(PacketDirection.ClientToServer, "gcpRoomChangeRoundSetting") is not null)
        {
            _roundSettingFields = RoomRoundSettingFields.Load(packetTables, lineage);
            if (_roundSettingFields is { } fields)
            {
                _ = table.S2C("gspRoomChangeGameSetting");
                context.Effect(services.RegisterMany<IInboundMessageBinding<RoomRoundSettingsChangeRequested>>(this));
                On("gcpRoomChangeRoundSetting", (_, body) =>
                {
                    var value = fields.Read(body);
                    return new RoomRoundSettingsChangeRequested(value.Index, lineage, body.ToArray());
                });
            }
        }
        On("gcpRoomChangeTeam", (_, body) => RoomRequests.ReadTeamChange(packetTables, lineage, body));
        // kr-812 sub_844570 reads the channel word and UTF-16 text; the one shared chat reader decodes it.
        On("gcpRoomSendChatModuleMsg", (_, body) => RoomRequests.ReadChat(packetTables, lineage, body), optional: true);
        // The waiting-room chat box sends C2S gcpRoomSendChatMsg and draws nothing until S2C gspRoomReceiveChatMsg
        // answers (kr-812 sub_8145C0, cn-717 sub_899D30). A build mounts the pair only when its table names both.
        if (table.Find(PacketDirection.ClientToServer, "gcpRoomSendChatMsg") is not null)
        {
            _ = table.S2C("gspRoomReceiveChatMsg");
            PacketLayoutContract.RequireFields(packetTables, lineage, "RoomSessionChatRequest", "Room chat",
                ("team_only", "bool"), ("text", "wstr"));
            PacketLayoutContract.RequireFields(packetTables, lineage, "RoomSessionChatReceived", "Room chat",
                ("slot", "u16"), ("team_only", "bool"), ("text", "wstr"));
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoomSessionChatSent>>(this));
            context.Effect(services.RegisterMany<IMessageSender<RoomSessionChatReceived>>(this));
            On("gcpRoomSendChatMsg", (_, body) => ReadSessionChat(packetTables, lineage, body));
        }
        On("gcpRoomReqOpenCloseSlot", (_, body) => RoomRequests.ReadSeatLock(packetTables, lineage, body), optional: true);
        PacketLayoutContract.RequireFields(packetTables, lineage, "RoomKickRequest", "Room kicking", ("nickname", "wstr"));
        On("gcpReqKickFromSession", (_, body) =>
            new RoomKickRequested((string)PacketCodec.ReadScalars(packetTables, lineage, "RoomKickRequest", body)["nickname"]), optional: true);
        if (table.Find(PacketDirection.ClientToServer, "gcpRoomReqChangeMaster") is not null)
        {
            _ = table.S2C("gspRoomRepChangeMaster");
            PacketLayoutContract.RequireFields(packetTables, lineage, "RoomMasterChangeRequest", "Room master change",
                ("nickname", "wstr"));
            _ = packetTables.Resolve(lineage, "RoomMasterChangeReply");
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoomMasterChangeRequested>>(this));
            context.Effect(services.RegisterMany<IMessageSender<RoomMasterChangeReplied>>(this));
            On("gcpRoomReqChangeMaster", (_, body) => new RoomMasterChangeRequested(
                (string)PacketCodec.ReadScalars(packetTables, lineage, "RoomMasterChangeRequest", body)["nickname"]));
        }
    }

    /// <summary>Reads C2S gcpRoomSendChatMsg: the team-only flag and the line (kr-812 sub_8456D0, cn-717 sub_8CFCB0).</summary>
    internal static RoomSessionChatSent ReadSessionChat(PacketTables packetTables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packetTables, build, "RoomSessionChatRequest", body);
        return new RoomSessionChatSent((bool)fields["team_only"], (string)fields["text"]);
    }

    private void BindRequest<T>(IPluginContext context, ushort opcode, string name, Func<ISession, ReadOnlySpan<byte>, T> decode) where T : IMessage
    {
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        var first = name switch
        {
            "gcpGameReportMapLoadingComplete" => LoadReportFirst,
            "gcpReqEndStage" => EndStageFirst,
            "gcpRoomReqEnterGame" => StartRequestFirst,
            "gcpReqLeaveSession" or "gcpRoomReqLeaveGame" => LeaveRequestFirst,
            _ => null,
        };
        context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
        {
            if (first is not null && await first(session, packet, ct).ConfigureAwait(false))
                return;
            T message;
            try
            {
                message = decode(session, packet.Payload.Span);
            }
            catch (PacketFormatException ex)
            {
                logger.LogError("Session {Id} {Name} does not match the expected layout ({Reason}); closing.",
                    session.Id, name, ex.Message);
                await session.Connection.CloseAsync($"malformed {name}").ConfigureAwait(false);
                return;
            }
            if (message is RoomCreateRequested or RoomJoinRequested && MultiplayerRoomEntering is not null)
                await MultiplayerRoomEntering(session, ct).ConfigureAwait(false);
            if (message is RoomReadyRequested ready && ReadyRequestFirst is not null
                && await ReadyRequestFirst(session, ready, ct).ConfigureAwait(false))
                return;
            if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                logger.LogWarning("Session {Id} {Name} has no handler on {Protocol}.", session.Id, name, protocol);
        }));
    }
}
