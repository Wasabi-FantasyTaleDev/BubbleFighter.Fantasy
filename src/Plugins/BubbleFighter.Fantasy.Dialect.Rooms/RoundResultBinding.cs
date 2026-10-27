using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Adapts modern live scores and result stages to shared typed messages. Packet tables own each native row,
/// reward tail and field order; the result is composed in phases because its heads are count-gated and its
/// rows and trailing arrays are mask-gated (kr-1256 sub_429620, cn-774 sub_490F80, tw-454 sub_47E640).
/// </summary>
internal sealed class RoundResultBinding(string protocol, IOpcodeTable table, PacketTables packets, string build,
    IReadOnlyCollection<int> scoreboardModes, ILogger logger)
    : IInboundMessageBinding<RoundEndRequested>, IMessageSender<RoundScoreUpdated>,
      IMessageSender<RoundScoreboard>, IMessageSender<RoundEnded>, IMessageSender<ResultStageAdvanced>, IRoundResultSequencePolicy
{
    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    private IGameText? _texts;
    private string _rowMarker = "";
    private IReadOnlyDictionary<string, object?> _tailValues = new Dictionary<string, object?>();

    public string Protocol => protocol;

    /// <summary>The 612 handler (kr-1256 sub_9E88C0) opens the result scene the scoreboard then fills.</summary>
    public bool EndBeforeScoreboard => true;

    /// <summary>One S2C 614 carries the rewards between 612 and 615.</summary>
    public bool SingleScoreboard => true;

    /// <summary>Mounts the result frames; every written field must exist in this build's tables.</summary>
    internal void Mount(IPluginContext context)
    {
        _texts = context.Services.TryGet<IGameText>(out var texts) ? texts : null;
        _s2c = new Dictionary<string, ushort>
        {
            ["score"] = Resolve(PacketDirection.ServerToClient, "gspUpdateScoreData"),
            ["open"] = Resolve(PacketDirection.ServerToClient, "gspRoundResultOpen_", "Unknown612"),
            ["board"] = Resolve(PacketDirection.ServerToClient, "gspRoundScoreboard_", "Unknown614"),
            ["reward"] = Resolve(PacketDirection.ServerToClient, "gspRoundRewardStage_", "Unknown615"),
        };
        PacketLayoutContract.RequireFields(packets, build, "RoundLiveScore", "Typed results", ("team", "u8"), ("total", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultOpen", "Typed results",
            ("unknown_n0", "u32"), ("unknown_n1", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultClose", "Typed results", ("unknown_n0", "u8"));
        foreach (var structure in new[] { "ScoreDataLead", "ScoreHeadEntry", "ScoreDataMask", "ScoreRow", "ScoreGatedWord", "ScoreDataTail" })
            _ = packets.Resolve(build, structure);
        if (!packets.ValuesOf(build).TryGetValue("round_score_row_marker", out var marker) || marker is not string markerName)
            throw new ConfigException("Typed results require round_score_row_marker in the build's packet values.");
        _rowMarker = markerName;
        if (packets.Resolve(build, "ScoreRow")[0].Name != _rowMarker)
            throw new ConfigException("round_score_row_marker must identify the native row's leading word.");
        PacketLayoutContract.RequireFields(packets, build, "ScoreRow", "Typed results",
            (_rowMarker, "u32"), ("user_number", "u32"), ("nickname", "wstr"), ("team", "u8"),
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
        _tailValues = tailValues;
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundEndRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreboard>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundEnded>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ResultStageAdvanced>>(this));
        new RoundResetBinding(protocol, table, packets, build, logger).Mount(context);
        // kr-1256 sub_6E5740, cn-774 sub_8A5E30 and tw-454 sub_7191E0 send header-only stage completion.
        RoomRoutes.Bind(context, protocol, table, logger, endRequest, body =>
        {
            RoomRoutes.RequireEmpty(body, endRequest);
            return new RoundEndRequested();
        });
    }

    /// <summary>Sends both team totals through gspUpdateScoreData (kr-1256 sub_9E3420 reads team, total, two slots and three empty vectors).</summary>
    public async ValueTask SendAsync(ISession session, RoundScoreUpdated message, CancellationToken ct)
    {
        for (byte team = 1; team <= 2; team++)
            await session.SendAsync(S2C("score"),
                PacketCodec.Write(packets, build, "RoundLiveScore", new Dictionary<string, object?>
                {
                    ["team"] = team,
                    ["total"] = checked((ushort)message.TeamTotals[team - 1]),
                }), ct).ConfigureAwait(false);
    }

    /// <summary>Opens the result screen (kr-1256 sub_9E88C0 reads two words, which the packet relay path sends as zero).</summary>
    public ValueTask SendAsync(ISession session, RoundEnded message, CancellationToken ct) =>
        session.SendAsync(S2C("open"), PacketCodec.Write(packets, build, "RoundResultOpen", new Dictionary<string, object?>
        {
            ["unknown_n0"] = 0,
            ["unknown_n1"] = 0,
        }), ct);

    /// <summary>
    /// Fills the result rows in the modes that keep team totals: each seat's player, team and level, and the reward
    /// figures once they are known. Final team results carry the decided winner (zero means draw); live rows
    /// retain the client's unspecified-winner sentinel until the result sequence commits an outcome.
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundScoreboard message, CancellationToken ct)
    {
        if (!scoreboardModes.Contains(message.ModeId))
            return ValueTask.CompletedTask;
        if (message.WithRewards && message.WinnerTeam is not (0 or 1 or 2))
            throw new InvalidOperationException("A final team scoreboard requires winner 0, 1 or 2.");
        var rows = message.Rows.OrderBy(row => row.Slot).ToArray();
        var writer = PacketWriter.Create();
        PacketCodec.Write(writer, packets, build, "ScoreDataLead", new Dictionary<string, object?>
        {
            ["head_count"] = 2,
            // cn-774 sub_4917E0 resolves 0/1/2 as draw/red/blue; cn-774 sub_8E40C0 and
            // tw-454 sub_A646E0 repeat the result phase/banner work when installing the scoreboard.
            ["winner_slot"] = message.WithRewards ? message.WinnerTeam : 255,
            ["banner_gate"] = message.WithRewards,
        });
        for (var team = 0; team < 2; team++)
            PacketCodec.Write(writer, packets, build, "ScoreHeadEntry", new Dictionary<string, object?>
            {
                ["team_total"] = checked((ushort)message.TeamTotals[team]),
                ["unknown_160"] = 0,
                ["unknown_308"] = 0,
            });
        var mask = 0UL;
        // All three readers consume exactly 36 row bits: kr-1256 sub_429620, cn-774 sub_490F80, tw-454 sub_47E640.
        foreach (var row in rows)
        {
            if (row.Slot is < 0 or >= 36)
                throw new InvalidOperationException($"Scoreboard row slot {row.Slot} cannot be a presence-mask bit.");
            var bit = 1UL << row.Slot;
            if ((mask & bit) != 0)
                throw new InvalidOperationException($"Scoreboard row for slot {row.Slot} appears more than once.");
            mask |= bit;
        }
        PacketCodec.Write(writer, packets, build, "ScoreDataMask", new Dictionary<string, object?>
        {
            ["presence_mask"] = unchecked((long)mask),
        });
        foreach (var row in rows)
            PacketCodec.Write(writer, packets, build, "ScoreRow", RowValues(row, session));
        // One word of each trailing array per set mask bit, ascending; the emit recipe carries zeros.
        for (var gated = 0; gated < rows.Length * 2; gated++)
            PacketCodec.Write(writer, packets, build, "ScoreGatedWord", new Dictionary<string, object?> { ["unknown_0"] = 0 });
        PacketCodec.Write(writer, packets, build, "ScoreDataTail", _tailValues);
        var buffer = (System.Buffers.ArrayBufferWriter<byte>)writer.Buffer;
        return session.SendAsync(S2C("board"), buffer.WrittenSpan.ToArray(), ct);
    }

    /// <summary>Closes the result into the reward stage (kr-1256 sub_9E9EF0 takes one flag, sent as zero).</summary>
    public ValueTask SendAsync(ISession session, ResultStageAdvanced message, CancellationToken ct) =>
        message.Stage == ResultStage.Reward
            ? session.SendAsync(S2C("reward"), PacketCodec.Write(packets, build, "RoundResultClose",
                new Dictionary<string, object?> { ["unknown_n0"] = 0 }), ct)
            : ValueTask.CompletedTask;

    // kr-1256 sub_9E5E70 seeds the row identity; the tables place the shared reward values in each native row.
    private Dictionary<string, object?> RowValues(RoundScoreboardRow row, ISession session) => new()
    {
        [_rowMarker] = 1,
        ["user_number"] = checked((int)row.UserNumber),
        ["nickname"] = RoomNames.Resolve(row.Nickname, row.NicknameKey, session, _texts),
        ["team"] = checked((byte)row.Team),
        ["level"] = row.Reward?.Level ?? row.Level,
        ["exp_level_start"] = checked((int)(row.Reward?.LevelFloorExp ?? 0)),
        ["exp_total"] = checked((int)(row.Reward?.TotalExp ?? 0)),
        ["exp_next_level"] = checked((int)(row.Reward?.NextLevelFloorExp ?? 0)),
        ["exp_gained"] = row.Reward?.ExpEarned ?? 0,
        ["lucci_gained"] = row.Reward?.LucciEarned ?? 0,
    };

    private ushort Resolve(PacketDirection direction, params string[] names)
    {
        foreach (var name in names)
            if (table.Find(direction, name) is { } opcode)
                return opcode;
        throw new ConfigException($"Typed results require {direction} {string.Join(" or ", names)}.");
    }

    private ushort S2C(string name) => _s2c[name];
}
