using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Admits the audited native knock-down, finishing-blow and star reports into shared round messages.
/// Result encoders are published separately; player references remain subject to room ownership checks.
/// </summary>
internal sealed partial class PlayRoomCodec
{
    // cn-324 sub_4632B0 creates the player at the start of each per-seat entity range.
    private const int SeatSerialBase = 1_000_000;
    private const int SeatSerialStride = 100_000;

    private PlayValues.ScoreValues Score => play.Score;

    private CaptainScoreCodec? _captainScore;

    private void MountScoreReports(IPluginContext context)
    {
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var table = tables.ForProtocol(protocol);
        if (CaptainKills)
            _captainScore = new CaptainScoreCodec(packetTables, lineage);
        var tutorialScores = TutorialScoresSupported();
        if (tutorialScores)
        {
            _ = table.C2S("gcpGameUpdateScore");
            _ = table.S2C("gspGameBroadcastPlayerScoreData");
            PacketLayoutContract.RequireFields(packetTables, lineage, "ScoreRow", "Selected tutorial score",
                ("knock_downs", "u32"), ("kills", "u32"));
            _ = TutorialHelpField(packetTables, lineage);
            _ = TutorialNpcFinishFields(packetTables, lineage);
            PacketLayoutContract.RequireFields(packetTables, lineage, "RoundScoreReport", "Selected tutorial report",
                ("unknown_12", "u32"));
            if (packetTables.Resolve(lineage, "RoundScoreReport").Single(field => field.Name == "kind").Type == "u8")
            {
                _ = table.C2S("gcpGameUpdateScoreOptimze");
                PacketLayoutContract.RequireFields(packetTables, lineage, "RoundOptimizedScoreReport", "Selected tutorial finishing report",
                    ("kind", "u8"), ("unknown_8", "u32"), ("reporter_slot", "u8"));
            }
            context.Effect(services.RegisterMany<IInboundMessageBinding<TutorialScoreReported>>(this));
            context.Effect(services.RegisterMany<IMessageSender<TutorialScoreUpdated>>(this));
        }
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
            context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(_resultSender));
            context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(_resultSender));
            context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(_resultSender));
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
                    if (tutorialScores && name == "gcpGameUpdateScore" && ReadTutorialScore(packetTables, lineage, packet.Payload.Span) is { } progress)
                        await messages.DispatchAsync(protocol, session, progress, ct).ConfigureAwait(false);
                    if (tutorialScores && name == "gcpGameUpdateScoreOptimze"
                        && ReadOptimizedTutorialScore(packetTables, lineage, packet.Payload.Span) is { } compactProgress)
                        await messages.DispatchAsync(protocol, session, compactProgress, ct).ConfigureAwait(false);
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
        var killReports = play.ScoreReports || directReports || referenceReports || CaptainKills;
        if (StarReports)
        {
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundStarReported>>(this));
            if (Score.StarReport == PlayValues.StarReportPacket.Optimized)
            {
                PacketLayoutContract.RequireFields(packetTables, lineage, "RoundOptimizedScoreReport", "Native score reports",
                    ("kind", "u8"), ("unknown_8", "u32"), ("reporter_slot", "u8"));
                _ = table.C2S("gcpGameUpdateScoreOptimze");
                if (!optimizedScores)
                    OnReport("gcpGameUpdateScoreOptimze", ReadOptimizedReport);
            }
            else
            {
                PacketLayoutContract.RequireFields(packetTables, lineage, "RoundScoreReport", "Native score reports",
                    ("unknown_8", "u32"), ("unknown_12", "u32"));
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

        PacketLayoutContract.RequireFields(packetTables, lineage, "RoundScoreReport", "Native score reports",
            ("unknown_8", "u32"), ("unknown_12", "u32"));
        if (play.ScoreReports && Score.WideReport)
            PacketLayoutContract.RequireFields(packetTables, lineage, "RoundScoreReport", "Native wide score reports",
                ("kind", "u32"), ("victim_slot", "u32"), ("reporter_slot", "u32"), ("combo", "u32"));
        else if (play.ScoreReports)
            PacketLayoutContract.RequireFields(packetTables, lineage, "RoundScoreReport", "Native narrow score reports",
                ("kind", "u8"), ("reporter_slot", "u8"), ("unknown_16", "u8"), ("unknown_20", "u16"));
        if (directReports || referenceReports)
            _ = table.C2S("gcpGameUpdateScore");
        OnReport("gcpGameUpdateScore", ReadFullScoreReport);
        if (optimizedScores)
        {
            PacketLayoutContract.RequireFields(packetTables, lineage, "RoundOptimizedScoreReport", "Native score reports",
                ("kind", "u8"), ("unknown_8", "u32"), ("reporter_slot", "u8"));
            OnReport("gcpGameUpdateScoreOptimze", ReadOptimizedReport);
        }
    }

    /// <summary>Separates native direct-kill reports from the build's independently admitted ordinary score events.</summary>
    internal IMessage? ReadFullScoreReport(ISession session, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packetTables, lineage, "RoundScoreReport", body);
        var kind = Convert.ToInt32(fields["kind"]);
        if (Score.StarReport == PlayValues.StarReportPacket.Full && StarChangeOf(kind) is { } star)
            return new RoundStarReported(0, star);
        if (CaptainKills && Score.CaptainKill > 0 && kind == Score.CaptainKill)
            return ReadCaptainKill(session, fields);
        var direct = kind == 3 && packetTables.CapabilityOf(lineage, "direct_kill_reports");
        var ordinary = kind is 1 or 2 && packetTables.CapabilityOf(lineage, "character_reference_score_reports");
        if (direct || ordinary)
        {
            // kr-812 sub_51F780 / kr-812 sub_520A20 use victim then scorer references.
            // kr-812 sub_528550 returns side codes, not seats.
            var scorer = PlayerSeatOf(unchecked((int)fields["unknown_12"]));
            // cn-279 sub_773950's NPC knockdown path emits kind 2 with -1 at unknown_16 and the
            // already-computed reporter slot at npc_scorer_slot (offset 20), unlike its character-reference path.
            if (kind == 2 && (int)fields["unknown_8"] == 0 && (int)fields["unknown_12"] == 0
                && (int)fields["unknown_16"] == -1 && fields.TryGetValue("npc_scorer_slot", out var npcScorer))
            {
                var slot = Convert.ToInt32(npcScorer);
                scorer = slot is >= 0 and < 64 ? slot : -1;
            }
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
    /// The Captain kill (<c>kr-812 sub_430640</c>): the fallen Captain's and the killer's character words at +8 and
    /// +12 and their seats at +16 and +17. Every member but the Captain's own client sends it, so the report names
    /// the killer as its scorer and the round credits only the killer's own copy.
    /// </summary>
    private RoundFinishingBlowReported? ReadCaptainKill(ISession session, IReadOnlyDictionary<string, object> fields)
    {
        var result = (_captainScore ?? throw new InvalidOperationException("Captain score codec has not been mounted.")).Read(fields);
        if (result is null)
            logger.LogWarning("Session {Id} Captain kill names no consistent player pair; ignored.", session.Id);
        return result;
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
    /// <c>gcpGameUpdateScore</c> (writer <c>kr-812 sub_530960</c>, reader <c>kr-812 sub_5309D0</c>): <c>u8 kind, u32, u32, u8, u8, u8,
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
    /// <c>gcpGameUpdateScoreOptimze</c> (writer <c>kr-812 sub_530900</c>, reader <c>kr-812 sub_530930</c>): <c>u8 kind, u32, u8</c>. Only
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
        if (Score.StarReport == PlayValues.StarReportPacket.Optimized && StarChangeOf(kind) is { } star)
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
    /// The seat a client reference names: a client builds its entity references as
    /// <c>1000000 + 100000 * seat</c>, so the seat is the quotient. A reference below the base
    /// names no seat.
    /// </summary>
    /// <param name="serial">The value the client sent.</param>
    /// <returns>The seat index, which the round checks against its own roster.</returns>
    internal static int SeatOf(int serial) => serial < SeatSerialBase
        ? -1
        : (serial - SeatSerialBase) / SeatSerialStride;

    // cn-324 sub_460750 resets per-seat counters; cn-324 sub_4632B0 creates the player before later owned objects.
    // The quotient identifies an object's owner, not its kind: NPCs and damagers share that owner's range.
    internal static int PlayerSeatOf(int serial) => serial < SeatSerialBase
        || (serial - SeatSerialBase) % SeatSerialStride != 0 ? -1 : SeatOf(serial);
}
