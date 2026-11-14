using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Binds the star modes' packets: the drop request (C2S <c>gcpReqCreateStar</c>), the star creation
/// (<c>gspGameCreateStar</c>), the claim (<c>gcpEatStar</c>) and its confirmation (<c>gspStarEaten</c>). Every
/// build reads and writes them with the Lucci packets' own serializers (kr-812 sub_530870 writes both create
/// requests, sub_5302A0 reads both creations), so the layouts are the <c>packets/lucci-drops.yaml</c> structures
/// and a build differs only in its opcode numbers. The map's star points come from the host's map report
/// (<c>gcpGameReportMapData</c>), which <see cref="SupplyBinding"/> reads when it is mounted. A build whose table
/// has the empty star fever notice also gets the fever sender.
/// </summary>
internal sealed class StarBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundStarDropRequested>, IInboundMessageBinding<RoundStarPickupRequested>,
      IInboundMessageBinding<RoundStarPointsReported>, IInboundMessageBinding<RoundStarReported>,
      IMessageSender<RoundStarCreated>, IMessageSender<RoundStarPicked>, IMessageSender<RoundStarFeverStarted>
{
    // kr-1256 sub_6D55D0 case 634 hands the empty body to sub_6ECD60, which starts the star fever in modes 5 and 6.
    private const string FeverOpcode = "gspGameBigDropEventTick_";

    /// <inheritdoc />
    public string Protocol => protocol;

    /// <summary>
    /// Gets the element limit for reading the host's map report here; zero, the default, leaves the report to
    /// <see cref="SupplyBinding"/>, which passes its star points on.
    /// </summary>
    internal int MapReportLimit { get; init; }

    /// <summary>
    /// Gets whether this binding reads the clients' own star counts from <c>gcpGameUpdateScoreOptimze</c> itself. The
    /// modern rooms set it, since their round bindings have no score report codec to read them.
    /// </summary>
    internal bool ReadsStarReports { get; init; }

    /// <summary>Mounts the packets; a build without the audited capability or opcodes is a configuration error.</summary>
    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "star_items"))
            throw new ConfigException("star_items requires audited star create, claim and count consumers.");
        // The drop request is read by LucciBinding, so only its presence is checked here. The three structures this
        // binding reads and writes are checked field by field with the exact types it casts and sizes them as: the two
        // claims cast both words to int and the creation sizes nine fields, two words and seven scalars.
        _ = tables.Resolve(build, "LucciDropRequest");
        PacketLayoutContract.RequireFields(tables, build, "LucciPickup", "Star items",
            ("character_id", "u32"), ("object_id", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "LucciPicked", "Star items",
            ("character_id", "u32"), ("object_id", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "LucciCreate", "Star items",
            ("object_id", "u32"), ("kind", "u32"), ("x", "f32"), ("y", "f32"), ("z", "f32"),
            ("velocity_x", "f32"), ("velocity_y", "f32"), ("velocity_z", "f32"), ("unknown_36", "u32"));
        // A report layout that names the star vectors must give them the types ReadStarPoints casts.
        if (tables.Resolve(build, "SupplyMapReport").Any(field => field.Name == "star_kinds"))
            PacketLayoutContract.RequireFields(tables, build, "SupplyMapReport", "Star items",
                ("star_kinds", "list<u32>"), ("star_positions", "list<SupplyPosition>"),
                ("star_period_ms", "list<u32>"), ("star_first_ms", "list<u32>"));
        // Resolve every opcode first so a missing row fails the mount instead of the first drop.
        _ = table.C2S("gcpReqCreateStar");
        _ = table.C2S("gcpEatStar");
        _ = table.S2C("gspGameCreateStar");
        _ = table.S2C("gspStarEaten");
        PlayValues.ScoreValues? score = null;
        if (ReadsStarReports)
        {
            _ = HasNamedStarReporter(tables, build);
            score = PlayValues.Of(tables, build).Score;
            if (score.StarReport != PlayValues.StarReportPacket.Optimized)
                throw new ConfigException("Modern star items read star counts only from gcpGameUpdateScoreOptimze (score.star_report: Optimized).");
            PacketLayoutContract.RequireFields(tables, build, "RoundOptimizedScoreReport", "Star reports",
                ("kind", "u8"), ("unknown_8", "u32"), ("reporter_slot", "u8"));
            _ = table.C2S("gcpGameUpdateScoreOptimze");
        }
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStarDropRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStarPickupRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStarPointsReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarCreated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarPicked>>(this));
        if (table.Find(PacketDirection.ServerToClient, FeverOpcode) is not null)
            context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarFeverStarted>>(this));
        context.Effect(router.Register(protocol, table.C2S("gcpReqCreateStar"), async (session, packet, ct) =>
        {
            var drop = LucciBinding.ReadDrop(tables, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, new RoundStarDropRequested(drop.Point, drop.Kind, drop.Tag), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Star drops have no typed handler.");
        }));
        context.Effect(router.Register(protocol, table.C2S("gcpEatStar"), async (session, packet, ct) =>
        {
            var values = PacketCodec.ReadScalars(tables, build, "LucciPickup", packet.Payload.Span);
            var request = new RoundStarPickupRequested((int)values["character_id"], (int)values["object_id"]);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Star claims have no typed handler.");
        }));
        if (MapReportLimit > 0)
            context.Effect(router.Register(protocol, table.C2S("gcpGameReportMapData"), async (session, packet, ct) =>
            {
                var values = SupplyBinding.ReadReportValues(tables, build, packet.Payload.Span, MapReportLimit);
                if (ReadStarPoints(values, packet.Payload.Length) is { Points.Count: > 0 } stars
                    && !await messages.DispatchAsync(protocol, session, stars, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Map stars have no typed handler.");
            }));
        if (score is not null)
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStarReported>>(this));
            context.Effect(router.Register(protocol, table.C2S("gcpGameUpdateScoreOptimze"), async (session, packet, ct) =>
            {
                if (ReadStarReport(tables, build, score, packet.Payload.Span) is { } report
                    && !await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Star reports have no typed handler.");
            }));
        }
    }

    /// <summary>
    /// Reads a six-byte <c>gcpGameUpdateScoreOptimze</c> as the reporter's own star count change, by the build's
    /// <c>score</c> kinds, or returns null for a kind that names none.
    /// </summary>
    internal static RoundStarReported? ReadStarReport(PacketTables tables, string build, PlayValues.ScoreValues score,
        ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RoundOptimizedScoreReport", body);
        var kind = (byte)fields["kind"]!;
        var change = kind == score.StarSmallGained ? RoundStarChange.SmallGained
            : kind == score.StarBigGained ? RoundStarChange.BigGained
            : kind == score.StarSmallLost ? RoundStarChange.SmallLost
            : kind == score.StarBigLost ? RoundStarChange.BigLost
            : (RoundStarChange?)null;
        var slot = (byte)fields["reporter_slot"]!;
        return change is { } star ? new RoundStarReported(slot, star)
        {
            NamedReporterSlot = HasNamedStarReporter(tables, build) ? slot : null,
        } : null;
    }

    /// <summary>Admits a report's affected-seat identity only for builds whose native sender paths were audited.</summary>
    internal static bool HasNamedStarReporter(PacketTables tables, string build)
    {
        if (!tables.ValuesOf(build).TryGetValue("star_report_slot_builds", out var raw)) return false;
        if (raw is not string[] builds || builds.Length == 0 || builds.Distinct(StringComparer.Ordinal).Count() != builds.Length
            || builds.Any(candidate => !tables.Builds.Contains(candidate, StringComparer.Ordinal)))
            throw new ConfigException("star_report_slot_builds must list distinct known client builds.");
        if (!builds.Contains(build, StringComparer.Ordinal)) return false;
        // kr-1256 sub_9E5210 reports losses for a departing seat; kr-1256 sub_708660 preserves its seat byte.
        PacketLayoutContract.RequireFields(tables, build, "RoundOptimizedScoreReport", "Named star reporter",
            ("reporter_slot", "u8"));
        return true;
    }

    /// <summary>
    /// Reads the star points of a parsed map report, or returns null for a layout that names no star vectors. The host
    /// lists only the helpers kr-812 sub_75CFF0 names "star" (1) or "bigStar" (2), so an entry of another kind is skipped.
    /// </summary>
    internal static RoundStarPointsReported? ReadStarPoints(Dictionary<string, object?> values, int bodyLength)
    {
        if (!values.TryGetValue("star_kinds", out var kindVector))
            return null;
        var kinds = (object?[])kindVector!;
        var positions = (object?[])values["star_positions"]!;
        var periods = (object?[])values["star_period_ms"]!;
        var firsts = (object?[])values["star_first_ms"]!;
        if (positions.Length != kinds.Length || periods.Length != kinds.Length || firsts.Length != kinds.Length)
            throw new PacketFormatException("Star point vectors have different lengths.", bodyLength);
        var points = new List<RoundStarPoint>(kinds.Length);
        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = (int)kinds[i]!;
            if (kind is not (1 or 2))
                continue;
            var position = (Dictionary<string, object?>)positions[i]!;
            points.Add(new RoundStarPoint((float)position["x"]!, (float)position["y"]!, (float)position["z"]!, kind,
                (int)firsts[i]!, (int)periods[i]!));
        }
        return new RoundStarPointsReported(points);
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, RoundStarCreated message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspGameCreateStar"), PacketCodec.Write(tables, build, "LucciCreate",
            new Dictionary<string, object?>
            {
                ["object_id"] = message.ObjectId, ["kind"] = message.Kind,
                ["x"] = message.Point.X, ["y"] = message.Point.Y, ["z"] = message.Point.Z,
                ["velocity_x"] = message.Point.VelocityX, ["velocity_y"] = message.Point.VelocityY,
                ["velocity_z"] = message.Point.VelocityZ, ["unknown_36"] = message.Tag,
            }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, RoundStarPicked message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspStarEaten"), PacketCodec.Write(tables, build, "LucciPicked",
            new Dictionary<string, object?> { ["character_id"] = message.CharacterObjectId, ["object_id"] = message.ObjectId }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, RoundStarFeverStarted message, CancellationToken ct) =>
        session.SendAsync(table.S2C(FeverOpcode), ReadOnlyMemory<byte>.Empty, ct);
}
