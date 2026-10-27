using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Connects native chat to shared room and plaza audiences independently of round support.</summary>
internal sealed class RoomChatBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoomChatSent>, IMessageSender<RoomChatReceived>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _ = RoomRoutes.Preflight(table, packets, build, ["RoomChatRequest", "RoomChatReceived"], ["gspRoomReceiveChatModuleMsg"]);
        PacketLayoutContract.RequireFields(packets, build, "RoomChatReceived", "Room chat",
            ("slot", "u16"), ("nickname", "wstr"), ("channel", "u32"), ("text", "wstr"), ("unknown_96", "u32"));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomChatSent>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomChatReceived>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, "gcpRoomSendChatModuleMsg",
            body => RoomRequests.ReadChat(packets, build, body));
    }

    public ValueTask SendAsync(ISession session, RoomChatReceived message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspRoomReceiveChatModuleMsg"), Write(packets, build, message), ct);

    internal static byte[] Write(PacketTables packets, string build, RoomChatReceived message) =>
        // cn-774 sub_8F1B80 / tw-454 sub_A604B0 read the same chat fields, with the table's native tail.
        PacketCodec.Write(packets, build, "RoomChatReceived", new Dictionary<string, object?>
        {
            ["slot"] = checked((ushort)message.Slot), ["nickname"] = message.Nickname, ["channel"] = 0,
            ["text"] = message.Text, ["unknown_96"] = message.Channel == 1 ? unchecked((int)0xFFFFA1A1) : -1,
        });
}
