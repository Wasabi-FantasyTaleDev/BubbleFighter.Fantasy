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

    /// <summary>
    /// Writes one chat line for any build. builds.yaml room_chat_colour selects how a channel-1 line is coloured:
    /// <c>channel</c> sends the channel and lets the client colour it (kr-812 sub_8023E0), <c>suffix_word</c> sends
    /// channel 0 with the colour word the client uses for type 1 (kr-1256 sub_9DE360).
    /// </summary>
    internal static byte[] Write(PacketTables packets, string build, RoomChatReceived message)
    {
        var values = new Dictionary<string, object?>
        {
            ["slot"] = checked((ushort)message.Slot), ["nickname"] = message.Nickname, ["text"] = message.Text,
        };
        switch (packets.ValuesOf(build).GetValueOrDefault("room_chat_colour") as string)
        {
            case "channel":
                values["channel"] = message.Channel;
                break;
            case "suffix_word":
                values["channel"] = 0;
                values["unknown_96"] = message.Channel == 1 ? ChannelOneColour : -1;
                break;
            default:
                throw new ConfigException($"Build '{build}' must set room_chat_colour to channel or suffix_word in builds.yaml.");
        }
        return PacketCodec.Write(packets, build, "RoomChatReceived", values);
    }

    /// <summary>The colour word both kr-812 sub_8023E0 and kr-1256 sub_9DE360 give a channel-1 line (-24159).</summary>
    private const int ChannelOneColour = unchecked((int)0xFFFFA1A1);
}
