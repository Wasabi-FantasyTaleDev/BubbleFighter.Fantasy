using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Shop;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages;

/// <summary>
/// The kr-1256 message codecs (cn-774 and tw-454 inherit them): the shop balance check, the room, party and
/// fighter-info adapters, the match transport handshake and the round session signature, between wire opcodes
/// and shared message contracts. Login and the main-menu probe live on <c>dialect.messages</c>.
/// </summary>
[Plugin("dialect.modern.messages", Description = "Modern-family typed message adapters.", SupportedFamilies = ["modern"],
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter), typeof(IOpcodeTableRegistry)],
    Optional = [typeof(IEquippedViewPush)], OptionalMany = [typeof(IOpcodeTable)],
    ConditionalProvides = [], ConditionalProvidesMany = [typeof(IInboundMessageBinding<ShopBalanceRequested>), typeof(IMessageSender<ShopBalanceUpdated>),
        typeof(IInboundMessageBinding<RoomListRequested>), typeof(IMessageSender<RoomListSnapshot>), typeof(IInboundMessageBinding<RoomCreateRequested>),
        typeof(IInboundMessageBinding<RoomJoinRequested>), typeof(IInboundMessageBinding<RoomLeaveRequested>), typeof(IInboundMessageBinding<RoomReadyRequested>),
        typeof(IInboundMessageBinding<RoomTeamChangeRequested>), typeof(IInboundMessageBinding<RoomStartUnavailableRequested>), typeof(IInboundMessageBinding<RoomUiRefreshRequested>),
        typeof(IInboundMessageBinding<RoomChatSent>), typeof(IInboundMessageBinding<RoomSeatLockRequested>), typeof(IMessageSender<RoomEntered>),
        typeof(IMessageSender<RoomSeatUpdated>), typeof(IMessageSender<RoomSeatVacated>), typeof(IMessageSender<RoomMasterChanged>),
        typeof(IMessageSender<RoomJoinRefused>),
        typeof(IInboundMessageBinding<RoomMasterChangeRequested>), typeof(IMessageSender<RoomMasterChangeReplied>),
        typeof(IMessageSender<RoomSettingsChanged>), typeof(IInboundMessageBinding<RoomRoundSettingsChangeRequested>), typeof(IInboundMessageBinding<RoomSettingsChangeRequested>),
        typeof(IMessageSender<RoomChatReceived>), typeof(IMessageSender<RoomSeatLockChanged>), typeof(IMessageSender<RoomUiChanged>),
        typeof(IMessageSender<RoomProbeAcknowledged>), typeof(IInboundMessageBinding<PlazaEnterRequested>),
        typeof(IInboundMessageBinding<PlazaMenuReturnRequested>), typeof(IInboundMessageBinding<PlazaPositionReported>),
        typeof(IMessageSender<PlazaEntered>), typeof(IMessageSender<PlazaMemberEntered>), typeof(IMessageSender<PlazaMemberLeft>),
        typeof(IMessageSender<PlazaPositionReceived>), typeof(IMessageSender<PlazaMenuReturnRequested>),
        typeof(IInboundMessageBinding<RoomStartRequested>), typeof(IInboundMessageBinding<RoundObjectAddRequested>),
        typeof(IInboundMessageBinding<RoundLoadReported>), typeof(IInboundMessageBinding<MatchConnectionReported>),
        typeof(IMessageSender<RoomMatchStarting>), typeof(IMessageSender<RoundCountdownStarted>), typeof(IMessageSender<RoundStarted>),
        typeof(IMessageSender<RoundObjectAddAcknowledged>), typeof(IMatchCapabilityOfferSender),
        typeof(IInboundMessageBinding<RoundMovementReported>), typeof(IMessageSender<RoundMovementRelayed>), typeof(IInboundMessageBinding<RoundFeedSent>),
        typeof(IMessageSender<RoundFeedReceived>), typeof(IInboundMessageBinding<RoundLifeReported>),
        typeof(IInboundMessageBinding<RoundResetRequested>), typeof(IMessageSender<RoundResetGranted>), typeof(IInboundMessageBinding<RoundEndRequested>),
        typeof(IMessageSender<RoundScoreUpdated>), typeof(IMessageSender<RoundScoreboard>), typeof(IMessageSender<RoundEnded>),
        typeof(IMessageSender<ResultStageAdvanced>),
        typeof(IInboundMessageBinding<MatchCapabilityAuthenticate>), typeof(IInboundMessageBinding<MatchSceneIdentityRegistered>),
        typeof(IInboundMessageBinding<MatchAuthenticate>), typeof(IInboundMessageBinding<MatchSceneRegistered>),
        typeof(IInboundMessageBinding<PartyInviteRequested>), typeof(IInboundMessageBinding<PartyInviteAccepted>),
        typeof(IInboundMessageBinding<PartyInviteAnswered>), typeof(IInboundMessageBinding<PartyInviteTokenRefused>),
        typeof(IMessageSender<PartyInviteNotified>), typeof(IMessageSender<PartyInviteReplied>), typeof(IMessageSender<PartyUpdated>),
        typeof(IMessageSender<PartyLeaderReplaced>), typeof(IMessageSender<PartyLeft>), typeof(IMessageSender<PartyMemberLeft>),
        typeof(IMessageSender<PartyMemberJoined>),
        ],
    Provides = [], ProvidesMany = [typeof(IMessageSender<MatchSessionSignature>), typeof(IInboundMessageBinding<MatchMapNamed>), typeof(IMessageSender<MatchAuthenticationAccepted>), typeof(IMessageSender<MatchSceneAccepted>)])]
