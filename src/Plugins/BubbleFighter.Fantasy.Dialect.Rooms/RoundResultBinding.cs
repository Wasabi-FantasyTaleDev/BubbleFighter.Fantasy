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
      IMessageSender<RoundScoreboard>, IMessageSender<RoundEnded>, IMessageSender<RoundResultBanner>,
      IMessageSender<ResultStageAdvanced>, IRoundResultSequencePolicy
{
    private Dictionary<string, ushort> _s2c = new();
    private IGameText? _texts;
    private string _rowMarker = "";
    private IReadOnlyDictionary<string, object?> _tailValues = new Dictionary<string, object?>();
    private bool _hasBannerScoreTail;

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
        if (table.Find(PacketDirection.ServerToClient, "gspRoundResultBanner_") is { } bannerOpcode)
            _s2c["banner"] = bannerOpcode;
        else if (build == "kr-1256")
            _s2c["banner"] = Resolve(PacketDirection.ServerToClient, "Unknown613");
        PacketLayoutContract.RequireFields(packets, build, "RoundLiveScore", "Typed results", ("team", "u8"), ("total", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultOpen", "Typed results",
            ("unknown_n0", "u32"), ("unknown_n1", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultClose", "Typed results", ("unknown_n0", "u8"));
        if (_s2c.ContainsKey("banner"))
            PacketLayoutContract.RequireFields(packets, build, "RoundResultBanner", "Typed results",
                ("unknown_n0", "u32"), ("unknown_n1", "u32"), ("unknown_n2", "u8"), ("unknown_n3", "u8"));
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
        _hasBannerScoreTail = tail.Any(field => field.Name == "unknown_19196")
            && tail.Any(field => field.Name == "unknown_19197");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundEndRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreboard>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundEnded>>(this));
        if (_s2c.ContainsKey("banner"))
            context.Effect(context.Services.RegisterMany<IMessageSender<RoundResultBanner>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ResultStageAdvanced>>(this));
        new RoundResetBinding(protocol, table, packets, build, logger).Mount(context);
        // kr-1256 sub_6E5740, cn-774 sub_8A5E30 and tw-454 sub_7191E0 send header-only stage completion.
        RoomRoutes.Bind(context, protocol, table, logger, endRequest, body =>
        {
            RoomRoutes.RequireEmpty(body, endRequest);
            return new RoundEndRequested();
        });
    }

    /// <summary>
    /// Sends both team totals with each star holder's absolute count in the mode-5/6 group list.
    /// The native group array is one-based: seat slot N is written at group index N+1.
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoundScoreUpdated message, CancellationToken ct)
    {
        var liveFields = packets.Resolve(build, "RoundLiveScore");
        var hasGroupUpdates = liveFields.Any(field => field.Name == "unknown_32");
        var starGroups = hasGroupUpdates
            ? (message.Scoreboard?.Rows ?? [])
                .Where(row => row.Stats.Stars is not null)
                .Select(row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["kind"] = checked((byte)(row.Slot + 1)),
                    ["value"] = checked((ushort)Math.Clamp(row.Stats.Stars!.Value, 0, ushort.MaxValue)),
                }).ToArray()
            : [];
        if (starGroups.Any(group => Convert.ToInt32(group["kind"]) is < 1 or > 36))
            throw new InvalidOperationException("Star live-score group index must be between 1 and 36.");
        for (byte team = 1; team <= 2; team++)
        {
            var values = new Dictionary<string, object?>
            {
                ["team"] = team,
                ["total"] = checked((ushort)message.TeamTotals[team - 1]),
            };
            if (hasGroupUpdates)
                values["unknown_32"] = starGroups;
            await session.SendAsync(S2C("score"), PacketCodec.Write(packets, build, "RoundLiveScore", values), ct)
                .ConfigureAwait(false);
        }

        foreach (var row in (message.Scoreboard?.Rows ?? []).Where(row => row.Stats.Stars is not null))
        {
            if (row.Slot is < 0 or > byte.MaxValue)
                throw new InvalidOperationException($"Star live-score row has invalid slot {row.Slot}.");
            await session.SendAsync(S2C("score"),
                PacketCodec.Write(packets, build, "RoundLiveScore", new Dictionary<string, object?>
                {
                    // A zero team skips the team-total write while still applying the named seat row.
                    ["team"] = (byte)0,
                    ["total"] = (ushort)0,
                    ["scorer_slot"] = (byte)row.Slot,
                    ["victim_slot"] = byte.MaxValue,
                    ["scorer_stats"] = new[]
                    {
                        (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                        {
                            // Live stat 19 writes ScoreRow +0x78, the star-count slot the HUD reads.
                            ["kind"] = (byte)19,
                            ["value"] = checked((ushort)Math.Clamp(row.Stats.Stars!.Value, 0, ushort.MaxValue)),
                        },
                    },
                    ["victim_stats"] = Array.Empty<IReadOnlyDictionary<string, object?>>(),
                    ["unknown_32"] = Array.Empty<IReadOnlyDictionary<string, object?>>(),
                }), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Opens the result screen (kr-1256 sub_9E88C0 reads two words, which the packet relay path sends as zero).</summary>
    public ValueTask SendAsync(ISession session, RoundEnded message, CancellationToken ct) =>
        session.SendAsync(S2C("open"), PacketCodec.Write(packets, build, "RoundResultOpen", new Dictionary<string, object?>
        {
            ["unknown_n0"] = message.TeamTotals.Count > 0 ? message.TeamTotals[0] : 0,
            ["unknown_n1"] = message.TeamTotals.Count > 1 ? message.TeamTotals[1] : 0,
        }), ct);

    /// <summary>Carries the two bytes the native time-over banner displays (kr-1256 S2C 613).</summary>
    public ValueTask SendAsync(ISession session, RoundResultBanner message, CancellationToken ct)
    {
        if (!_s2c.ContainsKey("banner"))
            throw new InvalidOperationException($"Result banners are not mounted for {build}.");
        return session.SendAsync(S2C("banner"), PacketCodec.Write(packets, build, "RoundResultBanner",
            new Dictionary<string, object?>
            {
                ["unknown_n0"] = 0,
                ["unknown_n1"] = 0,
                ["unknown_n2"] = checked((byte)Math.Clamp(message.FirstScore, 0, byte.MaxValue)),
                ["unknown_n3"] = checked((byte)Math.Clamp(message.SecondScore, 0, byte.MaxValue)),
            }), ct);
    }

    /// <summary>
    /// Fills the result rows in the modes that keep team totals: each seat's player, team and level, and the reward
    /// figures once they are known. Final team results carry the decided winner (zero means draw); live rows
    /// retain the client's unspecified-winner sentinel until the result sequence commits an outcome.
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundScoreboard message, CancellationToken ct)
    {
        if (!scoreboardModes.Contains(message.ModeId))
            return ValueTask.CompletedTask;
        if (message.WithRewards && message.WinnerSlot is { } winnerSlot)
        {
            if (winnerSlot is < 0 or >= 36)
                throw new InvalidOperationException("A final individual scoreboard requires a winner slot from 0 to 35.");
        }
        else if (message.WithRewards && message.WinnerTeam is not (0 or 1 or 2))
        {
            throw new InvalidOperationException("A final team scoreboard requires winner 0, 1 or 2.");
        }
        var rows = message.Rows.OrderBy(row => row.Slot).ToArray();
        var writer = PacketWriter.Create();
        PacketCodec.Write(writer, packets, build, "ScoreDataLead", new Dictionary<string, object?>
        {
            ["head_count"] = 2,
            // cn-774 sub_4917E0 resolves 0/1/2 as draw/red/blue; cn-774 sub_8E40C0 and
            // tw-454 sub_A646E0 repeat the result phase/banner work when installing the scoreboard.
            ["winner_slot"] = message.WithRewards
                ? message.WinnerSlot is { } winnerSeat
                    ? message.Presentation == RoundScoreboardPresentation.StarIndividual ? checked(winnerSeat + 1) : message.Rows.FirstOrDefault(row => row.Slot == winnerSeat)?.Team ?? 0
                    : message.WinnerTeam
                : 255,
            // A star time-over result has already announced the winner through S2C 613. Its final
            // scoreboard must not replay that banner or reset its displayed totals.
            ["banner_gate"] = message.WithRewards
                && (message.Presentation == RoundScoreboardPresentation.Generic || !message.TimeOver),
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
            PacketCodec.Write(writer, packets, build, "ScoreRow", RowValues(row, session, message.Presentation, message.WithRewards, message.TimeOver));
        // One word of each trailing array per set mask bit, ascending; the emit recipe carries zeros.
        for (var gated = 0; gated < rows.Length * 2; gated++)
            PacketCodec.Write(writer, packets, build, "ScoreGatedWord", new Dictionary<string, object?> { ["unknown_0"] = 0 });
        var tailValues = new Dictionary<string, object?>(_tailValues);
        if (_hasBannerScoreTail)
        {
            tailValues["unknown_19196"] = checked((byte)Math.Clamp(message.BannerFirstScore, 0, byte.MaxValue));
            tailValues["unknown_19197"] = checked((byte)Math.Clamp(message.BannerSecondScore, 0, byte.MaxValue));
        }
        PacketCodec.Write(writer, packets, build, "ScoreDataTail", tailValues);
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
    private Dictionary<string, object?> RowValues(RoundScoreboardRow row, ISession session, RoundScoreboardPresentation presentation, bool withRewards, bool timeOver)
    {
        var values = new Dictionary<string, object?>
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
        if (presentation == RoundScoreboardPresentation.StarIndividual)
        {
            // The final individual result reads +0x54; the time-over rank panel also reads +0x78.
            values["unknown_84"] = row.Stats.Stars ?? 0;
            values["unknown_120"] = row.Stats.Stars ?? 0;
        }
        else if (presentation == RoundScoreboardPresentation.StarTeam)
        {
            // The final team result reads +0x78; both scoreboard passes also refresh the generic
            // team score at +0x54 used by the time-over victory panel.
            values["unknown_120"] = row.Stats.Stars ?? 0;
            values["unknown_84"] = row.Stats.Stars ?? 0;
        }
        return values;
    }

    private ushort Resolve(PacketDirection direction, params string[] names)
    {
        foreach (var name in names)
            if (table.Find(direction, name) is { } opcode)
                return opcode;
        throw new ConfigException($"Typed results require {direction} {string.Join(" or ", names)}.");
    }

    private ushort S2C(string name) => _s2c[name];
}
