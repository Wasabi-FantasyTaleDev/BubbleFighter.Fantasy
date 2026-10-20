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

        On("gcpReqJoinSession", (session, body) =>
        {
            var request = PlazaRoomWire.ReadJoinSession(packetTables, lineage, body);
            return new RoomJoinRequested(request.RoomId, request.Password);
        });
        On("gcpRoomReqReadyStatus", (_, body) =>
        {
            // The first flag is the ready mark; the other two are not used.
            var (ready, _, _) = PlazaRoomWire.ReadReadyStatus(packetTables, lineage, body);
            return new RoomReadyRequested(ready);
        });
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
        On("gcpRoomChangeTeam", (_, body) =>
        {
            // kr-812 sub_9BEF70 and cn-406 sub_8AF7F0 send no fields; the flow selects the team.
            if (body.Length != 0)
                throw new PacketFormatException($"gcpRoomChangeTeam must be empty; got {body.Length} byte(s).", 0);
            return new RoomTeamChangeRequested();
        });
        // kr-812 sub_844570 reads the channel word and UTF-16 text.
        On("gcpRoomSendChatModuleMsg", (_, body) =>
        {
            var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoomChatRequest", body);
            return new RoomChatSent((int)fields["channel"], (string)fields["text"]);
        }, optional: true);
        On("gcpRoomReqOpenCloseSlot", (_, body) =>
        {
            var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoomSeatLock", body);
            return new RoomSeatLockRequested((int)fields["slot"], (bool)fields["closed"]);
        }, optional: true);
        On("gcpReqKickFromSession", (_, body) =>
            new RoomKickRequested((string)PacketCodec.ReadScalars(packetTables, lineage, "RoomKickRequest", body)["nickname"]), optional: true);
        if (table.Find(PacketDirection.ClientToServer, "gcpRoomReqChangeMaster") is not null)
        {
            _ = table.S2C("gspRoomRepChangeMaster");
            _ = packetTables.Resolve(lineage, "RoomMasterChangeRequest");
            _ = packetTables.Resolve(lineage, "RoomMasterChangeReply");
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoomMasterChangeRequested>>(this));
            context.Effect(services.RegisterMany<IMessageSender<RoomMasterChangeReplied>>(this));
            On("gcpRoomReqChangeMaster", (_, body) => new RoomMasterChangeRequested(
                (string)PacketCodec.ReadScalars(packetTables, lineage, "RoomMasterChangeRequest", body)["nickname"]));
        }
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
