using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Signals;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.MatchConnection;

/// <summary>Coordinates bounded, authenticated room-local UDP discovery and native peer punching.</summary>
[Plugin("match-udp-peers", Category = "network", Names = ["en=UDP match peers", "zh-Hans=UDP 对局对等端", "zh-Hant=UDP 對局對等端", "ko=UDP 매치 피어"], Description = "Room-scoped native UDP peer setup.", Requires = [typeof(IMessageRouter)],
    OptionalMany = [typeof(IRoomPeerRoster), typeof(IUdpPeerDialect), typeof(IUdpDatagramEndpoint), typeof(IUdpTransportStatusRegistry)])]
public sealed class UdpPeerSetupPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<UdpPeerSetupOptions>();
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(options.Protocol) || string.IsNullOrWhiteSpace(options.UdpProtocol))
            throw new ConfigException("UDP peer setup requires nonempty game and UDP protocols.");
        var rosters = context.Services.GetAll<IRoomPeerRoster>().Where(value => value.Protocol == options.Protocol).ToArray();
        var dialects = context.Services.GetAll<IUdpPeerDialect>().Where(value => value.Protocol == options.Protocol).ToArray();
        var endpoints = context.Services.GetAll<IUdpDatagramEndpoint>().Where(value => value.Protocol == options.UdpProtocol).ToArray();
        if (rosters.Length != 1 || dialects.Length != 1 || endpoints.Length != 1)
            throw new ConfigException($"UDP peer setup '{options.Protocol}' requires one roster, dialect and endpoint; found {rosters.Length}/{dialects.Length}/{endpoints.Length}.");
        var coordinator = new UdpPeerCoordinator(options, rosters[0], dialects[0], endpoints[0], context.Logger);
        context.Effect(coordinator);
        foreach (var registry in context.Services.GetAll<IUdpTransportStatusRegistry>().Where(value => value.Protocol == options.Protocol))
            context.Effect(registry.Register(coordinator));
        var routes = context.Services.Get<IMessageRouter>().For(context);
        context.Effect(routes.Use<RoundLoadReported>(options.Protocol, coordinator.LoadAsync));
        context.Effect(routes.Use<RoundFeedSent>(options.Protocol, coordinator.FilterMovementAsync));
        context.Effect(routes.On<UdpPeerTableAcknowledged>(options.Protocol, coordinator.TableAcknowledgedAsync));
        context.Effect(routes.On<UdpPeerPunchReported>(options.Protocol, coordinator.PunchReportedAsync));
        context.Effect(routes.On<UdpPeerRelayMembershipChanged>(options.Protocol, coordinator.MembershipChangedAsync));
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(
            endpoints[0].Register(coordinator.ReceiveAsync)), cancellationToken).ConfigureAwait(false);
        await context.RunTaskAsync("udp-peer-setup", coordinator.RunAsync).ConfigureAwait(false);
    }
}

/// <summary>Bounds discovery retention, room count and native peer setup timing.</summary>
public sealed class UdpPeerSetupOptions
{
    /// <summary>Gets or sets whether native discovery and peer punching run for this row.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the game protocol owning these rooms.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured discovery listener protocol.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string UdpProtocol { get; set; } = string.Empty;

    /// <summary>Gets or sets an externally mapped UDP port; zero advertises the bound listener port.</summary>
    [Range(0, ushort.MaxValue)]
    public int AdvertisePort { get; set; }

    /// <summary>Gets or sets the maximum number of retained round coordinations.</summary>
    [Range(1, 65536)]
    public int MaxRounds { get; set; } = 128;

    /// <summary>Gets or sets the maximum number of retained player identities across those rounds.</summary>
    [Range(1, 65536)]
    public int MaxPeers { get; set; } = 1024;

    /// <summary>Gets or sets the interval at which room state and setup deadlines are rechecked.</summary>
    [Range(10, 1000)]
    public int TickMs { get; set; } = 50;

    /// <summary>Gets or sets how long to wait for native UDP discovery before retaining TCP fallback.</summary>
    [Range(100, 60000)]
    public int DiscoveryTimeoutMs { get; set; } = 3000;

    /// <summary>Gets or sets the interval for repeating one-shot discovery while waiting; zero disables refresh offers.</summary>
    [Range(0, 60000)]
    public int DiscoveryRefreshMs { get; set; } = 1000;

    /// <summary>Gets or sets how long to wait for every native peer-table acknowledgment.</summary>
    [Range(100, 60000)]
    public int TableTimeoutMs { get; set; } = 2000;

    /// <summary>Gets or sets the native inner/outer punch attempt window.</summary>
    [Range(100, 60000)]
    public int PunchWindowMs { get; set; } = 1000;

