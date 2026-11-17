using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>Configuration of the shared clan-server connection.</summary>
public sealed class ClanGatewayOptions
{
    /// <summary>Gets or sets the connection protocol name the clan listener row names.</summary>
    [Required]
    public string Protocol { get; set; } = "clan";
}

/// <summary>
/// One clan-server connection for every client build. The client opens it before it logs in, so the build is not
/// known when it connects; but the frame, its cipher and the handshake are the same in every build, so this row
/// greets the connection and answers its heartbeats itself. The login names the account, whose game session
/// names the build: from then on every frame goes to that build's own clan protocol (<c>&lt;build&gt;/clan</c>,
/// mounted by the build's protocol row), whose login handler checks the passport as it would on its own port.
/// </summary>
[Plugin("protocol.clan-gateway", Category = "network", Names = ["en=Shared clan connection", "zh-Hans=共享公会连接", "zh-Hant=共用公會連線", "ko=공용 클랜 연결"],
    Description = "One clan-server port for every client build; each connection is handed to its build after login.",
    Requires = [typeof(IConnectionProtocolRegistry), typeof(IPacketRouter), typeof(ISessionRegistry), typeof(IOpcodeTableRegistry)])]
public sealed class ClanGatewayPlugin : IPlugin
{
    // The handshake numbers are the same in every build with a clan connection: ClanHello is dispatcher case
    // 0x3EA (kr-1256 sub_525170, kr-812 sub_498790), the heartbeat is stamped by kr-1256 sub_525050 and
    // kr-812 sub_498690, and the login by kr-1256 sub_525DF0 and kr-812 sub_498C30. A build's table that names
    // them otherwise is refused when a login is routed to it.
    internal const ushort HelloOpcode = 1002, HeartbeatOpcode = 1003, LoginOpcode = 1005;

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ClanGatewayOptions>();
        var protocol = options.Protocol;
        if (protocol.Contains('/', StringComparison.Ordinal))
            throw new ConfigException($"protocol '{protocol}' must not contain '/': build protocols are named <build>/clan.");
        var wire = new ProtocolOptions();
        var versionVerified = new VersionVerifiedScope(wire.MaxTrackedConnections);
        context.Effect(versionVerified);
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.Get<IConnectionProtocolRegistry>().Register(
            CreateProtocol(protocol, wire, router, context.Events, versionVerified, context.Logger, context.Lifetime)));
        foreach (var registration in RegisterHandlers(protocol, router, context.Services.Get<ISessionRegistry>(),
            context.Services.Get<IOpcodeTableRegistry>(), context.Logger))
            context.Effect(registration);
        context.Logger.LogInformation("Shared clan connection '{Protocol}' mounted; logins are handed to their build's clan protocol.", protocol);
        return ValueTask.CompletedTask;
    }

    /// <summary>The shared connection: the clan frame, greeted with ClanHello, its frames routed once its login names a build.</summary>
    internal static WireProtocol CreateProtocol(string protocol, ProtocolOptions wire, IPacketRouter router, IEventBus events,
        VersionVerifiedScope versionVerified, ILogger logger, CancellationToken lifetime)
    {
        var profile = new ClientProfile(protocol, "", 0, 0, 0, "The shared clan-server connection before its login names a client build.");
        var dialect = profile.CreateClanDialect();
        var table = new OpcodeTable(profile, [
            new OpcodeEntry(HelloOpcode, PacketDirection.ServerToClient, "ClanHello", Evidence.Verified, "kr-1256 sub_525170 case 0x3EA", null),
            new OpcodeEntry(HeartbeatOpcode, PacketDirection.ClientToServer, "ClanHeartbeat", Evidence.Verified, "kr-1256 sub_525050", null),
            new OpcodeEntry(LoginOpcode, PacketDirection.ClientToServer, "ClanLogin", Evidence.Verified, "kr-1256 sub_525DF0", null),
        ]);
        return new WireProtocol(protocol, dialect, dialect.EncodeFrame(HelloOpcode, []), wire, router, table, events, sessions: null,
            versionVerified, logger, lifetime, requiresPatchHandshake: false, routable: true);
    }

    /// <summary>
    /// The shared connection's own handlers, for frames that arrive before a login names a build. The client repeats
    /// the heartbeat every 3000 ms and asks its game connection for a passport on every hello (kr-1256 sub_525170
    /// case 0x3EA), so each heartbeat is answered with a hello until the login.
    /// </summary>
    internal static IDisposable[] RegisterHandlers(string protocol, IPacketRouter router, ISessionRegistry sessions, IOpcodeTableRegistry tables,
        ILogger logger) =>
    [
        router.Register(protocol, HeartbeatOpcode, (session, packet, ct) =>
            packet.Payload.Length != 0 ? throw new PacketFormatException("ClanHeartbeat must be empty.", 0)
                : session.SendAsync(HelloOpcode, ReadOnlyMemory<byte>.Empty, ct)),
        router.Register(protocol, LoginOpcode, (session, packet, ct) => RouteLoginAsync(session, packet, router, sessions, tables, logger, ct)),
    ];

    /// <summary>
    /// Finds the build of the account a login names and hands the connection, and this login, to that build's clan
    /// protocol. The account's first word is its user number in every build (<c>ClanServerLogin.user_number</c>
    /// in packets/clan-server.yaml; kr-1256 sub_525DF0, kr-812 sub_498C30). A login whose account has no game
    /// session, or whose build has no clan connection, is closed: the client reconnects by itself and is greeted
    /// again (kr-1256 sub_525050).
    /// </summary>
    private static async ValueTask RouteLoginAsync(ISession session, InboundPacket packet, IPacketRouter router, ISessionRegistry sessions,
        IOpcodeTableRegistry tables, ILogger logger, CancellationToken ct)
    {
        if (session is not WireSession wire)
            throw new InvalidOperationException($"Session {session.Id} is not a wire session and cannot be routed.");
        if (packet.Payload.Length < sizeof(int))
            throw new PacketFormatException($"ClanLogin of {packet.Payload.Length} bytes has no user number.", 0);
        var userNumber = BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.Span);
        var game = sessions.All.FirstOrDefault(candidate => !candidate.Connection.IsClosed
            && candidate.TryFeature<IPlayerSession>(out var player) && player.Identity.UserNum == userNumber);
        if (game is null)
        {
            logger.LogWarning("Clan connection {Id} login for user={User} refused: no game session is logged in as that user.", session.Id, userNumber);
            await session.Connection.CloseAsync("clan login without a game session").ConfigureAwait(false);
            return;
        }
        var build = game.Profile;
        var clan = tables.TryGet(build.Id, out var buildTable) ? buildTable!.ForConnection(OpcodeConnection.Clan) : null;
        if (clan?.Find(PacketDirection.ClientToServer, "ClanLogin") is not { } login)
        {
            logger.LogWarning("Clan connection {Id} login for user={User} refused: build {Build} has no clan connection.", session.Id, userNumber, build.Id);
            await session.Connection.CloseAsync($"build {build.Id} has no clan connection").ConfigureAwait(false);
            return;
        }
        if (login != LoginOpcode || clan.Find(PacketDirection.ClientToServer, "ClanHeartbeat") != HeartbeatOpcode
            || clan.Find(PacketDirection.ServerToClient, "ClanHello") != HelloOpcode)
            throw new ConfigException($"Opcode table '{build.Id}' numbers the clan handshake differently from the shared clan connection ({HelloOpcode}, {HeartbeatOpcode}, {LoginOpcode}).");
        var route = build.Id + "/clan";
        wire.Route(build, route);
        logger.LogInformation("Clan connection {Id} of user={User} handed to {Route}.", session.Id, userNumber, route);
        await router.DispatchAsync(route, session, packet, ct).ConfigureAwait(false);
    }
}
