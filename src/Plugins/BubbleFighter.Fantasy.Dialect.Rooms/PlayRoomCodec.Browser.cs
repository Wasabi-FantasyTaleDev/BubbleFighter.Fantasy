using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class PlayRoomCodec
{
    /// <summary>Mounts the existing plaza and waiting-room codecs without activating unaudited round adapters.</summary>
    internal void MountBrowser(IPluginContext context)
    {
        MountWaitingControls(context);
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var router = services.Get<IPacketRouter>();
        var table = tables.ForProtocol(protocol);
        foreach (var structure in new[] { "RoomListReply", "RoomListRequest", "RoomSeatUpdate", "RoomSeatLeave", "RoomJoinResult", "TutorialMasterSlot", "RoomMoveInto" })
            _ = packetTables.Resolve(lineage, structure);
        foreach (var name in new[] { "gspRepListSession", "gspGotoRoom", "gspRoomSetSlot", "gspRoomLeaveSlot", "gspRoomSetMasterSlotIdx", "gspRoomSetSession", "gspRepJoinSession", "gspRoomChangeGameSetting", "gspRoomReceiveChatModuleMsg", "gspRoomOpenCloseSlot", "gspRepKickFromSession" })
            _ = table.S2C(name);
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomListRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoomLeaveRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomListSnapshot>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomEntered>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSeatUpdated>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomSeatVacated>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomMasterChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoomLeft>>(this));

        void Bind<T>(string name, Func<ISession, ReadOnlyMemory<byte>, T> decode) where T : IMessage
        {
            context.Effect(router.Register(protocol, table.C2S(name), async (session, packet, ct) =>
            {
                var request = decode(session, packet.Payload);
                if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"Waiting-room request {name} has no typed handler on '{protocol}'.");
            }));
        }

        Bind("gcpReqListSession", (session, body) =>
        {
            var (page, pageSize) = PlazaRoomWire.ReadListSession(packetTables, lineage, body.Span);
            session.Items[PageSizeItem] = (int)pageSize;
            return new RoomListRequested(page);
        });
        Bind("gcpReqCreateSession", (_, body) =>
        {
            var request = PlazaRoomWire.ReadCreateRoom(body.Span, schema);
            return new RoomCreateRequested(request.Title, request.Password, request.Mode, request.MapKey,
                new RoomBuildSettings(lineage, request.GameSetting));
        });
        Bind("gcpReqLeaveSession", (_, body) =>
        {
            if (!body.IsEmpty)
                throw new PacketFormatException($"gcpReqLeaveSession must be empty; got {body.Length} byte(s).", 0);
            return new RoomLeaveRequested();
        });
        MountPlaza(context);
    }
}
