using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.MatchConnection;

/// <summary>Offers UDP only after the room starts and updates its transport when the room preference changes.</summary>
[Plugin("match-udp-relay-setup", Category = "network", Names = ["en=UDP relay setup", "zh-Hans=UDP 中继设置", "zh-Hant=UDP 中繼設定", "ko=UDP 릴레이 설정"], Description = "Room-scoped UDP relay admission and transport switching.",
    OptionalMany = [typeof(IRoomPeerRoster), typeof(IMatchUdpRelayDirectory), typeof(IMatchUdpRelayControl), typeof(IUdpTransportStatusRegistry)])]
public sealed class UdpRelaySetupPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<UdpRelaySetupOptions>();
        if (!options.Enabled) return;
        var rosters = context.Services.GetAll<IRoomPeerRoster>().Where(value => value.Protocol == options.GameProtocol).ToArray();
        var directories = context.Services.GetAll<IMatchUdpRelayDirectory>().Where(value => value.Protocol == options.RelayProtocol).ToArray();
        var controls = context.Services.GetAll<IMatchUdpRelayControl>().Where(value => value.Protocol == options.RelayProtocol).ToArray();
        if (rosters.Length != 1 || directories.Length != 1 || controls.Length != 1)
            throw new ConfigException($"UDP relay setup requires one room roster, directory and control; found {rosters.Length}/{directories.Length}/{controls.Length}.");
        var coordinator = new UdpRelayCoordinator(options, rosters[0], directories[0], controls[0], context.Logger);
        var status = new UdpRelayStatus(options.GameProtocol, rosters[0], directories[0]);
        foreach (var registry in context.Services.GetAll<IUdpTransportStatusRegistry>().Where(value => value.Protocol == options.GameProtocol))
            context.Effect(registry.Register(status));
        await context.RunTaskAsync("udp-relay-setup", coordinator.RunAsync).ConfigureAwait(false);
    }
}

/// <summary>Configures room-scoped native UDP offers and the deployment-wide enable switch.</summary>
public sealed class UdpRelaySetupOptions
{
    /// <summary>Gets or sets whether this row may offer UDP; false preserves TCP-only operation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the game protocol owning the room roster.</summary>
    [Required] public string GameProtocol { get; set; } = string.Empty;

    /// <summary>Gets or sets the relay protocol owning the authenticated UDP registrations.</summary>
    [Required] public string RelayProtocol { get; set; } = string.Empty;

    /// <summary>Gets or sets the external UDP port; zero uses the listener's bound port.</summary>
    [Range(0, ushort.MaxValue)] public int AdvertisePort { get; set; }

    /// <summary>Gets or sets how often room lifecycle and transport preferences are rechecked.</summary>
    [Range(10, 1000)] public int TickMs { get; set; } = 50;
}

/// <summary>Reads a seat's relay registration state; the coordinator's tick is what changes it.</summary>
internal sealed class UdpRelayStatus(string protocol, IRoomPeerRoster roster, IMatchUdpRelayDirectory directory) : IUdpTransportStatus
{
    public string Protocol => protocol;

    public string Kind => "relay";

    public UdpPathStatus Describe(ISession game)
    {
        var room = roster.FindBySession(game.Id);
        var seat = room?.Seats.FirstOrDefault(member => member.Game.Id == game.Id);
        if (room is null || seat is null) return new(UdpPathState.Tcp, "not seated");
        if (seat.Relay is not { Connection.IsClosed: false } relay) return new(UdpPathState.Tcp, "no match connection");
        return directory.StateOf(relay) switch
        {
            MatchUdpRelayState.Verified => new(UdpPathState.Udp, "verified"),
            MatchUdpRelayState.Bound => new(UdpPathState.Negotiating, "bound, answering the challenge"),
            MatchUdpRelayState.Offered => new(UdpPathState.Negotiating, "offered, no UDP probe received yet; if it stays here, UDP to the server is blocked"),
            MatchUdpRelayState.Disabled => new(UdpPathState.Tcp, "fallback: " + directory.DisabledFrom(relay) switch
            {
                MatchUdpRelayState.Offered => "the client gave up before any UDP probe reached the server; its UDP to the server port is blocked or lost",
                MatchUdpRelayState.Bound => "the client gave up while answering the server's UDP challenge; replies are lost on the way back",
                MatchUdpRelayState.Verified => "the client reported the verified UDP path failed during the round",
                _ => "the client asked for TCP before a UDP offer",
            }),
            MatchUdpRelayState.Registered when room.Stage != RoomStage.Playing => new(UdpPathState.Tcp, "waiting for the round"),
            MatchUdpRelayState.Registered when !room.UsesUdp => new(UdpPathState.Tcp, "room uses TCP"),
            MatchUdpRelayState.Registered => new(UdpPathState.Tcp, "waiting for every match connection"),
            _ => new(UdpPathState.Tcp, "match connection not registered for UDP"),
        };
    }
}

internal sealed class UdpRelayCoordinator(UdpRelaySetupOptions options, IRoomPeerRoster roster,
    IMatchUdpRelayDirectory directory, IMatchUdpRelayControl control, ILogger logger)
{
    internal async ValueTask RunAsync(CancellationToken ct)
    {
        if (control.IdentityBytes is not (4 or 8)) throw new ConfigException("UDP relay control identity width must be four or eight.");
        try
        {
            await DemandLoop.RunAsync(TimeSpan.FromMilliseconds(options.TickMs), directory.WhenAnyRegistered, TickAsync, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async ValueTask TickAsync(CancellationToken ct)
    {
        foreach (var session in directory.RegisteredSessions())
        {
            try { await UpdateAsync(session, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                (session.Connection.IsClosed || directory.StateOf(session) == MatchUdpRelayState.Absent))
            {
                logger.LogDebug(ex, "Session {Session} ended during UDP setup.", session.Id);
            }
        }
    }

    private async ValueTask UpdateAsync(ISession session, CancellationToken ct)
    {
        var state = directory.StateOf(session);
        if (state == MatchUdpRelayState.Absent) return;
        var room = roster.FindBySession(session.Id);
        var eligible = room is { Stage: RoomStage.Playing, UsesUdp: true }
            && room.Seats.All(seat => seat.RelayInitialized && seat.Relay is { Connection.IsClosed: false });
        if (!eligible)
        {
            if (state is MatchUdpRelayState.Offered or MatchUdpRelayState.Bound or MatchUdpRelayState.Verified or MatchUdpRelayState.Disabled)
            {
                directory.Reset(session);
                // kr-812 sub_8117F0 and kr-1256 sub_9601E0 clear bypass flags before checking a zero port/cookie.
                await control.WithdrawAsync(session, ct).ConfigureAwait(false);
                logger.LogInformation("Room {Room} session {Session} switched to TCP.", room?.RoomId, session.Id);
            }
            return;
        }
        if (state != MatchUdpRelayState.Registered) return;
        var identity = await directory.IssueAsync(session, control.IdentityBytes, ct).ConfigureAwait(false);
        await control.OfferAsync(session, checked((ushort)(options.AdvertisePort == 0 ? control.BoundPort : options.AdvertisePort)), identity, ct).ConfigureAwait(false);
        logger.LogInformation("Room {Room} session {Session} offered UDP after round start.", room!.RoomId, session.Id);
    }
}
