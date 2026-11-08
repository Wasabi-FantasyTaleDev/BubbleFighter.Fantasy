using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Mounts audited result senders and the native end request. Packet tables own each native row,
/// reward tail and field order; the result is composed in phases because its heads are count-gated and its
/// rows and trailing arrays are mask-gated (kr-1256 sub_429620, cn-774 sub_490F80, tw-454 sub_47E640).
/// </summary>
internal sealed class RoundResultBinding(string protocol, IOpcodeTable table, PacketTables packets, string build,
    IReadOnlyCollection<int> scoreboardModes, ILogger logger)
    : IInboundMessageBinding<RoundEndRequested>
{
    public string Protocol => protocol;

    /// <summary>Mounts the result frames; every written field must exist in this build's tables.</summary>
    internal void Mount(IPluginContext context)
    {
        var texts = context.Services.TryGet<IGameText>(out var value) ? value : null;
        _ = Resolve(PacketDirection.ServerToClient, "gspUpdateScoreData");
        _ = Resolve(PacketDirection.ServerToClient, "gspRoundResultOpen_", "Unknown612");
        _ = Resolve(PacketDirection.ServerToClient, "gspRoundScoreboard_", "Unknown614");
        _ = Resolve(PacketDirection.ServerToClient, "gspRoundRewardStage_", "Unknown615");
        PacketLayoutContract.RequireFields(packets, build, "RoundLiveScore", "Typed results", ("team", "u8"), ("total", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultOpen", "Typed results",
            ("unknown_n0", "u32"), ("unknown_n1", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultClose", "Typed results", ("unknown_n0", "u8"));
        foreach (var structure in new[] { "ScoreDataLead", "ScoreHeadEntry", "ScoreDataMask", "ScoreRow", "ScoreGatedWord", "ScoreDataTail" })
            _ = packets.Resolve(build, structure);
        if (!packets.ValuesOf(build).TryGetValue("round_score_row_marker", out var marker) || marker is not string markerName)
            throw new ConfigException("Typed results require round_score_row_marker in the build's packet values.");
        if (packets.Resolve(build, "ScoreRow")[0].Name != markerName)
            throw new ConfigException("round_score_row_marker must identify the native row's leading word.");
        PacketLayoutContract.RequireFields(packets, build, "ScoreRow", "Typed results",
            (markerName, "u32"), ("user_number", "u32"), ("nickname", "wstr"), ("team", "u8"),
            ("level", "u32"), ("exp_level_start", "u32"), ("exp_total", "u32"),
            ("exp_next_level", "u32"), ("exp_gained", "u32"), ("lucci_gained", "u32"));
        var endRequest = table.Find(PacketDirection.ClientToServer, "gcpReqEndStage_") is not null ? "gcpReqEndStage_" : "Unknown625";
        _ = Resolve(PacketDirection.ClientToServer, endRequest);
        // Native conditional strings need an explicit gate value even when that value is the table's default.
        var tail = packets.Resolve(build, "ScoreDataTail");
        var tailValues = new Dictionary<string, object?>();
        foreach (var field in tail)
            if (PacketTables.TryParseWhen(field.Type, out var gate, out var mask, out _))
            {
                var source = tail.Single(candidate => candidate.Name == gate);
                if ((unchecked((ulong)source.Default) & mask) != 0)
                    throw new ConfigException($"Typed results require the default {gate} to leave {field.Name} absent.");
                tailValues[gate] = source.Default;
            }
        var sender = new RoundResultSender(protocol, packets, build, _ => table, scoreboardModes,
            new RoundResultWire.MaskedRows(markerName, tailValues), texts);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundEndRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreUpdated>>(sender));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreboard>>(sender));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundEnded>>(sender));
        context.Effect(context.Services.RegisterMany<IMessageSender<ResultStageAdvanced>>(sender));
        var respawn = new RoundRespawnBinding(protocol, table, packets, build);
        respawn.Mount(context);
        RoomRoutes.Bind(context, protocol, table, logger, "gcpRespawn", respawn.Read);
        // kr-1256 sub_6E5740, cn-774 sub_8A5E30 and tw-454 sub_7191E0 send header-only stage completion.
        RoomRoutes.Bind(context, protocol, table, logger, endRequest, body =>
        {
            RoomRoutes.RequireEmpty(body, endRequest);
            return new RoundEndRequested();
        });
    }

    private ushort Resolve(PacketDirection direction, params string[] names)
    {
        foreach (var name in names)
            if (table.Find(direction, name) is { } opcode)
                return opcode;
        throw new ConfigException($"Typed results require {direction} {string.Join(" or ", names)}.");
    }
}
