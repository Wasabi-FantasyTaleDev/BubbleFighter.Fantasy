using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>
/// The opt-in modern round-start binding: the start request, the loading reports, the relay offer and the
/// countdown and start packets, all through packet tables and names matched to kr-812's classes. It is mounted
/// with the typed room binding; the default profile keeps the raw path.
/// </summary>
internal sealed class ModernRoundBinding(string protocol, IOpcodeTable table, PacketTables packets, string build,
    IReadOnlyList<int> loadingFlags, ILogger logger)
    : IInboundMessageBinding<RoomStartRequested>, IInboundMessageBinding<RoundObjectAddRequested>,
      IInboundMessageBinding<RoundLoadReported>, IInboundMessageBinding<MatchConnectionReported>,
      IMessageSender<RoomMatchStarting>, IMessageSender<RoundCountdownStarted>, IMessageSender<RoundStarted>,
      IMessageSender<RoundObjectAddAcknowledged>, IMatchCapabilityOfferSender
{
    private static readonly string[] Structures =
    [
        "RoomEnterGameRequest", "RoundObjectAddRequest", "RoundMapLoadingReport", "RelayConnectionReport",
        "RelaySessionKey", "RelayConnectionOffer", "RoomEnterGame", "RoundEverybodyLoadingComplete", "RoundCountdown",
        "RoundStartGame",
    ];

    private static readonly string[] ServerNames =
    [
        "gspRoomSetSlot", "gspSessionSignatureData", "gspRoomEnterGame", "gspConnectToRelayServer",
        "gspGameEverybodyLoadingComplete", "gspGameCountDown", "gspGameStartGame", "Unknown609",
    ];

    // The no-limit round length: kr-1256 sub_9E1EB0 case 0x261 (S2C 609) hands it to the round-duration setter
    // kr-1256 sub_6FEFC0, which writes the same value as its second timer's ceiling.
    private const int NoTimeLimitMs = 0x3FFFFFFF;

    // The S2C 510 stage record's room state for an ordinary map (kr-1256 sub_9D9930 case 0x1FE copies it to
    // session+12); a mission names its mode there instead.
    private const int MapRoomState = 3;

    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();
    private int _ticketLength;

    public string Protocol => protocol;

    public int TicketLength => _ticketLength;

    internal void Mount(IPluginContext context)
    {
        _s2c = ModernRoutes.Preflight(table, packets, build, Structures, ServerNames);
        if (loadingFlags.Count != 3)
            throw new ConfigException("round_loading_flags must hold the three S2C gspGameEverybodyLoadingComplete words.");
        _ticketLength = PacketCodec.Write(packets, build, "RelaySessionKey", new Dictionary<string, object?>()).Length;
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomStartRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundObjectAddRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundLoadReported>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchConnectionReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomMatchStarting>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundCountdownStarted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundStarted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundObjectAddAcknowledged>>(this));
        context.Effect(context.Services.RegisterMany<IMatchCapabilityOfferSender>(this));

        Bind(context, "gcpRoomReqEnterGame", ReadStart);
        // kr-1256 sub_755330 sends the map report and then the local character's object, as kr-812 sub_661AF0 does.
        Bind(context, "gcpReqAddObject", body =>
            new RoundObjectAddRequested((int)PacketCodec.ReadScalars(packets, build, "RoundObjectAddRequest", body)["object_id"]!));
        // kr-1256 sub_6DC970 writes the same four words as kr-812 sub_50E670 (C2S 701), once the stage loop's
        // one-second wait is over; the words are checked, not interpreted.
        Bind(context, "gcpGameReportMapLoadingComplete", body =>
        {
            _ = PacketCodec.ReadScalars(packets, build, "RoundMapLoadingReport", body);
            return new RoundLoadReported();
        });
        // kr-1256 sub_961C00 writes the one flag from the relay success (true) and failure (false) paths.
        Bind(context, "gcpReportRelayConnected", body =>
            new MatchConnectionReported((bool)PacketCodec.ReadScalars(packets, build, "RelayConnectionReport", body)["unknown_4"]!));
        // Neither the map-data report nor the map recheck has an audited layout or a reply; neither advances loading.
        ModernRoutes.LogOnly(context, protocol, table, logger, "gcpGameReportMapData");
        ModernRoutes.LogOnly(context, protocol, table, logger, "gcpReqRecheckMap");
    }

    private void Bind<T>(IPluginContext context, string name, Func<ReadOnlySpan<byte>, T> decode) where T : class, IMessage =>
        ModernRoutes.Bind(context, protocol, table, logger, name, decode);

    // kr-1256 sub_9D7570 writes the resolved map key, the map record's name and the master's raw selection
    // (possibly a random marker), then the map's rule record.
    private RoomStartRequested ReadStart(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "RoomEnterGameRequest", body);
        return new RoomStartRequested((string)fields["map_key"]!)
        {
            MapName = (string)fields["unknown_36"]!,
            MapSelection = (string)fields["unknown_64"]!,
            TimeLimits = new RoomRoundTimeLimits((int)fields["death_match_time_limit_ms"]!, (int)fields["time_attack_time_limit_ms"]!),
        };
    }

    /// <summary>
    /// Sends one member the start roster, the session signature and the stage enter: every seat's record first,
    /// so kr-1256 sub_C80220 can pick team spawns, then the signature kr-1256 sub_9616A9 compares with the room
    /// mode before the relay join, then the stage record.
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoomMatchStarting message, CancellationToken ct)
    {
        var room = message.Room;
        var profiles = ModernRoomProjection.Members(room);
        foreach (var seat in profiles)
            await session.SendAsync(S2C("gspRoomSetSlot"), ModernRoomRecords.SeatUpdate(packets, build, seat, profiles), ct).ConfigureAwait(false);
        await session.SendAsync(S2C("gspSessionSignatureData"), ModernMatchCodec.WriteSessionSignature(checked((ushort)room.ModeId)), ct).ConfigureAwait(false);
        var stage = message.StageKey.Length > 0 ? message.StageKey : message.MapKey;
        await Send(session, "gspRoomEnterGame", "RoomEnterGame", new()
        {
            ["map_key"] = message.MapKey, ["stage_key"] = stage,
            ["map_selection"] = message.MapKey, ["room_state"] = message.IsMission ? room.ModeId : MapRoomState,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the loading gates and the countdown (kr-1256 sub_9E1EB0 cases 0x249 and 0x25F). Case 0x249 stores the
    /// first word at +1708 (relay selected) and the second at +1709 (peer selected); the third is the packet
    /// optimizer switch the debug overlay prints as "Optimize Packet" (kr-1256 sub_7B5A00). A seat playing over its
    /// game connection gets both selections cleared, so kr-1256 sub_95B910 sends its round packets there.
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoundCountdownStarted message, CancellationToken ct)
    {
        await Send(session, "gspGameEverybodyLoadingComplete", "RoundEverybodyLoadingComplete", new()
        {
            ["unknown_4"] = !message.MatchOverGame && loadingFlags[0] != 0,
            ["unknown_5"] = !message.MatchOverGame && loadingFlags[1] != 0,
            ["unknown_6"] = loadingFlags[2] != 0,
        }, ct).ConfigureAwait(false);
        await Send(session, "gspGameCountDown", "RoundCountdown", new() { ["milliseconds"] = message.Milliseconds }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the round start (kr-1256 sub_9E7570 reads two words, the first the round length); a round without a
    /// limit carries the client's own no-limit length and then the empty S2C 609.
    /// </summary>
    public async ValueTask SendAsync(ISession session, RoundStarted message, CancellationToken ct)
    {
        await Send(session, "gspGameStartGame", "RoundStartGame", new()
        {
            ["unknown_4"] = message.Unlimited ? NoTimeLimitMs : message.TimeLimitMs, ["unknown_8"] = 0,
        }, ct).ConfigureAwait(false);
        if (message.Unlimited)
            await session.SendAsync(S2C("Unknown609"), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    /// <summary>Acknowledges the requester's own character add with a zero countdown (kr-1256 sub_9E7080).</summary>
    public ValueTask SendAsync(ISession session, RoundObjectAddAcknowledged message, CancellationToken ct) =>
        Send(session, "gspGameCountDown", "RoundCountdown", new() { ["milliseconds"] = 0 }, ct);

    /// <summary>
    /// Offers the relay endpoint and the issued key: kr-1256 sub_9A7250 reads the address word, the port and the
    /// key in kr-812's widths.
    /// </summary>
    public ValueTask SendAsync(ISession session, MatchCapabilityOffered message, CancellationToken ct)
    {
        if (message.Address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The relay endpoint offer requires an IPv4 address.", nameof(message));
        var key = PacketCodec.ReadScalars(packets, build, "RelaySessionKey", message.Ticket.Span);
        return Send(session, "gspConnectToRelayServer", "RelayConnectionOffer", new()
        {
            ["endpoint"] = new Dictionary<string, object?>
            {
                ["ipv4_word"] = BinaryPrimitives.ReadUInt32LittleEndian(message.Address.GetAddressBytes()),
                ["port"] = message.Port,
            },
            ["key"] = key,
        }, ct);
    }

    private ushort S2C(string name) => _s2c[name];

    private ValueTask Send(ISession session, string name, string structure, Dictionary<string, object?> values, CancellationToken ct) =>
        session.SendAsync(S2C(name), PacketCodec.Write(packets, build, structure, values), ct);
}

/// <summary>
/// The relay side of the modern typed round: the join and the scene report become the shared capability
/// admission messages, so the round's room-issued key is the only credential, as on legacy builds.
/// </summary>
internal sealed class ModernRelayCapabilityBinding(string protocol, PacketTables packets, string build)
    : IInboundMessageBinding<MatchCapabilityAuthenticate>, IInboundMessageBinding<MatchSceneIdentityRegistered>
{
    public string Protocol => protocol;

    /// <summary>Reads the complete join (kr-1256 sub_95FD30) and keeps only its key, the S2C 571 copy.</summary>
    internal MatchCapabilityAuthenticate ReadJoin(ReadOnlySpan<byte> body)
    {
        var join = PacketCodec.Read(packets, build, "RelayJoinRequest", body);
        var key = (IReadOnlyDictionary<string, object?>)join["key"]!;
        return new(PacketCodec.Write(packets, build, "RelaySessionKey", key));
    }

    /// <summary>Projects the scene report onto the actor and nickname the shared admission checks.</summary>
    internal static MatchSceneIdentityRegistered ReadScene(ReadOnlySpan<byte> body)
    {
        var scene = ModernMatchCodec.ReadScene(body);
        return new(scene.EntityId, scene.Nickname);
    }
}
