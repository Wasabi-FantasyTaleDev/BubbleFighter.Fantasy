using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The legacy round's score and result as typed messages: the clients' knock-down and
/// finishing-blow reports, the live score, the final scoreboard, the round's end and the move to
/// the reward stage. Every layout is the kr-812 reader named beside its writer; the values are
/// those of a two-team round, which is also
/// what the length-pinning tests use. The codec never invents a seat: it translates the client's
/// own references through <see cref="SeatOf"/> and lets the round reject what is not seated.
/// </summary>
internal sealed partial class PlayRoomCodec
{
    // The stat kinds the live score carries, in wire order.
    private const byte StatScore = 29;
    private const byte StatKnockdowns = 0;
    private const byte StatKills = 24;
    private const byte StatDeaths = 10;
    // The live stat that writes the score row's star count (kr-812 sub_804850 case 0x23 stores it at row +112).
    private const byte StatStars = 35;

    // The seat a client reference belongs to: a client names its entities as
    // 1000000 + 100000 * seat, so the seat is the quotient (dc.c:598904).
    private const int SeatSerialBase = 1_000_000;
    private const int SeatSerialStride = 100_000;

    private ScoreShape Score => play.Score ?? new ScoreShape();

    private void MountResult(IPluginContext context)
    {
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var table = tables.ForProtocol(protocol);
        // id-400 sub_78B4D0 and id-1809 sub_7AF440 consume results independently of kill reports.
        // Keep the native result surface available while unaudited inbound reports remain unbound.
        if (packetTables.CapabilityOf(lineage, "round_results"))
        {
            _ = packetTables.Resolve(lineage, "ScoreData");
            _ = packetTables.Resolve(lineage, "ScoreRow");
            foreach (var name in new[] { "gspGameBroadcastPlayerScoreData", "gspGameTimeOverEnd", "gspGameShowResult_" })
                _ = table.S2C(name);
            if (Score.LiveScorePacket)
            {
                _ = table.S2C("gspUpdateScoreData");
                _ = packetTables.Resolve(lineage, "RoundLiveScore");
                _ = packetTables.Resolve(lineage, "RoundLiveScoreStat");
            }
            context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(this));
            context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(this));
            context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(this));
        }
        var directReports = packetTables.CapabilityOf(lineage, "direct_kill_reports");
        var referenceReports = packetTables.CapabilityOf(lineage, "character_reference_score_reports");
        var optimizedScores = play.ScoreReports && !Score.WideReport;

        void OnReport(string name, Func<ISession, ReadOnlySpan<byte>, IMessage?> decode)
        {
            if (table.Find(PacketDirection.ClientToServer, name) is not { } opcode)
            {
                logger.LogInformation("dialect.rooms: {Protocol} has no {Name}; the result flow gets no score reports.", protocol, name);
                return;
            }
            context.Effect(services.Get<IPacketRouter>().Register(protocol, opcode, async (session, packet, ct) =>
            {
                IMessage? message;
                try
                {
                    message = decode(session, packet.Payload.Span);
                }
                catch (PacketFormatException ex)
                {
                    // A score report the layout does not fit costs that report, not the player's connection.
                    logger.LogWarning("Session {Id} {Name} does not match the expected layout ({Reason}); ignored.",
                        session.Id, name, ex.Message);
                    return;
                }
                if (message is null)
                    return;
                var handled = message switch
                {
                    RoundKillCreditReported credit => await messages.DispatchAsync(protocol, session, credit, ct).ConfigureAwait(false),
                    RoundFinishingBlowReported finishing => await messages.DispatchAsync(protocol, session, finishing, ct).ConfigureAwait(false),
                    RoundStarReported stars => await messages.DispatchAsync(protocol, session, stars, ct).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("A score decoder returned an unsupported typed report."),
                };
                if (!handled)
                    logger.LogWarning("Session {Id} {Name} has no handler on {Protocol}.", session.Id, name, protocol);
            }));
        }

        // The star counts arrive in the report the build states (score.star_report), whether or not the build's
        // kill reports are read; where they are, the same route decodes both.
        var killReports = play.ScoreReports || directReports || referenceReports;
        if (StarReports)
        {
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundStarReported>>(this));
            if (Score.StarReport == StarReportPacket.Optimized)
            {
                _ = packetTables.Resolve(lineage, "RoundOptimizedScoreReport");
                _ = table.C2S("gcpGameUpdateScoreOptimze");
                if (!optimizedScores)
                    OnReport("gcpGameUpdateScoreOptimze", ReadOptimizedReport);
            }
            else
            {
                _ = packetTables.Resolve(lineage, "RoundScoreReport");
                _ = table.C2S("gcpGameUpdateScore");
                if (!killReports)
                    OnReport("gcpGameUpdateScore", ReadFullScoreReport);
            }
        }
        if (!killReports)
        {
            logger.LogInformation("dialect.rooms: {Protocol} has no audited kill-credit report layout; report routes stay unbound.", protocol);
            return;
        }
        if (play.ScoreReports || referenceReports)
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundKillCreditReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinishingBlowReported>>(this));

        _ = packetTables.Resolve(lineage, "RoundScoreReport");
        if (directReports || referenceReports)
            _ = table.C2S("gcpGameUpdateScore");
        OnReport("gcpGameUpdateScore", ReadFullScoreReport);
        if (optimizedScores)
        {
            _ = packetTables.Resolve(lineage, "RoundOptimizedScoreReport");
            OnReport("gcpGameUpdateScoreOptimze", ReadOptimizedReport);
        }
    }

    /// <summary>Separates native direct-kill reports from the build's independently admitted ordinary score events.</summary>
    internal IMessage? ReadFullScoreReport(ISession session, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoundScoreReport", body);
        var kind = Convert.ToInt32(fields["kind"]);
        if (Score.StarReport == StarReportPacket.Full && StarChangeOf(kind) is { } star)
            return new RoundStarReported(0, star);
        var direct = kind == 3 && packetTables.CapabilityOf(lineage, "direct_kill_reports");
        var ordinary = kind is 1 or 2 && packetTables.CapabilityOf(lineage, "character_reference_score_reports");
        if (direct || ordinary)
        {
            // kr-812 sub_51F780/sub_520A20 use victim then scorer references; sub_528550 returns side codes, not seats.
            var scorer = PlayerSeatOf(unchecked((int)fields["unknown_12"]));
            var victim = PlayerSeatOf(unchecked((int)fields["unknown_8"]));
            if (scorer < 0 || victim == scorer)
            {
                logger.LogWarning("Session {Id} score report kind {Kind} has no player scorer or names itself as victim; ignored.", session.Id, kind);
                return null;
            }
            if (kind == 1)
                return new RoundKillCreditReported(scorer, victim >= 0 ? victim : null, 0)
                    { RequiresReporterOwnership = true };
            return new RoundFinishingBlowReported(scorer)
                { Victim = victim >= 0 ? victim : null, HasVictimReference = true,
                    RequiresDirectKillMode = direct, RequiresReporterOwnership = true };
        }
        if (!play.ScoreReports)
        {
            logger.LogInformation("Session {Id} gcpGameUpdateScore kind {Kind} has no admitted score semantics on {Protocol}.",
                session.Id, fields["kind"], protocol);
            return null;
        }
        return Score.WideReport ? ReadWideReport(session, body) : ReadKillCredit(session, body);
    }

    /// <summary>
    /// The eight-u32 <c>gcpGameUpdateScore</c> (reader <c>cn-324 sub_45CBA0</c>, sender <c>cn-324 sub_467E00</c>):
    /// <c>kind, victim reference, reporter reference, victim seat, reporter seat, u32, u32, u32</c>. Kind 1 is
    /// the knock-down, kind 2 the finishing blow, and kind 3 the direct Super Pang in NPC modes
    /// (<c>cn-324 sub_476B90</c>, call at 0x47846E). The client's
    /// other kinds (7, 16 and 17 at the senders beside <c>cn-324 sub_467E00</c>) name no victim and are not scores.
    /// </summary>
    /// <param name="session">The reporting session.</param>
    /// <param name="body">The packet body.</param>
    /// <returns>The knock-down or finishing-blow report, or <see langword="null"/> for a body that is neither.</returns>
    internal IMessage? ReadWideReport(ISession session, ReadOnlySpan<byte> body)
    {
        IMessage? message;
        try
        {
            message = DecodeWideScoreReport(packetTables, lineage, body);
        }
        catch (PacketFormatException ex)
        {
            logger.LogWarning("Session {Id} gcpGameUpdateScore does not match the expected layout ({Reason}); ignored.",
                session.Id, ex.Message);
            return null;
        }
        if (message is RoundKillCreditReported { IsRescue: true } && !Score.RescueCredit)
            return null;
        if (message is null)
            logger.LogInformation("Session {Id} gcpGameUpdateScore kind {Kind} is not a score report.", session.Id, BitConverter.ToInt32(body));
        return message;
    }

    /// <summary>Decodes a complete eight-u32 score report without inferring a victim from earlier packets.</summary>
    internal static IMessage? DecodeWideScoreReport(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RoundScoreReport", body);
        var kind = (int)fields["kind"];
        var victimSeat = (int)fields["victim_slot"];
        var reporterSeat = (int)fields["reporter_slot"];
        switch (kind)
        {
            // cn-324 sub_4767C0 at 0x476B7A reports completed rescue credit with kind 4.
            case 4:
                return new RoundKillCreditReported(reporterSeat, victimSeat >= 0 ? victimSeat : null, 0) { IsRescue = true };
            case 1:
                return new RoundKillCreditReported(reporterSeat, victimSeat >= 0 ? victimSeat : null, (int)fields["combo"]);
            case 2:
            case 3:
                return new RoundFinishingBlowReported(reporterSeat)
                {
                    Victim = victimSeat >= 0 ? victimSeat : null,
                    HasVictimReference = true,
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// <c>gcpGameUpdateScore</c> (kr-812 writer <c>sub_530960</c>, reader <c>sub_5309D0</c>): <c>u8 kind, u32, u32, u8, u8, u8,
    /// u16, u16</c>. Only kind 1 is a score report - the knock-down credit; the other kinds this
    /// sender routes here do not establish knock-down credit and are logged without dispatching it.
    /// </summary>
    internal RoundKillCreditReported? ReadKillCredit(ISession session, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoundScoreReport", body);
        // Existing knock-down admission credits the authenticated sender; direct kills have a separate ownership check.
        var reporter = (byte)fields["reporter_slot"];
        if ((byte)fields["kind"] != 1)
        {
            logger.LogInformation("Session {Id} gcpGameUpdateScore kind {Kind} is not a knock-down report.",
                session.Id, fields["kind"]);
            return null;
        }
        var victim = (int)fields["unknown_8"];
        var side = unchecked((sbyte)(byte)fields["unknown_16"]);
        var combo = (ushort)fields["unknown_20"];
        // A negative side code marks a victim the reporting client simulates itself, so it is never
        // a seat of this round.
        return new RoundKillCreditReported(reporter, side < 0 ? null : SeatOf(victim), combo);
    }

    /// <summary>
    /// <c>gcpGameUpdateScoreOptimze</c> (kr-812 writer <c>sub_530900</c>, reader <c>sub_530930</c>): <c>u8 kind, u32, u8</c>. Only
    /// kind 2 is a score report - the finishing blow, which names no victim; the round pairs it
    /// with that reporter's last knock-down report.
    /// </summary>
    internal RoundFinishingBlowReported? ReadFinishingBlow(ISession session, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoundOptimizedScoreReport", body);
        if ((byte)fields["kind"] != 2)
        {
            logger.LogInformation("Session {Id} gcpGameUpdateScoreOptimze kind {Kind} is not a finishing blow.",
                session.Id, fields["kind"]);
            return null;
        }
        return new RoundFinishingBlowReported((byte)fields["reporter_slot"]);
    }

    /// <summary>
    /// Decodes the six-byte <c>gcpGameUpdateScoreOptimze</c>: kind 2 is the finishing blow where the build's kill
    /// reports are read, and on a build whose star counts travel here the star kinds are the client's own count
    /// changes (<see cref="StarChangeOf"/>). Other kinds are logged.
    /// </summary>
    internal IMessage? ReadOptimizedReport(ISession session, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoundOptimizedScoreReport", body);
        var kind = (byte)fields["kind"];
        if (kind == 2 && play.ScoreReports && !Score.WideReport)
            return ReadFinishingBlow(session, body);
        if (Score.StarReport == StarReportPacket.Optimized && StarChangeOf(kind) is { } star)
            return new RoundStarReported((byte)fields["reporter_slot"], star);
        logger.LogInformation("Session {Id} gcpGameUpdateScoreOptimze kind {Kind} is not a report this row reads.", session.Id, kind);
        return null;
    }

    /// <summary>
    /// The star count change a report kind names, from the build's <c>score</c> values (kr-812: 6 small star
    /// more, 15 big star more, 7 small star dropped, 16 big star dropped; cn-279 uses 14 and 15 for the big star),
    /// or <see langword="null"/> when the row reads no star reports or the kind is no star kind.
    /// </summary>
    private RoundStarChange? StarChangeOf(int kind)
    {
        if (!StarReports)
            return null;
        var score = Score;
        if (kind == score.StarSmallGained) return RoundStarChange.SmallGained;
        if (kind == score.StarBigGained) return RoundStarChange.BigGained;
        if (kind == score.StarSmallLost) return RoundStarChange.SmallLost;
        if (kind == score.StarBigLost) return RoundStarChange.BigLost;
        return null;
    }

    /// <summary>
    /// <c>gspUpdateScoreData</c> (reader <c>kr-812 sub_80A8B0</c>, element reader <c>kr-812 sub_80A840</c>):
    /// <c>u8 team, u16 total, i8 slotA, i8 slotB, u32 count + count x (u8 kind, u16 value), u32
    /// count + count x (u8 kind, u16 value)</c>. The first vector belongs to slot A, the second to
    /// slot B; the recorded ticks carry four kinds for the scorer and none for the victim.
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundScoreUpdated message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Score.LiveScorePacket)
            return session.SendAsync(S2C(session, "gspUpdateScoreData"), WriteScoreUpdate(packetTables, lineage, message), ct);
        // A build without the live score packet is sent its whole table again (handler cn-324 sub_6F21C0
        // replaces the table with each one).
        return message.Scoreboard is { } board
            ? session.SendAsync(S2C(session, "gspGameBroadcastPlayerScoreData"), WriteScoreboard(packetTables, lineage, board), ct)
            : ValueTask.CompletedTask;
    }

    /// <summary>Writes the <c>gspUpdateScoreData</c> body the recorded ticks pin.</summary>
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
            ["team"] = checked((byte)Math.Clamp(message.ScoringTeam, 1, 2)),
            ["total"] = checked((ushort)TeamTotal(message.TeamTotals, message.ScoringTeam)),
            ["scorer_slot"] = unchecked((byte)(scorer?.Slot ?? -1)),
            ["victim_slot"] = unchecked((byte)(victim?.Slot ?? -1)),
            ["scorer_stats"] = StatValues(scorer),
            ["victim_stats"] = StatValues(victim),
        });
    }

    // The live score carries these four absolute counters in this order.
    private static IReadOnlyDictionary<string, object?>[] StatValues(RoundSeatStats? seat) => seat is null
        ? []
        : [StatValue(StatScore, seat.Score), StatValue(StatKnockdowns, seat.Knockdowns),
           StatValue(StatKills, seat.Kills), StatValue(StatDeaths, seat.Deaths),
           .. seat.Stars is { } stars ? [StatValue(StatStars, stars)] : Array.Empty<IReadOnlyDictionary<string, object?>>()];

    private static IReadOnlyDictionary<string, object?> StatValue(byte kind, int value) =>
        new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["value"] = checked((ushort)Math.Clamp(value, 0, ushort.MaxValue)),
        };

    private static int TeamTotal(IReadOnlyList<int> totals, int team) =>
        team >= 1 && team <= totals.Count ? totals[team - 1] : 0;

    /// <summary>
    /// The outcome word the S2C 705 handler <c>sub_803DF0</c> reads: 3 sets the win flag and 1 or 2
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

    /// <summary>
    /// <c>gspGameShowResult_</c> (handler <c>kr-812 sub_5AD030</c>): the empty packet that opens the result
    /// scene and starts the reward stage's refresh. It is inert until a non-zero outcome word has
    /// been written, so the dialect only sends it for the reward stage.
    /// </summary>
    public ValueTask SendAsync(ISession session, ResultStageAdvanced message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Stage == ResultStage.Reward
            ? session.SendAsync(S2C(session, "gspGameShowResult_"), ReadOnlyMemory<byte>.Empty, ct)
            : ValueTask.CompletedTask;
    }

    /// <summary>
    /// Writes <c>gspGameBroadcastPlayerScoreData</c> (member reader <c>kr-812 sub_417720</c>, handler
    /// <c>kr-812 sub_804620</c>): the team-row count, two empty vectors, the leading team, one flag,
    /// the team rows, sixteen seat rows and the per-slot tail pairs. The seat row is the build's
    /// <c>ScoreRow</c> table of <c>packets/score.yaml</c>. The client replaces its whole table with
    /// each one, so both passes carry every row.
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundScoreboard message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = WriteScoreboard(packetTables, lineage, message);
        if (Score.TimeOverEnd == TimeOverEndBody.Scoreboard && !message.WithRewards)
        {
            // This build takes the table of the result scene inside gspGameTimeOverEnd, whatever ended the round.
            return session.SendAsync(S2C(session, "gspGameTimeOverEnd"), body, ct);
        }
        if (!message.TimeOver)
            return session.SendAsync(S2C(session, "gspGameBroadcastPlayerScoreData"), body, ct);
        // A time-limit end is announced with each team's total and no scoreboard: the body is
        // 0B 01 for a round that ended 11 to 1.
        return session.SendAsync(S2C(session, "gspGameTimeOverEnd"), WriteTimeOverTotals(packetTables, lineage, message.TeamTotals), ct);
    }

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
        var rows = Enumerable.Range(0, RunCount("rows"))
            .Select(slot => ScoreRowValues(rowFields, FindRow(message, slot))).ToArray();
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
        PacketCodec.WriteResolvedFields(new PacketWriter(buffer), tables, build, "ScoreRow", fields, ScoreRowValues(fields, row));
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
        IReadOnlyList<PacketField> fields, RoundScoreboardRow? row)
    {
        var values = new Dictionary<string, object?>(fields.Count, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            values[field.Name] = field.Type switch
            {
                "wstr" => row is not null && field.Name == "nickname" ? row.Nickname : string.Empty,
                "list<wstr>" => row?.Reward is { } reward && field.Name == "badge_images" ? reward.BonusBadges : Array.Empty<string>(),
                _ => ScoreRowNumber(field, row),
            };
        }
        return values;
    }

    private static long ScoreRowNumber(PacketField field, RoundScoreboardRow? row)
    {
        long? supplied = row is null ? null : field.Name switch
        {
            "award_display_id" => row.AwardDisplayId,
            "slot" => row.Slot,
            "team" => Math.Max(1, row.Team),
            "level" => Math.Max(1, row.Level),
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

    /// <summary>
    /// The seat a client reference names: a client builds its entity references as
    /// <c>1000000 + 100000 * seat</c>, so the seat is the quotient. A reference below the base
    /// names no seat.
    /// </summary>
    /// <param name="serial">The value the client sent.</param>
    /// <returns>The seat index, which the round checks against its own roster.</returns>
    internal static int SeatOf(int serial) => serial < SeatSerialBase
        ? -1
        : (serial - SeatSerialBase) / SeatSerialStride;

    // cn-324 sub_460750 resets per-seat counters; sub_4632B0 creates the player before later owned objects.
    // The quotient identifies an object's owner, not its kind: NPCs and damagers share that owner's range.
    internal static int PlayerSeatOf(int serial) => serial < SeatSerialBase
        || (serial - SeatSerialBase) % SeatSerialStride != 0 ? -1 : SeatOf(serial);
}
