using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The clan game-connection packets (layouts in <c>packets/clan.yaml</c>, numbers from the build's opcode table by
/// name): the three empty client requests in; the passport, mark list, guild info and the attendance state out. The
/// three open snapshots share one typed message so their order stays beside the bytes the client reads
/// (kr-1256 sub_99C1D0, kr-1256 sub_99CFA0, kr-1256 sub_9B2690; attendance reader kr-1256 sub_10C5590).
/// </summary>
internal sealed class ClanBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<ClanServerAckReceived>, IInboundMessageBinding<ClanMainOpened>,
      IInboundMessageBinding<ClanAttendanceCheckedIn>, IMessageSender<ClanOpened>,
      IMessageSender<ClanAttendanceStateUpdated>
{
    /// <summary>The C2S names the binding reads; <c>Unknown25</c> is optional because not every build has the sender.</summary>
    internal static readonly string[] Requests = ["Unknown25", "Unknown2040", "Unknown2041"];

    /// <summary>The S2C names the binding writes, in the order the client consumes the three open snapshots.</summary>
    internal static readonly string[] Replies =
    [
        "gspClanPassport", "gspReqClanMarkInfo", "gspResGuildInfo", "gspClanAttendanceState",
    ];

    /// <summary>
    /// The empty body each structure must write for this build; the lengths are the client readers' own: the
    /// passport 16 (kr-1256 sub_99C1D0), mark list 8 (kr-1256 sub_99CFA0), guild info 16 (kr-1256 sub_9B2690) and attendance
    /// state 4 (kr-1256 sub_10C5590). A layout that disagrees is a configuration error, not a client surprise.
    /// </summary>
    private static readonly (string Structure, int Length)[] EmptyBodies =
    [
        ("ClanPassport", 16), ("ClanMarkInfo", 8), ("ClanGuildInfo", 16), ("ClanAttendanceState", 4),
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
        var router = context.Services.Get<IPacketRouter>();
        // The clan-server bounce sender is per build and absent where the client has no clan server (kr-812's
        // gcpReqClanPassport exists, the modern game port stamps it from the server dispatcher instead).
        if (opcodes.Find(PacketDirection.ClientToServer, "Unknown25") is not null)
            Route<ClanServerAckReceived>(context, router, messages, "Unknown25");
        Route<ClanMainOpened>(context, router, messages, "Unknown2040");
        Route<ClanAttendanceCheckedIn>(context, router, messages, "Unknown2041");
    }

    public async ValueTask SendAsync(ISession session, ClanOpened message, CancellationToken ct)
    {
        await session.SendAsync(opcodes.S2C("gspClanPassport"), WritePassport(tables, build), ct).ConfigureAwait(false);
        await session.SendAsync(opcodes.S2C("gspReqClanMarkInfo"), WriteMarkInfo(tables, build), ct).ConfigureAwait(false);
        await session.SendAsync(opcodes.S2C("gspResGuildInfo"), WriteGuildInfo(tables, build), ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, ClanAttendanceStateUpdated message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspClanAttendanceState"), WriteAttendanceState(tables, build), ct);

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
        PacketLayoutContract.RequireEmpty(tables, build, "Unknown2040Request", "Clan");
        PacketLayoutContract.RequireEmpty(tables, build, "Unknown2041Request", "Clan");
        // The four open snapshots are defaults-only writes: the binding supplies no field value, the codec lifts the
        // table defaults, and the structure states the fields the client reads (their names and types differ between
        // the baseline and the @kr-1256 restatements). RequireEmpty would reject a correct table and RequireFields
        // cannot name build-varying fields, so the checked property is the byte length of the empty write. There is
        // no consumed scalar field to retype, so this binding has no wrong-type rejection test.
        foreach (var (structure, length) in EmptyBodies)
        {
            var body = Write(tables, build, structure);
            if (body.Length != length)
                throw new ConfigException(
                    $"Invalid {structure} layout for '{build}': the empty body is {body.Length} bytes, the client reads {length}.");
        }
        foreach (var name in Replies)
            _ = opcodes.S2C(name);
        foreach (var name in Requests)
        {
            if (name is "Unknown25" && opcodes.Find(PacketDirection.ClientToServer, name) is null)
                continue;
            _ = opcodes.C2S(name);
        }
    }
}
