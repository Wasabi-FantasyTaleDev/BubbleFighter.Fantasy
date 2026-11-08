using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes native result bodies without deciding outcomes, rewards or result timing.</summary>
internal static class RoundResultWire
{
    /// <summary>Retains the audited masked-row marker and explicit absent-tail gates.</summary>
    internal sealed record MaskedRows(string RowMarker, IReadOnlyDictionary<string, object?> TailValues);

    /// <summary>Checks the outcome words and saturated byte totals before a fixed-row sender is published.</summary>
    internal static void RequireFixedScalarLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "RoundEndGame", "Round result sender",
            ("unknown_4", "u32"), ("unknown_8", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "RoundTimeOverTotals", "Round result sender",
            ("red_total", "u8"), ("blue_total", "u8"));
    }

    // The stat kinds the live score carries, in wire order.
    private const byte StatScore = 29;
    private const byte StatKnockdowns = 0;
    private const byte StatKills = 24;
    private const byte StatDeaths = 10;
    // The live stat that writes the score row's star count (kr-812 sub_804850 case 0x23 stores it at row +112).
    private const byte StatStars = 35;
    // kr-812 sub_804850 replaces row +128 from stat 13; kr-812 sub_7527C0 renders that count.
    private const byte StatCollectedLucci = 13;

    /// <summary>Resolves an explicitly audited pickup-counter field without enabling it for descendants.</summary>
    internal static string? CollectedLucciField(PacketTables tables, string build)
    {
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("collected_lucci_builds", out var raw)) return null;
        if (raw is not string[] builds || builds.Length == 0
            || builds.Distinct(StringComparer.Ordinal).Count() != builds.Length
            || builds.Any(candidate => !tables.Builds.Contains(candidate, StringComparer.Ordinal)))
            throw new ConfigException("collected_lucci_builds must list distinct known client builds.");
        if (!builds.Contains(build, StringComparer.Ordinal)) return null;
        if (!values.TryGetValue("collected_lucci_field", out var field) || field is not string name
            || !name.StartsWith("unknown_", StringComparison.Ordinal))
            throw new ConfigException("collected_lucci_field must name an audited unnamed score counter.");
        PacketLayoutContract.RequireFields(tables, build, "ScoreRow", "Collected coin counter", (name, "u32"));
        return name;
    }

    /// <summary>Writes the native live-score body from the audited field tables.</summary>
    /// <param name="tables">The audited client packet structures.</param>
    /// <param name="build">The client build whose structures are resolved.</param>
    /// <param name="message">The live score.</param>
    /// <returns>The packet body.</returns>
    internal static byte[] WriteScoreUpdate(PacketTables tables, string build, RoundScoreUpdated message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Seats);
        ArgumentNullException.ThrowIfNull(message.TeamTotals);
        RoundSeatStats? scorer = message.Seats.Count > 0 ? message.Seats[0] : null;
        RoundSeatStats? victim = message.Seats.Count > 1 ? message.Seats[1] : null;
        return PacketCodec.Write(tables, build, "RoundLiveScore", new Dictionary<string, object?>
        {
            // kr-812 sub_8046C0 skips the team-total write for zero but still applies seat stats.
            ["team"] = checked((byte)message.ScoringTeam),
            ["total"] = checked((ushort)TeamTotal(message.TeamTotals, message.ScoringTeam)),
            ["scorer_slot"] = unchecked((byte)(scorer?.Slot ?? -1)),
            ["victim_slot"] = unchecked((byte)(victim?.Slot ?? -1)),
            ["scorer_stats"] = StatValues(scorer, CollectedLucciField(tables, build) is not null, message.CollectedLucciOnly),
            ["victim_stats"] = StatValues(victim, CollectedLucciField(tables, build) is not null, message.CollectedLucciOnly),
        });
    }

    // Combat updates keep their established counter order; pickup events carry only the audited coin counter.
    private static IReadOnlyDictionary<string, object?>[] StatValues(RoundSeatStats? seat, bool collectedLucci, bool coinsOnly) => seat is null
        ? []
        : [.. coinsOnly ? Array.Empty<IReadOnlyDictionary<string, object?>>()
               : [StatValue(StatScore, seat.Score), StatValue(StatKnockdowns, seat.Knockdowns),
                  StatValue(StatKills, seat.Kills), StatValue(StatDeaths, seat.Deaths)],
           .. !coinsOnly && seat.Stars is { } stars ? [StatValue(StatStars, stars)] : Array.Empty<IReadOnlyDictionary<string, object?>>(),
           .. collectedLucci && seat.CollectedLucci is { } coins
               ? [StatValue(StatCollectedLucci, coins)] : Array.Empty<IReadOnlyDictionary<string, object?>>()];

    private static IReadOnlyDictionary<string, object?> StatValue(byte kind, int value) =>
        new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["value"] = checked((ushort)Math.Clamp(value, 0, ushort.MaxValue)),
        };

    private static int TeamTotal(IReadOnlyList<int> totals, int team) =>
        team >= 1 && team <= totals.Count ? totals[team - 1] : 0;

    /// <summary>
    /// The outcome word the S2C 705 handler <c>kr-812 sub_803DF0</c> reads: 3 sets the win flag and 1 or 2
    /// clear it, so 3 is the win, 2 the loss and 1 the draw. Zero would leave the result stage
    /// inert (its chain returns while that word is zero), so a draw is never sent as 0.
    /// </summary>
    /// <param name="outcome">The recipient's outcome.</param>
    /// <returns>The outcome word.</returns>
    internal static int OutcomeWord(RoundOutcome outcome) => outcome switch
    {
        RoundOutcome.Won => 3,
        RoundOutcome.Lost => 2,
        _ => 1,
    };

    /// <summary>Writes the two native byte totals with the existing saturation policy.</summary>
    internal static byte[] WriteTimeOverTotals(PacketTables tables, string build, IReadOnlyList<int> totals) =>
        PacketCodec.Write(tables, build, "RoundTimeOverTotals", new Dictionary<string, object?>
        {
            ["red_total"] = (byte)Math.Clamp(TeamTotal(totals, 1), 0, byte.MaxValue),
            ["blue_total"] = (byte)Math.Clamp(TeamTotal(totals, 2), 0, byte.MaxValue),
        });

    /// <summary>Writes the complete scoreboard using the build's resolved ScoreData and ScoreRow tables.</summary>
    /// <param name="tables">Audited packet tables.</param>
    /// <param name="build">The native client build.</param>
    /// <param name="message">Player rows and team totals to project into the native block.</param>
    /// <returns>The complete scoreboard body.</returns>
    internal static byte[] WriteScoreboard(PacketTables tables, string build, RoundScoreboard message)
    {
        return PacketCodec.Write(tables, build, "ScoreData", ScoreboardValues(tables, build, message));
    }

    /// <summary>Projects native scoreboard values for standalone or nested packet-table encoding.</summary>
    internal static IReadOnlyDictionary<string, object?> ScoreboardValues(PacketTables tables, string build, RoundScoreboard message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Rows);
        ArgumentNullException.ThrowIfNull(message.TeamTotals);
        var envelope = tables.Resolve(build, "ScoreData");
        var rowFields = tables.Resolve(build, "ScoreRow");
        var teams = Enumerable.Range(1, RunCount("teams")).Select(team =>
            new Dictionary<string, object?> { ["total"] = checked((uint)TeamTotal(message.TeamTotals, team)) }).ToArray();
        if (message.RoundWins is { } wins)
        {
            var series = RoundSeriesFields.Require(tables, build);
            if (message.ModeId != (int)GameModeId.Bomb || wins.Count != 2 || wins.Any(win => win < 0 || win > series.RoundsToWin))
                throw new ArgumentException("Bomb round wins must contain two counts within the native series limit.", nameof(message));
            for (var team = 1; team <= teams.Length; team++)
                teams[team - 1][series.TeamWins] = checked((uint)TeamTotal(wins, team));
        }
        var rows = Enumerable.Range(0, RunCount("rows"))
            .Select(slot => ScoreRowValues(tables, build, rowFields, FindRow(message, slot))).ToArray();
        return new Dictionary<string, object?>
        {
            ["winner_team"] = message.WinnerTeam is 1 or 2 ? message.WinnerTeam : -1,
            ["teams"] = teams,
            ["rows"] = rows,
        };

        int RunCount(string name)
        {
            var field = envelope.Single(field => field.Name == name);
            return PacketTables.TryParseRun(field.Type, out _, out var count)
                ? count
                : throw new ConfigException($"ScoreData.{name} must be a fixed structure run.");
        }
    }

    /// <summary>Writes a projected scoreboard row independently of its enclosing block.</summary>
    internal static byte[] WriteScoreRowBody(PacketTables tables, string build,
        IReadOnlyList<PacketField> fields, RoundScoreboardRow? row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        PacketCodec.WriteResolvedFields(new PacketWriter(buffer), tables, build, "ScoreRow", fields, ScoreRowValues(tables, build, fields, row));
        return buffer.WrittenSpan.ToArray();
    }

    private static RoundScoreboardRow? FindRow(RoundScoreboard message, int slot)
    {
        foreach (var row in message.Rows)
        {
            if (row.Slot == slot)
                return row;
        }
        return null;
    }

    // One seat of the scoreboard, field by field as the build's table lists them. A field the round
    // has a value for is filled by name; every other field carries the table's default. A seat nobody
    // sits in carries the table's `vacant` values, and 0 or nothing where the round would have filled.
    private static IReadOnlyDictionary<string, object?> ScoreRowValues(
        PacketTables tables, string build, IReadOnlyList<PacketField> fields, RoundScoreboardRow? row)
    {
        var values = new Dictionary<string, object?>(fields.Count, StringComparer.Ordinal);
        var collectedLucciField = CollectedLucciField(tables, build);
        foreach (var field in fields)
        {
            if (field.Name == collectedLucciField)
            {
                values[field.Name] = row?.Stats.CollectedLucci ?? 0;
                continue;
            }
            values[field.Name] = field.Type switch
            {
                "wstr" => row is not null && field.Name == "nickname" ? row.Nickname : string.Empty,
                "list<wstr>" => row?.Reward is { } reward && field.Name == "badge_images" ? reward.BonusBadges : Array.Empty<string>(),
                _ => ScoreRowNumber(tables, build, field, row),
            };
        }
        return values;
    }

    private static long ScoreRowNumber(PacketTables tables, string build, PacketField field, RoundScoreboardRow? row)
    {
        long? supplied = row is null ? null : field.Name switch
        {
            "award_display_id" => row.AwardDisplayId,
            "slot" => row.Slot,
            "team" => row.Team,
            "level" => NativeDisplayLevel.Project(tables, build, row.Level),
            "knock_downs" => row.Stats.Knockdowns,
            "kills" => row.Stats.Kills,
            "score" => row.Stats.Score,
            "stats_visible" => 1,
            "leveled_up" => row.Reward?.LeveledUp == true ? 1 : 0,
            "exp_level_start" => row.Reward?.LevelFloorExp ?? 0,
            "exp_total" => row.Reward?.TotalExp ?? 0,
            "exp_next_level" => row.Reward?.NextLevelFloorExp ?? 0,
            "exp_gained" => row.Reward?.ExpEarned ?? 0,
            "lucci_gained" => row.Reward?.LucciEarned ?? 0,
            "stars" => row.Stats.Stars ?? 0,
            _ => null,
        };
        if (supplied is { } value)
            return value;
        return row is null ? field.Vacant ?? (IsScoreRowValue(field.Name) ? 0 : field.Default) : field.Default;
    }

    // The names the round fills; on a seat nobody sits in they are 0 unless the table states a vacant value.
    private static bool IsScoreRowValue(string name) => name is "slot" or "team" or "level" or "knock_downs" or "kills"
        or "score" or "stats_visible" or "leveled_up" or "exp_level_start" or "exp_total" or "exp_next_level" or "exp_gained" or "lucci_gained"
        or "stars";

    /// <summary>Selects the native horde stage notification independently of the result body.</summary>
    internal static string? HordeOutcomeNotification(PlayValues.ScoreValues shape, RoundEnded message)
    {
        // cn-324 sub_438500 sets HordeModeMgr+5328 from these empty notifications.
        // cn-324 sub_43A4C0 / cn-324 sub_43B560 / cn-324 sub_43C810 consume it instead of team totals.
        if (!shape.HordeStageOutcome || (GameModeId)message.ModeId is not (GameModeId.Horde or GameModeId.HordeZhe or GameModeId.HordeTutor))
            return null;
        return message.Outcome == RoundOutcome.Won ? "gspHordeStageCleared_" : "gspHordeStageFailed_";
    }

    /// <summary>
    /// Writes the two-word <c>gspGameEndGame</c> body. The build table selects the outcome word:
    /// kr-812 and cn-324 use the second; cn-324 sub_408DF0 ret 4 changes ESP before cn-324 sub_6F1AE0 reads object +8.
    ///
    /// </summary>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Native client build.</param>
    /// <param name="outcome">The recipient's outcome.</param>
    /// <param name="outcomeFirst">Whether the outcome is the first word.</param>
    /// <returns>The packet body.</returns>
    internal static byte[] WriteGameEnd(PacketTables tables, string build, RoundOutcome outcome, bool outcomeFirst = false)
    {
        return WriteEndWord(tables, build, OutcomeWord(outcome), outcomeFirst);
    }

    internal static byte[] WriteGameEndForRecipient(PacketTables tables, string build, PlayValues.ScoreValues shape, RoundEnded message)
    {
        var word = OutcomeWord(message.Outcome);
        // cn-324 sub_67A760 selects escape with 2, defeated king with 3, and all-infected locally.
        // Both allegiances consume the same cause; recipient rewards retain their independent outcome.
        if (shape.ZombieResultCause && (GameModeId)message.ModeId == GameModeId.Zombie)
        {
            var humansWon = message.RecipientTeam switch
            {
                1 => message.Outcome == RoundOutcome.Won,
                14 => message.Outcome == RoundOutcome.Lost,
                _ => throw new InvalidOperationException("Zombie results require a human or zombie allegiance."),
            };
            word = message.Reason == RoundEndReason.TimeLimit ? 2 : humansWon ? 3 : 1;
        }
        // cn-324 sub_63B230 / kr-812 sub_5ACB20 compare result 2 with local team in native horde stage kind 10.
        if (shape.NpcTeamOutcome && (GameModeId)message.ModeId is GameModeId.Npc or GameModeId.Horde or GameModeId.HordeZhe or GameModeId.HordeTutor && message.Outcome != RoundOutcome.Draw)
        {
            var team = message.RecipientTeam == 0 ? 2 : message.RecipientTeam;
            if (team is not (1 or 2)) throw new InvalidOperationException("Native horde results require a red or blue team.");
            var blueWins = message.Outcome == RoundOutcome.Won ? team == 2 : team == 1;
            word = blueWins ? 2 : 3;
        }
        return WriteEndWord(tables, build, word, shape.OutcomeFirst);
    }

    private static byte[] WriteEndWord(PacketTables tables, string build, int word, bool outcomeFirst) =>
        PacketCodec.Write(tables, build, "RoundEndGame", new Dictionary<string, object?>
        {
            ["unknown_4"] = outcomeFirst ? word : 0,
            ["unknown_8"] = outcomeFirst ? 0 : word,
        });

    /// <summary>
    /// Fills the result rows in the modes that keep team totals: each seat's player, team and level, and the reward
    /// figures once they are known. Final team results carry the decided winner (zero means draw), red 1, blue 2 or a
    /// zombie-mode allegiance (14, 15 or 34); live rows retain the client's unspecified-winner sentinel until the result
    /// sequence commits an outcome.
    /// </summary>
    internal static byte[] WriteMaskedScoreboard(PacketTables packets, string build, MaskedRows shape,
        ISession session, IGameText? texts, RoundScoreboard message)
    {
        // kr-1256 sub_429620 keeps any winner up to 36; kr-1256 sub_9E9710 and cn-774 sub_A91FB0 compare it with the
        // local team, so the zombie allegiances (14 against human 1, 15 against human 2) reach the win/lose result.
        if (message.WithRewards && message.WinnerTeam is not (0 or 1 or 2 or 14 or 15)
            && !((GameModeId)message.ModeId is (GameModeId.Zombie or GameModeId.ZombieGhost or GameModeId.ZombieEscape) && message.WinnerTeam == 34))
            throw new InvalidOperationException("A final team scoreboard requires a supported winner allegiance for its mode.");
        var rows = message.Rows.OrderBy(row => row.Slot).ToArray();
        RoundSeriesFields? series = null;
        if (message.RoundWins is { } wins)
        {
            series = RoundSeriesFields.Require(packets, build);
            if (message.ModeId != (int)GameModeId.Bomb || wins.Count != 2 || wins.Any(win => win < 0 || win > series.RoundsToWin))
                throw new ArgumentException("Bomb round wins must contain two counts within the native series limit.", nameof(message));
        }
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
                [series?.TeamWins ?? "unknown_308"] = message.RoundWins?[team] ?? 0,
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
            PacketCodec.Write(writer, packets, build, "ScoreRow", MaskedRowValues(packets, build, shape.RowMarker, row, session, texts));
        // One word of each trailing array per set mask bit, ascending; the emit recipe carries zeros.
        for (var gated = 0; gated < rows.Length * 2; gated++)
            PacketCodec.Write(writer, packets, build, "ScoreGatedWord", new Dictionary<string, object?> { ["unknown_0"] = 0 });
        PacketCodec.Write(writer, packets, build, "ScoreDataTail", shape.TailValues);
        var buffer = (System.Buffers.ArrayBufferWriter<byte>)writer.Buffer;
        return buffer.WrittenSpan.ToArray();
    }

    // kr-1256 sub_9E5E70 seeds the row identity; the tables place the shared reward values in each native row.
    private static Dictionary<string, object?> MaskedRowValues(PacketTables packets, string build, string rowMarker,
        RoundScoreboardRow row, ISession session, IGameText? texts) => new()
    {
        [rowMarker] = 1,
        ["user_number"] = checked((int)row.UserNumber),
        ["nickname"] = RoomNames.Resolve(row.Nickname, row.NicknameKey, session, texts),
        ["team"] = checked((byte)row.Team),
        ["level"] = NativeDisplayLevel.Project(packets, build, row.Reward?.Level ?? row.Level),
        ["exp_level_start"] = checked((int)(row.Reward?.LevelFloorExp ?? 0)),
        ["exp_total"] = checked((int)(row.Reward?.TotalExp ?? 0)),
        ["exp_next_level"] = checked((int)(row.Reward?.NextLevelFloorExp ?? 0)),
        ["exp_gained"] = row.Reward?.ExpEarned ?? 0,
        ["lucci_gained"] = row.Reward?.LucciEarned ?? 0,
    };

}
