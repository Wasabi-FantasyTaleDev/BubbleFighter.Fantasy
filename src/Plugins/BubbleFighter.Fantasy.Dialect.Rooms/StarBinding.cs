using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Binds the star modes' packets: the drop request (C2S <c>gcpReqCreateStar</c>), the star creation
/// (<c>gspGameCreateStar</c>), the claim (<c>gcpEatStar</c>), its confirmation (<c>gspStarEaten</c>) and,
/// where the build states it, the empty Fever/Hurry notification. Every build reads and writes the item
/// bodies with the Lucci packets' own field order (kr-812 sub_530870 writes both create requests,
/// sub_5302A0 reads both creations), so a build differs only in its opcode numbers. The modern room path
/// also binds the full score report that carries a client's own star-count changes.
/// </summary>
internal sealed class StarBinding(string protocol, IOpcodeTable table, PacketTables tables, string build,
    bool bindScoreReports = false)
    : IInboundMessageBinding<RoundStarDropRequested>, IInboundMessageBinding<RoundStarPickupRequested>,
      IInboundMessageBinding<RoundStarReported>, IMessageSender<RoundStarCreated>, IMessageSender<RoundStarPicked>,
      IMessageSender<RoundStarFeverStarted>
{
    /// <summary>The build's score-report values when this binding owns the modern full report.</summary>
    private PlayValues.ScoreValues? _score;

    /// <inheritdoc />
    public string Protocol => protocol;

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
        // Resolve every opcode first so a missing row fails the mount instead of the first drop.
        _ = table.C2S("gcpReqCreateStar");
        _ = table.C2S("gcpEatStar");
        _ = table.S2C("gspGameCreateStar");
        _ = table.S2C("gspStarEaten");
        var fever = table.Find(PacketDirection.ServerToClient, "gspGameFeverStart_");
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStarDropRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStarPickupRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarCreated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarPicked>>(this));
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
        if (fever is not null)
            context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarFeverStarted>>(this));
        if (!bindScoreReports)
            return;
        // kr-1256 reports star gains in the six-byte opcode 2029 body: kind 21 for a small star and
        // kind 24 for a big star. Select the layout and opcode from the same profile values.
        var score = PlayValues.Of(tables, build).Score;
        _score = score;
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStarReported>>(this));
        var (layout, opcodeName) = score.StarReport switch
        {
            PlayValues.StarReportPacket.Optimized => ("RoundOptimizedScoreReport", "gcpGameUpdateScoreOptimze"),
            PlayValues.StarReportPacket.Full => ("RoundScoreReport", "gcpGameUpdateScore"),
            _ => throw new ConfigException($"Unsupported star report layout {score.StarReport}."),
        };
        _ = tables.Resolve(build, layout);
        var scoreOpcode = table.C2S(opcodeName);
        context.Effect(router.Register(protocol, scoreOpcode, async (session, packet, ct) =>
        {
            if (ReadStarReport(packet.Payload.Span, layout) is not { } report)
                return;
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Star count reports have no typed handler.");
        }));
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
        session.SendAsync(table.S2C("gspGameFeverStart_"), ReadOnlyMemory<byte>.Empty, ct);

    private RoundStarReported? ReadStarReport(ReadOnlySpan<byte> body, string layout)
    {
        var values = PacketCodec.ReadScalars(tables, build, layout, body);
        var kind = (int)(byte)values["kind"];
        if (kind == 0)
            return null;
        var score = _score ?? throw new InvalidOperationException("Star score reports are not mounted.");
        RoundStarChange? change = kind == score.StarSmallGained ? RoundStarChange.SmallGained
            : kind == score.StarBigGained ? RoundStarChange.BigGained
            : kind == score.StarSmallLost ? RoundStarChange.SmallLost
            : kind == score.StarBigLost ? RoundStarChange.BigLost
            : null;
        return change is { } value ? new RoundStarReported((byte)values["reporter_slot"], value) : null;
    }
}