    /// <summary>Gets or sets the interval between native punch retries; zero sends only the initial request.</summary>
    [Range(0, 60000)]
    public int PunchRetryMs { get; set; } = 250;

    /// <summary>Gets or sets the maximum duration of one ordered control publication.</summary>
    [Range(100, 60000)]
    public int ControlTimeoutMs { get; set; } = 2000;
}

internal sealed class UdpPeerCoordinator : IDisposable, IUdpTransportStatus
{
    private enum Phase { Waiting, Offering, Discovery, Refreshing, Publishing, Tables, Prepared, Starting, Punching, Finalizing, Ready, Fallback, Suspended }
    private sealed class Round(int roomId, int generation, bool preparedInRoom = false, long installationVersion = 0)
    {
        internal int RoomId { get; } = roomId;
        internal int Generation { get; } = generation;
        internal bool PreparedInRoom { get; } = preparedInRoom;
        internal long InstallationVersion { get; } = installationVersion;
        internal List<Peer> Peers { get; } = [];
        internal Phase Phase;
        internal long Deadline;
        internal long NextPunch;
        internal long NextDiscovery;
        internal bool TableDirty;
        internal string FallbackReason = "";
    }

    private sealed class Peer(Round round, RoomSeatView seat, uint identity)
    {
        internal Round Round { get; } = round;
        internal int Slot { get; } = seat.Slot;
        internal ISession Game { get; } = seat.Game;
        internal ISession? Relay;
        internal uint Identity { get; set; } = identity;
        internal IPEndPoint? Endpoint;
        internal IPAddress? LocalAddress;
        internal ushort LocalPort;
        internal bool TableAcknowledged;
        internal uint? ReportedConnections;
        internal HashSet<long> Direct { get; } = [];
    }

    public string Protocol => _options.Protocol;

    public string Kind => "peer";

    public UdpPathStatus Describe(ISession game)
    {
        lock (_gate)
        {
            var peer = _rounds.Values.OrderByDescending(round => round.Generation)
                .SelectMany(round => round.Peers).FirstOrDefault(member => member.Game.Id == game.Id);
            if (peer is null) return new(UdpPathState.Tcp, "no UDP setup for this round");
            var others = peer.Round.Peers.Count - 1;
            return peer.Round.Phase switch
            {
                Phase.Ready when peer.Direct.Count > 0 => new(UdpPathState.Udp, $"direct to {peer.Direct.Count} of {others}"),
                Phase.Ready when peer.ReportedConnections is { } count => new(UdpPathState.Tcp,
                    $"fallback: client reported {count} direct path(s); no authenticated direct membership"),
                Phase.Ready => new(UdpPathState.Tcp, "fallback: no confirmed direct path; client punch result not received"),
                Phase.Offering or Phase.Discovery or Phase.Refreshing => new(UdpPathState.Negotiating, "discovering endpoints"),
                Phase.Publishing or Phase.Tables or Phase.Prepared => new(UdpPathState.Negotiating, "exchanging peer tables"),
                Phase.Starting or Phase.Punching or Phase.Finalizing => new(UdpPathState.Negotiating, "punching"),
                Phase.Fallback => new(UdpPathState.Tcp, "fallback: " + peer.Round.FallbackReason),
                Phase.Suspended => new(UdpPathState.Tcp, "room uses TCP"),
                _ => new(UdpPathState.Tcp, "waiting for the round"),
            };
        }
    }

    private static string SlotList(IEnumerable<Peer> peers) => string.Join(", ", peers.Select(peer => peer.Slot + 1));

    private readonly object _gate = new();
    private readonly Dictionary<(int Room, int Generation), Round> _rounds = [];
    private readonly Dictionary<uint, Peer> _identities = [];
    private readonly UdpPeerSetupOptions _options;
    private readonly IRoomPeerRoster _roster;
    private readonly IUdpPeerDialect _wire;
    private readonly ILogger _logger;
    private readonly ushort _port;
    // Complete while any round is tracked; refreshed under _gate wherever _rounds changes.
    private readonly PresenceSignal _tracking = new();
    private bool _disposed;
    private bool _capacityReported;

    internal UdpPeerCoordinator(UdpPeerSetupOptions options, IRoomPeerRoster roster, IUdpPeerDialect wire,
        IUdpDatagramEndpoint endpoint, ILogger logger)
    {
        Validator.ValidateObject(options, new ValidationContext(options), true);
        _options = options;
        _roster = roster;
        _wire = wire;
        _logger = logger;
        _port = checked((ushort)(options.AdvertisePort == 0 ? endpoint.LocalEndPoint.Port : options.AdvertisePort));
        if (_port == 0 || options.Protocol != roster.Protocol || options.Protocol != wire.Protocol)
            throw new ConfigException("UDP peer setup requires a nonzero advertised port and matching protocol scopes.");
    }

