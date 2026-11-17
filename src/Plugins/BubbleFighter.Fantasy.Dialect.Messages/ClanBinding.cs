using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The clan game-connection packets (layouts in <c>packets/clan.yaml</c>, numbers from the build's opcode table by
/// name): the three empty client requests in; the passport, mark list, guild info and the attendance state out. The
/// two open snapshots share one typed message so their order stays beside the bytes the client reads
/// (kr-1256 sub_99CFA0, kr-1256 sub_9B2690; attendance reader kr-1256 sub_10C5590); the passport answers the
/// passport request alone, because the client forwards it as a clan login (kr-1256 sub_9D2FF0 case 21).
/// </summary>
internal sealed class ClanBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<ClanServerAckReceived>, IInboundMessageBinding<ClanMainOpened>,
      IInboundMessageBinding<ClanAttendanceCheckedIn>, IMessageSender<ClanOpened>,
      IMessageSender<ClanAttendanceStateUpdated>, IMessageSender<ClanPassportIssued>
{
    /// <summary>
    /// The C2S names the binding reads. Each is optional: not every build has the passport sender, and kr-812 has neither
    /// the clan-main open (kr-1256 sub_5A9410) nor the attendance check-in (kr-1256 sub_57F010).
    /// </summary>
    internal static readonly string[] Requests = ["gcpReqClanPassport", "gcpReqClanMainOpen_", "gcpReqClanAttendanceCheckIn_"];

    /// <summary>
    /// The S2C names the binding writes: the passport, and the two open snapshots in the client's order. The snapshots
    /// answer the clan-main open only, so a build without <c>gcpReqClanMainOpen_</c> (kr-812 and the builds based on it) needs
    /// neither; the attendance state (<c>gspClanAttendanceState</c>) likewise exists only with the check-in.
    /// </summary>
    internal static readonly string[] Replies =
    [
        "gspClanPassport", "gspReqClanMarkInfo", "gspResGuildInfo",
    ];

    /// <summary>
    /// The empty body each structure may write; the lengths are the client readers' own: the passport 20
    /// (kr-1256 sub_99C1D0, whose leading word the raw .text bytes pin before the name; kr-812 sub_844AB0), mark list 8
    /// (kr-1256 sub_99CFA0, one table) or 12 (kr-812 sub_844FB0, two tables), guild info 16 (kr-1256 sub_9B2690,
    /// kr-812 sub_8407F0) and attendance state 14 (kr-1256 sub_10C5590 list, then kr-1256 sub_113F1F0 4+4+2) or 0 where the build has no attendance packet.
    /// A layout that matches none is a configuration error, not a client surprise.
    /// </summary>
    private static readonly (string Structure, int[] Lengths)[] EmptyBodies =
    [
        ("ClanPassport", [20]), ("ClanMarkInfo", [8, 12]), ("ClanGuildInfo", [16]), ("ClanAttendanceState", [0, 14]),
    ];

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        ValidateLayouts();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanServerAckReceived>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanMainOpened>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanAttendanceCheckedIn>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanOpened>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanAttendanceStateUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanPassportIssued>>(this));
        var router = context.Services.Get<IPacketRouter>();
        // The passport request is per build and absent where the client has no clan connection.
        if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqClanPassport") is not null)
            Route<ClanServerAckReceived>(context, router, messages, "gcpReqClanPassport");
        // The clan-main open and the attendance check-in exist only where the build has them (not kr-812).
        if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqClanMainOpen_") is not null)
            Route<ClanMainOpened>(context, router, messages, "gcpReqClanMainOpen_");
        if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqClanAttendanceCheckIn_") is not null)
            Route<ClanAttendanceCheckedIn>(context, router, messages, "gcpReqClanAttendanceCheckIn_");
    }

    public async ValueTask SendAsync(ISession session, ClanOpened message, CancellationToken ct)
    {
        await session.SendAsync(opcodes.S2C("gspReqClanMarkInfo"), WriteMarkInfo(tables, build), ct).ConfigureAwait(false);
        await session.SendAsync(opcodes.S2C("gspResGuildInfo"), WriteGuildInfo(tables, build), ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, ClanAttendanceStateUpdated message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspClanAttendanceState"), WriteAttendanceState(tables, build), ct);

    public ValueTask SendAsync(ISession session, ClanPassportIssued message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspClanPassport"), WritePassport(tables, build, message), ct);

    /// <summary>
    /// The <c>gspClanPassport</c> body the client echoes as its clan login (reader kr-1256 sub_99C1D0, forwarded
    /// by kr-1256 sub_9D2FF0 case 21): the account id, the nickname and the ticket; the two words between are
    /// echoed unused and keep the table default.
    /// </summary>
    internal static byte[] WritePassport(PacketTables tables, string build, ClanPassportIssued message) =>
        PacketCodec.Write(tables, build, "ClanPassport", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user_number"] = message.UserNumber,
            ["nickname"] = message.Nickname,
            ["ticket"] = message.Ticket.ToArray().Select(value => (object?)value).ToArray(),
        });

    /// <summary>The empty <c>gspClanPassport</c> body (S2C 21; reader kr-1256 sub_99C1D0).</summary>
    internal static byte[] WritePassport(PacketTables tables, string build) => Write(tables, build, "ClanPassport");

    /// <summary>The empty <c>gspReqClanMarkInfo</c> body (S2C 140; parser kr-1256 sub_99CFA0).</summary>
    internal static byte[] WriteMarkInfo(PacketTables tables, string build) => Write(tables, build, "ClanMarkInfo");

    /// <summary>The empty <c>gspResGuildInfo</c> body (S2C 918; handler kr-1256 sub_9B2690).</summary>
    internal static byte[] WriteGuildInfo(PacketTables tables, string build) => Write(tables, build, "ClanGuildInfo");

    /// <summary>The empty <c>gspClanAttendanceState</c> body (S2C 1508; list reader kr-1256 sub_10C5590).</summary>
    internal static byte[] WriteAttendanceState(PacketTables tables, string build) => Write(tables, build, "ClanAttendanceState");

    private static byte[] Write(PacketTables tables, string build, string structure) =>
        PacketCodec.Write(tables, build, structure, new Dictionary<string, object?>(StringComparer.Ordinal));

    private void Route<T>(IPluginContext context, IPacketRouter router, IMessageRouter messages, string name)
        where T : IMessage =>
        context.Effect(router.Register(protocol, opcodes.C2S(name), async (session, packet, ct) =>
        {
            // The client stamps these frames without a body (the request structures in packets/clan.yaml are empty);
            // anything else is not a client builder and must not be repaired into a clan open.
            if (!packet.Payload.Span.IsEmpty)
                throw new PacketFormatException($"{name} must be empty.", 0);
            var message = (T)Activator.CreateInstance(typeof(T))!;
            if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"{name} has no typed clan handler.");
        }));

    private void ValidateLayouts()
    {
        // The three routed requests are zero-field structures: the clients stamp the opcode on an empty packet.
        PacketLayoutContract.RequireEmpty(tables, build, "ClanPassportRequest", "Clan");
        PacketLayoutContract.RequireEmpty(tables, build, "ClanMainOpenRequest", "Clan");
        PacketLayoutContract.RequireEmpty(tables, build, "ClanAttendanceCheckInRequest", "Clan");
        PacketLayoutContract.RequireFields(tables, build, "ClanPassport", "Clan",
            ("user_number", "u32"), ("nickname", "wstr"), ("ticket", "list<u8>"));
        // The four game-port bodies are checked as defaults-only writes: the binding supplies no field value, the codec lifts the
        // table defaults, and the structure states the fields the client reads (their names and types differ between
        // the baseline and the @kr-1256 restatements). RequireEmpty would reject a correct table and RequireFields
        // cannot name build-varying fields, so the checked property is the byte length of the empty write. There is
        // no consumed scalar field to retype, so this binding has no wrong-type rejection test.
        foreach (var (structure, lengths) in EmptyBodies)
        {
            var body = Write(tables, build, structure);
            if (!lengths.Contains(body.Length))
                throw new ConfigException(
                    $"Invalid {structure} layout for '{build}': the empty body is {body.Length} bytes, the client reads {string.Join(" or ", lengths)}.");
        }
        _ = opcodes.S2C("gspClanPassport");
        if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqClanMainOpen_") is not null)
        {
            _ = opcodes.S2C("gspReqClanMarkInfo");
            _ = opcodes.S2C("gspResGuildInfo");
        }
        // The attendance check-in and its state come together: a build has both rows or neither.
        var attendance = opcodes.Find(PacketDirection.ClientToServer, "gcpReqClanAttendanceCheckIn_") is not null;
        if (attendance != opcodes.Find(PacketDirection.ServerToClient, "gspClanAttendanceState") is not null)
            throw new ConfigException($"Opcode table '{opcodes.Profile.Id}' must state both gcpReqClanAttendanceCheckIn_ and gspClanAttendanceState or neither.");
    }
}