public sealed class ModernMessagesPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ModernMessagesOptions>();
        var router = context.Services.Get<IMessageRouter>();
        var table = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        var build = context.Row.ClientBuild ?? throw new ConfigException("dialect.modern.messages requires an explicit client_build.");
        if (table.Find(PacketDirection.ClientToServer, "Unknown827") is { } balanceRequest)
            new Shop.ShopBalanceCodec(options.Protocol, balanceRequest, table.S2C("gspUserMoneyStat")).Mount(context);
        else
            context.Logger.LogInformation("Shop balance check is unavailable on {Protocol}: no Unknown827 request name.", options.Protocol);
        // Room play families: mounted only where the build's table names both directions.
        Play.ModernRelayCapabilityBinding? relayCapability = null;
        if (table.Find(PacketDirection.ClientToServer, "gcpReqListSession") is { } roomListRequest
            && table.Find(PacketDirection.ServerToClient, "gspRepListSession") is { } roomListReply)
        {
            // The room packets keep the legacy classes, so their layouts are the packet tables' (kr-1256 based on kr-812).
            var tables = Packets.PacketTables.Load(options.PacketsDirectory);
            var tableBuild = tables.NearestBuild(context.Row.LineageOrBuild())
                ?? throw new ConfigException($"dialect.modern.messages: no build of [{string.Join(", ", context.Row.LineageOrBuild())}] is named in '{options.PacketsDirectory}'.");
            new Play.RoomListCodec(options.Protocol, roomListRequest, roomListReply, tables, tableBuild, context.Logger).Mount(context);
            if (options.RoomBinding.Equals("typed", StringComparison.OrdinalIgnoreCase))
            {
                new Play.ModernRoomBinding(options.Protocol, table, tables, tableBuild, context.Logger, options.TypedRounds).Mount(context);
                new Play.ModernPlazaBinding(options.Protocol, table, tables, tableBuild, context.Logger).Mount(context);
                if (options.TypedRounds)
                {
                    new Play.ModernRoundBinding(options.Protocol, table, tables, tableBuild, options.RoundLoadingFlags, context.Logger).Mount(context);
                    new Play.ModernMovementBinding(options.Protocol, table,
                        context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.RelayProtocol), context.Logger).Mount(context);
                    new Play.ModernFeedBinding(options.Protocol, options.RelayProtocol).Mount(context);
                    new Play.ModernResultBinding(options.Protocol, table, options.ResultScoreboardModeIds, context.Logger).Mount(context);
                    _ = tables.Resolve(tableBuild, "RelayJoinRequest");
                    relayCapability = new Play.ModernRelayCapabilityBinding(options.RelayProtocol, tables, tableBuild);
                }
            }
        }
        else if (options.RoomBinding.Equals("typed", StringComparison.OrdinalIgnoreCase))
            throw new ConfigException("The typed modern room binding requires the room-list request and reply opcodes.");
        // The party invitation packets live on dialect.messages since S5 (packets/party.yaml, PartyBinding); this row
        // keeps the room, fighter-info and match adapters.
        // The match start flow sends the round's session signature through this row. The main-menu probe and
        // the whole login burst moved to dialect.messages (slice S3), which publishes the same S2C
        // gspSessionSignatureData reply for the empty C2S 521 probe.
        var signatureReply = table.S2C("gspSessionSignatureData");
        context.Effect(context.Services.RegisterMany<IMessageSender<MatchSessionSignature>>(
            new SessionSignatureSender(options.Protocol, signatureReply)));
        var match = new ModernMatchBinding(options.RelayProtocol);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchMapNamed>>(match));
        context.Effect(context.Services.RegisterMany<IMessageSender<MatchAuthenticationAccepted>>(match));
        context.Effect(context.Services.RegisterMany<IMessageSender<MatchSceneAccepted>>(match));
        var packets = context.Services.Get<IPacketRouter>();
        // Typed rounds admit the relay by the room-issued key alone, as legacy builds do; the raw path keeps the
        // account-bearing join its own start flow checks.
        if (relayCapability is { } capability)
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchCapabilityAuthenticate>>(capability));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchSceneIdentityRegistered>>(capability));
            RegisterRelay(1, "match capability authentication", capability.ReadJoin);
            RegisterRelay(12, "match scene identity", Play.ModernRelayCapabilityBinding.ReadScene);
        }
        else
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchAuthenticate>>(match));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchSceneRegistered>>(match));
            RegisterRelay(1, "match authentication", ModernMatchCodec.ReadAuthentication);
            RegisterRelay(12, "match scene", ModernMatchCodec.ReadScene);
        }
        context.Effect(packets.Register(options.RelayProtocol, 3, async (s, p, ct) =>
        {
            if (!await router.DispatchAsync(options.RelayProtocol, s, ModernMatchCodec.ReadMap(p.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("No match map handler is installed.");
        }));
        return ValueTask.CompletedTask;

        void RegisterRelay<T>(ushort opcode, string what, SpanReader<T> read) where T : IMessage =>
            context.Effect(packets.Register(options.RelayProtocol, opcode, async (s, p, ct) =>
            {
                if (!await router.DispatchAsync(options.RelayProtocol, s, read(p.Payload.Span), ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"No {what} handler is installed.");
            }));
    }

    private delegate T SpanReader<out T>(ReadOnlySpan<byte> body);

    /// <summary>
    /// Sends the round's <c>gspSessionSignatureData</c> (S2C 521 on kr-1256, 522 on cn-774): a u16 game mode, a
    /// zero u8 and an empty byte vector. The typed round start uses it to hand each seat its signature.
    /// </summary>
    private sealed class SessionSignatureSender(string protocol, ushort signatureReply) : IMessageSender<MatchSessionSignature>
    {
        /// <inheritdoc />
        public string Protocol => protocol;

        /// <inheritdoc />
        public ValueTask SendAsync(ISession session, MatchSessionSignature message, CancellationToken ct) =>
            session.SendAsync(signatureReply, ModernMatchCodec.WriteSessionSignature(message.GameMode), ct);
    }
}

/// <summary>Selects the game connection scope for these build-specific message adapters.</summary>
public sealed class ModernMessagesOptions : IValidatableOptions
{
    /// <summary>
    /// Gets or sets the room binding mode. <c>raw</c> keeps the verified default path; <c>typed</c> mounts the
    /// table-driven waiting-room and plaza bindings, an opt-in test path that cannot start rounds yet.
    /// </summary>
    public string RoomBinding { get; set; } = "raw";

    /// <summary>
    /// Gets or sets whether the typed room binding also starts rounds: the start request, the loading reports,
    /// the relay offer and the countdown and start packets go through the shared round flow, and the relay
    /// join is admitted by the round's key. Requires <c>room_binding: typed</c>; off by default.
    /// </summary>
    public bool TypedRounds { get; set; }

    /// <summary>
    /// Gets or sets the three words of S2C gspGameEverybodyLoadingComplete on a typed round (kr-1256 sub_9E1EB0
    /// case 0x249 reads three bool32 words). The first arms the client's own round-ready report, which the
    /// S2C gspGameStartGame handler kr-1256 sub_9E7570 checks. Defaults to the raw path's 1, 0, 1.
    /// </summary>
    public int[] RoundLoadingFlags { get; set; } = [1, 0, 1];

    /// <summary>
    /// Gets or sets the modes whose typed result shows the scoreboard rows (S2C 614, kr-1256 sub_9E2FE0 case 0x266):
    /// the ones whose team totals the round keeps. Defaults to the timed score mode, as on the packet relay path.
    /// </summary>
    public int[] ResultScoreboardModeIds { get; set; } = [1];
    /// <summary>Gets or sets the directory of the packet tables the room codecs read their layouts from.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";

    /// <summary>Gets or sets the connection protocol whose main-menu probe is decoded.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-1256/game";

    /// <summary>Gets or sets the separate authenticated match transport scope.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string RelayProtocol { get; set; } = "kr-1256/relay";

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!string.Equals(RoomBinding, "raw", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(RoomBinding, "typed", StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult("room_binding must be either 'raw' or 'typed'.", [nameof(RoomBinding)]);
        if (TypedRounds && !string.Equals(RoomBinding, "typed", StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult("typed_rounds requires room_binding: typed.", [nameof(TypedRounds), nameof(RoomBinding)]);
        if (ResultScoreboardModeIds.Any(mode => mode is <= 0 or > ushort.MaxValue))
            yield return new ValidationResult("result_scoreboard_mode_ids must be mode ids from 1 to 65535.", [nameof(ResultScoreboardModeIds)]);
        if (RoundLoadingFlags is not { Length: 3 } || RoundLoadingFlags[0] == 0)
            yield return new ValidationResult(
                "round_loading_flags must hold three words with a nonzero first word, which arms the client's round-ready report.",
                [nameof(RoundLoadingFlags)]);
    }
}