    internal async ValueTask LoadAsync(ISession session, RoundLoadReported message, MessageHandler<RoundLoadReported> next,
        CancellationToken ct)
    {
        var view = _roster.FindBySession(session.Id);
        if (!_wire.DiscoveryInWaitingRoom && view is { IsMatchRoom: true, Stage: RoomStage.Starting }
            && view.Seats.Any(seat => ReferenceEquals(seat.Game, session)))
        {
            Prune();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var key = (view.RoomId, view.Generation);
                if (!_rounds.ContainsKey(key))
                {
                    if (_rounds.Count >= _options.MaxRounds || view.Seats.Count > _options.MaxPeers - _identities.Count)
                    {
                        if (!_capacityReported)
                            _logger.LogError("UDP peer setup capacity reached; additional rooms retain TCP.");
                        _capacityReported = true;
                    }
                    else
                    {
                        _capacityReported = false;
                        var round = new Round(view.RoomId, view.Generation);
                        try
                        {
                            foreach (var seat in view.Seats)
                            {
                                var peer = new Peer(round, seat, NewIdentity());
                                round.Peers.Add(peer);
                                _identities.Add(peer.Identity, peer);
                            }
                            _rounds.Add(key, round);
                            _tracking.Refresh(() => _rounds.Count != 0);
                        }
                        catch
                        {
                            foreach (var peer in round.Peers) _identities.Remove(peer.Identity);
                            throw;
                        }
                    }
                }
            }
        }
        // cn-324 sub_5AD610 pumps discovery only in RoomStage, before loading; other builds can offer later.
        // Never await acknowledgments here: the same game stream must remain free to receive them.
        await next(session, message, ct).ConfigureAwait(false);
    }

    internal ValueTask ReceiveAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UdpPeerDiscovery? discovery;
        try { discovery = _wire.ReadDiscovery(payload); }
        catch (PacketFormatException ex)
        {
            _logger.LogWarning("Malformed UDP peer discovery from {Remote}: {Error}.", remote, ex.Message);
            return ValueTask.CompletedTask;
        }
        if (discovery is null)
        {
            _logger.LogWarning("Unknown UDP peer discovery packet from {Remote}.", remote);
            return ValueTask.CompletedTask;
        }
        Peer? peer;
        lock (_gate) peer = !_disposed && _identities.TryGetValue(discovery.ProbeIdentity, out var found) ? found : null;
        if (peer is null || !Current(peer) || discovery.LocalPort == 0 || remote.Port == 0
            || remote.Address.AddressFamily != AddressFamily.InterNetwork
            || peer.Game.Connection.RemoteEndPoint is not IPEndPoint gameRemote
            || !gameRemote.Address.MapToIPv4().Equals(remote.Address))
            return ValueTask.CompletedTask;
        lock (_gate)
        {
            if (_disposed || !Contains(peer))
                return ValueTask.CompletedTask;
            var refreshing = peer.Round.PreparedInRoom
                && peer.Round.Phase is Phase.Refreshing or Phase.Publishing or Phase.Tables or Phase.Prepared;
            if (!refreshing && peer.Round.Phase is not (Phase.Offering or Phase.Discovery))
                return ValueTask.CompletedTask;
            var localAddress = LocalCandidate(discovery.LocalAddress, remote.Address) ? discovery.LocalAddress : null;
            var changed = peer.Endpoint is not null && (!peer.Endpoint.Equals(remote)
                || !Equals(peer.LocalAddress, localAddress) || peer.LocalPort != discovery.LocalPort);
            if (changed && !refreshing) return ValueTask.CompletedTask;
            var first = peer.Endpoint is null;
            peer.Endpoint = new(remote.Address, remote.Port);
            peer.LocalAddress = localAddress;
            peer.LocalPort = discovery.LocalPort;
            if (changed)
            {
                peer.Round.TableDirty = true;
                _logger.LogInformation("Room {Room} slot {Slot} UDP endpoint changed before punching; refreshing its peer table.",
                    peer.Round.RoomId, peer.Slot);
            }
            if (first)
                _logger.LogInformation("Room {Room} slot {Slot} UDP discovery endpoint accepted.", peer.Round.RoomId, peer.Slot);
        }
        return ValueTask.CompletedTask;
    }

    internal ValueTask TableAcknowledgedAsync(ISession session, UdpPeerTableAcknowledged message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var peer = FindGame(session);
        if (peer is null || !Current(peer)) return ValueTask.CompletedTask;
        lock (_gate)
            if (Contains(peer) && peer.Round.Phase is Phase.Refreshing or Phase.Publishing or Phase.Tables)
                peer.TableAcknowledged = true;
        return ValueTask.CompletedTask;
    }

    internal ValueTask PunchReportedAsync(ISession session, UdpPeerPunchReported message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var peer = FindGame(session);
        if (peer is null || !Current(peer)) return ValueTask.CompletedTask;
        if (message.OccupiedSeats > _options.MaxPeers || message.ConnectedPeers > message.OccupiedSeats)
        {
            _logger.LogWarning("Room {Room} slot {Slot} ignored UDP counts {Connected}/{Occupied} outside the configured peer bounds.",
                peer.Round.RoomId, peer.Slot, message.ConnectedPeers, message.OccupiedSeats);
            return ValueTask.CompletedTask;
        }
        // Counts never grant a direct path. Only the current authenticated relay's per-peer request does.
        lock (_gate)
            if (Contains(peer) && peer.Round.Phase is Phase.Finalizing or Phase.Ready)
                peer.ReportedConnections = message.ConnectedPeers;
        _logger.LogInformation("Room {Room} peer slot {Slot} reports {Connected}/{Occupied} UDP paths.",
            peer.Round.RoomId, peer.Slot, message.ConnectedPeers, message.OccupiedSeats);
        return ValueTask.CompletedTask;
    }

    internal ValueTask MembershipChangedAsync(ISession session, UdpPeerRelayMembershipChanged message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var view = _roster.FindBySession(session.Id);
        var seat = view?.Seats.SingleOrDefault(value => ReferenceEquals(value.Relay, session) && value.RelayInitialized);
        if (view is null || seat is null || view.Stage != RoomStage.Playing || !view.UsesUdp) return ValueTask.CompletedTask;
        lock (_gate)
        {
            if (_disposed || !_rounds.TryGetValue((view.RoomId, view.Generation), out var round)) return ValueTask.CompletedTask;
            var sender = round.Peers.SingleOrDefault(value => ReferenceEquals(value.Game, seat.Game));
            var target = round.Peers.SingleOrDefault(value => value.Identity == message.PeerIdentity
                && view.Seats.Any(current => ReferenceEquals(current.Game, value.Game)));
            if (sender is null || target is null || ReferenceEquals(sender, target))
            {
                // cn-324 sub_6F8530 queues relay membership with the peer identity; a later offer replaces it.
                _logger.LogWarning("Room {Room} session {Session} ignored UDP membership without another current peer.",
                    view.RoomId, session.Id);
                return ValueTask.CompletedTask;
            }
            if (!CurrentRelay(sender, view) || !CurrentRelay(target, view))
                return ValueTask.CompletedTask;
            if (message.Relayed) sender.Direct.Remove(target.Game.Id);
            else if (round.Phase is Phase.Finalizing or Phase.Ready && sender.Endpoint is not null && target.Endpoint is not null)
                sender.Direct.Add(target.Game.Id);
            // cn-279 sub_702AC0 and cn-324 sub_6F7BE0 reset client paths; queued old reports cannot re-enable them.
            else return ValueTask.CompletedTask;
        }
        return ValueTask.CompletedTask;
    }

    internal async ValueTask FilterMovementAsync(ISession session, RoundFeedSent message, MessageHandler<RoundFeedSent> next,
        CancellationToken ct)
    {
        if (_wire.IsPeerMovement(message.Code) && _roster.FindBySession(session.Id) is { Stage: RoomStage.Playing, UsesUdp: true } view)
        {
            lock (_gate)
            {
                if (!_disposed && _rounds.TryGetValue((view.RoomId, view.Generation), out var round))
                {
                    var sender = round.Peers.SingleOrDefault(peer => view.Seats.Any(seat => ReferenceEquals(seat.Game, peer.Game)
                        && (ReferenceEquals(seat.Game, session) || ReferenceEquals(seat.Relay, session)) && seat.RelayInitialized));
                    if (sender is not null && CurrentRelay(sender, view))
                    {
                        var direct = new HashSet<long>(message.DirectPeerRecipients ?? new HashSet<long>());
                        foreach (var target in view.Seats)
                            if (sender.Direct.Contains(target.Game.Id)
                                && round.Peers.Any(peer => ReferenceEquals(peer.Game, target.Game) && CurrentRelay(peer, view)))
                                direct.Add(target.Game.Id);
                        message = message with { DirectPeerRecipients = direct };
                    }
                }
            }
        }
        await next(session, message, ct).ConfigureAwait(false);
    }

    internal async ValueTask RunAsync(CancellationToken lifetime)
    {
        try
        {
            await DemandLoop.RunAsync(TimeSpan.FromMilliseconds(_options.TickMs), WhenWork,
                ct => TickAsync(Environment.TickCount64, ct), lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            Peer[] peers;
            lock (_gate) peers = _identities.Values.Where(peer => peer.Round.Phase != Phase.Waiting).ToArray();
            using var stop = new CancellationTokenSource(_options.ControlTimeoutMs);
            foreach (var peer in peers)
            {
                if (!Current(peer)) continue;
                try { await ResetAsync(peer, stop.Token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UDP peer setup could not restore TCP for session {Session} during unload.", peer.Game.Id);
                    await peer.Game.Connection.CloseAsync("UDP peer setup unloaded before TCP restoration").ConfigureAwait(false);
                }
            }
        }
    }

    // A tracked round always needs the tick. A build that prepares UDP in the waiting room also needs it while any
    // room exists, because that is where its rounds are found (TrackWaitingRooms); other builds learn of a round
    // from the load report, which adds it here.
    internal Task WhenWork()
    {
        var rounds = _tracking.WhenPresent;
        if (rounds.IsCompleted || !_wire.DiscoveryInWaitingRoom) return rounds;
        var rooms = _roster.WhenAnyRoom();
        return rooms.IsCompleted ? rooms : Task.WhenAny(rounds, rooms);
    }

    internal async ValueTask TickAsync(long now, CancellationToken ct)
    {
        Prune();
        if (_wire.DiscoveryInWaitingRoom) TrackWaitingRooms();
        Round[] rounds;
        lock (_gate) rounds = _disposed ? [] : _rounds.Values.ToArray();
        foreach (var round in rounds)
        {
            try
            {
                await _roster.ExecuteAsync(round.RoomId, (view, operationCt) => TickRoundAsync(round, view, now, operationCt), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Room {Room} UDP control could not enter the room queue; retaining TCP until a later tick.", round.RoomId);
            }
        }
    }

    private async ValueTask TickRoundAsync(Round round, RoomRosterView view, long now, CancellationToken ct)
    {
        if (!Matches(round, view) || (!round.PreparedInRoom && view.Stage != RoomStage.Playing)) return;
        Phase action;
        Phase previous;
        bool retry;
        Peer[] peers;
        lock (_gate)
        {
            if (!Contains(round) || round.Peers.Count == 0) return;
            peers = round.Peers.ToArray();
            previous = round.Phase;
            retry = round.Phase == Phase.Punching && now < round.Deadline
                && _options.PunchRetryMs > 0 && now >= round.NextPunch;
            if (!view.UsesUdp && round.Phase is Phase.Waiting or Phase.Suspended) return;
            action = round.Phase switch
            {
                _ when !view.UsesUdp => Phase.Suspended,
                not (Phase.Waiting or Phase.Fallback or Phase.Suspended) when !round.PreparedInRoom
                    && peers.Any(peer => !CurrentRelay(peer, view)) => Phase.Fallback,
                Phase.Starting or Phase.Punching or Phase.Finalizing or Phase.Ready
                    when peers.Any(peer => !CurrentRelay(peer, view)) => Phase.Fallback,
                Phase.Waiting or Phase.Suspended when round.PreparedInRoom && view.Stage == RoomStage.Waiting => Phase.Offering,
                Phase.Waiting or Phase.Suspended when !round.PreparedInRoom && peers.All(peer => view.Seats.Any(seat => ReferenceEquals(seat.Game, peer.Game)
                    && seat.RelayInitialized && seat.Relay is { Connection.IsClosed: false })) => Phase.Offering,
                Phase.Discovery when peers.All(peer => peer.Endpoint is not null) => Phase.Publishing,
                Phase.Tables or Phase.Prepared when round.TableDirty => Phase.Publishing,
                Phase.Tables when peers.All(peer => peer.TableAcknowledged) => round.PreparedInRoom ? Phase.Prepared : Phase.Starting,
                Phase.Prepared when view.Stage == RoomStage.Playing && peers.All(peer => view.Seats.Any(seat => ReferenceEquals(seat.Game, peer.Game)
                    && seat.RelayInitialized && seat.Relay is { Connection.IsClosed: false })) => Phase.Starting,
                Phase.Punching when now >= round.Deadline => Phase.Finalizing,
                Phase.Punching when retry => Phase.Starting,
                // cn-406 sub_70E190 only arms one reply that RoomStage sub_5A7110 sends once and clears, so a missing
                // discovery is offered again for as long as the room waits. The same handler empties the peer map
                // (sub_5E6260 on +556) that table rows fill (sub_70F6B0) and probes are checked against (sub_70F850),
                // so once a table is out no offer may follow; a missing acknowledgment re-sends the table instead.
                Phase.Discovery when round.PreparedInRoom && view.Stage == RoomStage.Waiting
                    => _options.DiscoveryRefreshMs > 0 && now >= round.NextDiscovery ? Phase.Refreshing
                        : _options.DiscoveryRefreshMs == 0 && now >= round.Deadline ? Phase.Fallback
                        : round.Phase,
                Phase.Tables when round.PreparedInRoom && view.Stage == RoomStage.Waiting && now >= round.Deadline => Phase.Publishing,
                Phase.Discovery or Phase.Tables when now >= round.Deadline => Phase.Fallback,
                _ => round.Phase,
            };
            if (action == round.Phase) return;
            if (action == Phase.Fallback)
                // Early clients set up in the waiting room before any relay exists, so only a timeout explains their fallback.
                round.FallbackReason = !(round.PreparedInRoom && previous is Phase.Discovery or Phase.Tables)
                    && peers.Any(peer => !CurrentRelay(peer, view))
                    ? "a player's match connection closed or was replaced during setup"
                    : round.Phase == Phase.Discovery
                        ? $"no UDP discovery from slot {SlotList(peers.Where(peer => peer.Endpoint is null))} {(round.PreparedInRoom ? "before the round loaded" : $"within {_options.DiscoveryTimeoutMs} ms")}; UDP to the server may be blocked"
                        : $"peer table not acknowledged by slot {SlotList(peers.Where(peer => !peer.TableAcknowledged))} {(round.PreparedInRoom ? "before the round loaded" : $"within {_options.TableTimeoutMs} ms")}";
            if (action == Phase.Offering)
                foreach (var peer in peers)
                {
                    if (round.Phase == Phase.Suspended)
                    {
                        var identity = NewIdentity();
                        _identities.Remove(peer.Identity);
                        peer.Identity = identity;
                        _identities.Add(identity, peer);
                    }
                    peer.Relay = view.Seats.Single(seat => ReferenceEquals(seat.Game, peer.Game)).Relay;
                    peer.Endpoint = null;
                    peer.LocalAddress = null;
                    peer.LocalPort = 0;
                    peer.TableAcknowledged = false;
                    peer.ReportedConnections = null;
                    peer.Direct.Clear();
                }
            if (action == Phase.Starting && round.PreparedInRoom)
                foreach (var peer in peers)
                    peer.Relay = view.Seats.Single(seat => ReferenceEquals(seat.Game, peer.Game)).Relay;
            if (action == Phase.Publishing)
            {
                round.TableDirty = false;
                foreach (var peer in peers) peer.TableAcknowledged = false;
            }
            round.Phase = action;
        }
        using var send = CancellationTokenSource.CreateLinkedTokenSource(ct);
        send.CancelAfter(_options.ControlTimeoutMs);
        try
        {
            foreach (var peer in peers)
            {
                if (!Current(peer)) continue;
                switch (action)
                {
                    case Phase.Offering:
                    case Phase.Refreshing:
                        // cn-324 sub_6F7BE0 / cn-406 sub_70E190 / id-400 sub_791630 re-arm
                        // one room-stage discovery while retaining the same identity and candidate addresses.
                        await _wire.OfferAsync(peer.Game, _port, peer.Identity, send.Token).ConfigureAwait(false);
                        break;
                    case Phase.Publishing:
                        // cn-324 sub_5AD610 / cn-406 sub_5A7110 send one discovery per offer.
                        // Waiting-room offers re-arm it; table-end handlers need no preceding stop.
                        if (!round.PreparedInRoom)
                            await _wire.StopDiscoveryAsync(peer.Game, send.Token).ConfigureAwait(false);
                        await SendTableAsync(peer, peers, send.Token).ConfigureAwait(false);
                        break;
                    case Phase.Starting:
                        if (round.PreparedInRoom && !retry)
                        {
                            // The round's seat records (gspRoomSetSlot, copied whole by cn-324 sub_58DC10) overwrite the
                            // seat identity the waiting-room table wrote (cn-324 sub_6F9140 sets seat +80). Every movement
                            // datagram carries that identity (cn-324 sub_6F8530) and receivers drop unknown ones
                            // (cn-324 sub_6F92E0), so the table is installed again after the last seat record.
                            await SendTableAsync(peer, peers, send.Token).ConfigureAwait(false);
                            await _wire.StopDiscoveryAsync(peer.Game, send.Token).ConfigureAwait(false);
                        }
                        // cn-324 sub_6F8F20 and cn-406 sub_70F490 resend probes without clearing
                        // learned endpoints; only the later finish control commits relay membership.
                        await _wire.StartPunchAsync(peer.Game, send.Token).ConfigureAwait(false);
                        break;
                    case Phase.Finalizing:
                        await _wire.FinishPunchAsync(peer.Game, send.Token).ConfigureAwait(false);
                        break;
                    case Phase.Fallback:
                    case Phase.Suspended:
                        await ResetAsync(peer, send.Token).ConfigureAwait(false);
                        break;
                }
            }
            lock (_gate)
            {
                if (!Contains(round)) return;
                round.Phase = action switch
                {
                    Phase.Offering => Phase.Discovery, Phase.Publishing => Phase.Tables,
                    Phase.Refreshing => previous,
                    Phase.Starting => Phase.Punching, Phase.Finalizing => Phase.Ready, _ => action,
                };
                if (!retry && action != Phase.Refreshing)
                    round.Deadline = now + (action switch
                    {
                        Phase.Offering => _options.DiscoveryTimeoutMs, Phase.Publishing => _options.TableTimeoutMs,
                        Phase.Starting => _options.PunchWindowMs, _ => 0,
                    });
                if (action == Phase.Starting)
                    round.NextPunch = now + _options.PunchRetryMs;
                if (action is Phase.Offering or Phase.Refreshing)
                    round.NextDiscovery = now + _options.DiscoveryRefreshMs;
                if (action is Phase.Fallback or Phase.Suspended) foreach (var peer in round.Peers) peer.Direct.Clear();
            }
            if (action == Phase.Offering)
                _logger.LogInformation("Room {Room} UDP discovery offered in {Stage} to {Peers} peer(s).", round.RoomId, view.Stage, peers.Length);
            if (action == Phase.Prepared)
                _logger.LogInformation("Room {Room} UDP endpoint table acknowledged; awaiting the initialized playing round.", round.RoomId);
            if (action == Phase.Starting && !retry)
                _logger.LogInformation("Room {Room} UDP peer punching started after relay initialization.", round.RoomId);
            if (action == Phase.Starting && retry)
                _logger.LogDebug("Room {Room} retried native UDP punching within its original window.", round.RoomId);
            if (action == Phase.Fallback)
                _logger.LogWarning("Room {Room} UDP setup fell back from {Phase}: {Endpoints}/{Peers} endpoints, {Acks}/{Peers} table acknowledgments; retaining TCP relay delivery.",
                    round.RoomId, previous, peers.Count(peer => peer.Endpoint is not null), peers.Length, peers.Count(peer => peer.TableAcknowledged), peers.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {Room} UDP peer setup publication failed; resetting direct paths.", round.RoomId);
            lock (_gate)
            {
                round.Phase = Phase.Fallback;
                round.FallbackReason = $"sending a setup message failed ({ex.GetType().Name})";
                foreach (var peer in round.Peers) peer.Direct.Clear();
            }
            foreach (var peer in peers)
            {
                if (!Current(peer)) continue;
                using var reset = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reset.CancelAfter(_options.ControlTimeoutMs);
                try { await ResetAsync(peer, reset.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception failure)
                {
                    _logger.LogError(failure, "UDP peer reset failed for session {Session}; closing its inconsistent transport.", peer.Game.Id);
                    await peer.Game.Connection.CloseAsync("UDP peer setup reset failed").ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask ResetAsync(Peer peer, CancellationToken ct)
    {
        // cn-279 sub_702AC0 and cn-324 sub_6F7BE0 clear all old direct-path flags on a fresh offer.
        await _wire.OfferAsync(peer.Game, _port, peer.Identity, ct).ConfigureAwait(false);
        await _wire.StopDiscoveryAsync(peer.Game, ct).ConfigureAwait(false);
        await _wire.FinishPunchAsync(peer.Game, ct).ConfigureAwait(false);
    }

    private Peer? FindGame(ISession session)
    {
        var view = _roster.FindBySession(session.Id);
        if (view is null) return null;
        var generation = view.Stage == RoomStage.Waiting && _wire.DiscoveryInWaitingRoom
            ? (long)view.Generation + 1 : view.Generation;
        lock (_gate) return !_disposed && generation <= int.MaxValue
            && _rounds.TryGetValue((view.RoomId, (int)generation), out var round)
            ? round.Peers.SingleOrDefault(peer => ReferenceEquals(peer.Game, session)) : null;
    }

    private bool Current(Peer peer)
    {
        var view = _roster.Find(peer.Round.RoomId);
        lock (_gate)
            return Contains(peer) && !peer.Game.Connection.IsClosed && view is { IsMatchRoom: true } && Matches(peer.Round, view)
                && (view.Stage != RoomStage.Waiting || view.Seats.Count == peer.Round.Peers.Count
                    && view.Seats.All(seat => peer.Round.Peers.Any(value => value.Slot == seat.Slot && ReferenceEquals(value.Game, seat.Game))))
                && view.Seats.Any(seat => seat.Slot == peer.Slot && ReferenceEquals(seat.Game, peer.Game));
    }

    private static bool Matches(Round round, RoomRosterView view) =>
        (!round.PreparedInRoom || view.InstallationVersion == round.InstallationVersion) && (view.Stage switch
    {
        RoomStage.Waiting => round.PreparedInRoom && (long)view.Generation + 1 == round.Generation,
        RoomStage.Starting or RoomStage.Playing => view.Generation == round.Generation,
        _ => false,
    });

    private void TrackWaitingRooms()
    {
        foreach (var view in _roster.Snapshot())
        {
            if (view is not { Stage: RoomStage.Waiting, UsesUdp: true } || view.Seats.Count == 0
                || view.Generation == int.MaxValue || view.Seats.Any(seat => seat.Game.Connection.IsClosed)) continue;
            lock (_gate)
            {
                if (_disposed) return;
                var key = (view.RoomId, view.Generation + 1);
                if (_rounds.ContainsKey(key)) continue;
                if (_rounds.Count >= _options.MaxRounds || view.Seats.Count > _options.MaxPeers - _identities.Count)
                {
                    if (!_capacityReported)
                        _logger.LogError("UDP waiting-room preparation capacity reached; additional rooms retain TCP.");
                    _capacityReported = true;
                    continue;
                }
                _capacityReported = false;
                var round = new Round(view.RoomId, view.Generation + 1, preparedInRoom: true, view.InstallationVersion);
                try
                {
                    foreach (var seat in view.Seats)
                    {
                        var peer = new Peer(round, seat, NewIdentity());
                        round.Peers.Add(peer);
                        _identities.Add(peer.Identity, peer);
                    }
                    _rounds.Add(key, round);
                    _tracking.Refresh(() => _rounds.Count != 0);
                }
                catch
                {
                    foreach (var peer in round.Peers) _identities.Remove(peer.Identity);
                    throw;
                }
            }
        }
    }

    private async ValueTask SendTableAsync(Peer peer, Peer[] peers, CancellationToken ct)
    {
        foreach (var candidate in peers)
        {
            if (!Current(peer) || !Current(candidate)) continue;
            UdpPeerCandidate row;
            lock (_gate)
            {
                if (!Contains(peer) || !Contains(candidate) || peer.Endpoint is null || candidate.Endpoint is null) continue;
                var local = peer.Endpoint.Address.Equals(candidate.Endpoint.Address) && candidate.LocalAddress is not null;
                row = new(candidate.Slot, candidate.Identity, local ? [candidate.LocalAddress!] : [],
                    local ? candidate.LocalPort : (ushort)0, candidate.Endpoint);
            }
            await _wire.SendCandidateAsync(peer.Game, row, ct).ConfigureAwait(false);
        }
        await _wire.EndTableAsync(peer.Game, ct).ConfigureAwait(false);
    }

    private static bool CurrentRelay(Peer peer, RoomRosterView view) => peer.Relay is { Connection.IsClosed: false }
        && view.Seats.Any(seat => ReferenceEquals(seat.Game, peer.Game) && ReferenceEquals(seat.Relay, peer.Relay)
            && seat.RelayInitialized);

    private void Prune()
    {
        Round[] rounds;
        lock (_gate) rounds = _disposed ? [] : _rounds.Values.ToArray();
        foreach (var round in rounds)
        {
            Peer[] peers;
            lock (_gate) peers = round.Peers.ToArray();
            // A waiting-room table is one complete installation, never a surviving subset after a join or departure.
            var stale = peers.Where(peer => !Current(peer)).ToArray();
            if (round.PreparedInRoom && stale.Length != 0 && _roster.Find(round.RoomId)?.Stage == RoomStage.Waiting) stale = peers;
            lock (_gate)
            {
                foreach (var peer in stale)
                {
                    if (!Contains(peer)) continue;
                    _identities.Remove(peer.Identity);
                    round.Peers.Remove(peer);
                    foreach (var remaining in round.Peers) remaining.Direct.Remove(peer.Game.Id);
                }
                if (round.Peers.Count == 0 && _rounds.Remove((round.RoomId, round.Generation)))
                    _tracking.Refresh(() => _rounds.Count != 0);
            }
        }
    }

    private bool Contains(Round round) => !_disposed && _rounds.TryGetValue((round.RoomId, round.Generation), out var current)
        && ReferenceEquals(current, round);

    private bool Contains(Peer peer) => !_disposed && _identities.TryGetValue(peer.Identity, out var current)
        && ReferenceEquals(current, peer);

    private uint NewIdentity()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var identity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
            if (identity is not (0 or uint.MaxValue) && !_identities.ContainsKey(identity)) return identity;
        }
        throw new InvalidOperationException("Cannot allocate a unique native UDP peer identity.");
    }

    private static bool LocalCandidate(IPAddress address, IPAddress observed)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224) return false;
        return !IPAddress.IsLoopback(address) || IPAddress.IsLoopback(observed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var peer in _identities.Values) peer.Direct.Clear();
            _identities.Clear();
            _rounds.Clear();
            _tracking.Refresh(() => false);
        }
    }
}
