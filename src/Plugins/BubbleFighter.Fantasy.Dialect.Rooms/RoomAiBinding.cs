using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts the native computer-player popup without interpreting its preset as a human account.</summary>
internal sealed class RoomAiBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoomAiAddRequested>, IInboundMessageBinding<RoomAiRemoveRequested>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        _ = table.C2S("gcpRoomAddNpcBot_");
        _ = table.C2S("gcpRoomRemoveNpcBot_");
        _ = table.S2C("gspRoomNpcBotControllerChanged_");
        var direct = packets.Resolve(build, "RoomAiAddRequest").Any(field => field.Name == "selection_mode");
        PacketLayoutContract.RequireFields(packets, build, "RoomAiAddRequest", "Room AI",
            ("team", direct ? "u8" : "optional<0x3A,RoomAiTeam>"), ("slot", "u32"), ("difficulty", "u32"), ("preset", "u32"));
        if (direct)
        {
            PacketLayoutContract.RequireFields(packets, build, "RoomAiAddRequest", "Room AI", ("selection_mode", "u32"));
            PacketLayoutContract.RequireFields(packets, build, "UserSlot", "Room AI",
                ("is_ai", "bool"), ("ai_controller_user_number", "u32"), ("ai_difficulty", "u32"), ("ai_preset", "u32"));
        }
        else
            PacketLayoutContract.RequireFields(packets, build, "RoomAiTeam", "Room AI", ("team", "u8"));
        PacketLayoutContract.RequireFields(packets, build, "RoomAiRemoveRequest", "Room AI", ("slot", "u32"));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomAiAddRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomAiRemoveRequested>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, "gcpRoomAddNpcBot_", ReadAdd);
        RoomRoutes.Bind(context, protocol, table, logger, "gcpRoomRemoveNpcBot_", body =>
            new RoomAiRemoveRequested((int)PacketCodec.ReadScalars(packets, build, "RoomAiRemoveRequest", body)["slot"]));
    }

    internal RoomAiAddRequested ReadAdd(ReadOnlySpan<byte> body)
    {
        // kr-1256 sub_DBD9A0 writes exactly these two optional markers; other bytes are malformed.
        var direct = packets.Resolve(build, "RoomAiAddRequest").Any(field => field.Name == "selection_mode");
        if (!direct && (body.IsEmpty || body[0] is not (0x3A or 0xBA)))
            throw new PacketFormatException("Room AI team requires a present or absent native marker.", 0);
        var fields = PacketCodec.Read(packets, build, "RoomAiAddRequest", body);
        int? team = direct ? Convert.ToInt32(fields["team"])
            : fields["team"] is IReadOnlyDictionary<string, object?> explicitTeam
                ? Convert.ToInt32(explicitTeam["team"]) : null;
        var selection = RoomAiSelection.Preset;
        if (direct)
        {
            // cn-774 sub_E4E870/sub_E4E8B0/sub_E4E8F0 select random, clone and explicit popup operations.
            selection = (int)fields["selection_mode"]! switch
            {
                0 => RoomAiSelection.Random,
                1 => RoomAiSelection.Clone,
                2 => RoomAiSelection.Preset,
                _ => throw new PacketFormatException("Unknown native room AI selection operation.", 1),
            };
            if (team is < 0 or > 2 || (selection != RoomAiSelection.Preset
                && ((int)fields["slot"]! != -1 || (int)fields["difficulty"]! != -1 || (int)fields["preset"]! != -1)))
                throw new PacketFormatException("Native automatic AI selection requires a team and three absent selectors.", 0);
        }
        return new(team, (int)fields["slot"]!, (int)fields["difficulty"]!, (int)fields["preset"]!) { Selection = selection };
    }
}
