using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Signals;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed class MigrationSource(MigrationSourceOptions options, IMigrationBroker broker, INodeLeaseSource leases,
    IAccountSessionHandoff handoffs, IOnlinePlayers online, IPartyDirectory? parties, IMessageSender<ChannelMoveOffered> offers,
    IHostReadiness? readiness, TimeProvider clock, ILogger logger, IMigrationTargetDirectory? targets = null, IMessageSender<ServerNotice>? notices = null) : IRemoteRoomMigration, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, Pending> _pending = [];
    // Present while a departure is pending; the clean-up loop sleeps on it. Refreshed by whoever holds _gate.
    private readonly PresenceSignal _departures = new();
    private bool _disposed;
    public string ClientBuild => options.ClientBuild;

    public ValueTask<RoomJoinRefusal?> BeginAsync(ISession session, RoomBrowserSelection selection, string password, CancellationToken cancellationToken) =>
        BeginPartyAsync(session, selection, password, MigrationKind.Room, cancellationToken);

    // A party leader moves first; once its offer is out every online member in an idle scene is offered the same
    // destination. Both families open a pending connection on any result-zero offer without having asked for one
    // (kr-812 sub_81AB20 -> sub_81B680, kr-1256 sub_9A6440), so a member's client follows without its own request.
    private async ValueTask<RoomJoinRefusal?> BeginPartyAsync(ISession session, RoomBrowserSelection selection, string password,
        MigrationKind kind, CancellationToken cancellationToken)
    {
        var (leader, followers) = CaptureFollowers(session, selection, kind);
        var refusal = await BeginCoreAsync(session, selection, password, kind, followers.Length > 0 ? leader : null,
            cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return refusal;
        foreach (var follower in followers)
        {
            RoomJoinRefusal? followed;
            try
            {
                followed = await BeginCoreAsync(follower, selection, password, kind, leader, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Party member connection {ConnectionId} could not follow its leader ({FailureType}).",
                    follower.Connection.Id, error.GetType().Name);
                continue;
            }
            if (followed is { } reason)
                logger.LogInformation("Party member connection {ConnectionId} stays on this node: {Refusal}.", follower.Connection.Id, reason);
        }
        return null;
    }

    private (int Leader, ISession[] Followers) CaptureFollowers(ISession session, RoomBrowserSelection selection, MigrationKind kind)
    {
        if (parties is null || !session.TryFeature<IPlayerSession>(out var player)
            || parties.PartyOf(player.Identity.UserNum) is not { Members.Count: > 1 } party
            || party.Leader.UserNum != player.Identity.UserNum) return (0, []);
        var followers = party.Members.Skip(1).Select(member => online.FindByUserNum(member.UserNum)).OfType<ISession>()
            .Where(member => !member.Connection.IsClosed && member.Profile.Id == ClientBuild
                && member.TryFeature<IPlayerSession>(out var follower) && follower.Phase is SessionPhase.MainMenu or SessionPhase.Plaza
                // A home return is a move to the members' own home, which the destination admits only for its own players.
                && (kind == MigrationKind.Room || follower.MigrationHome?.NodeId == selection.NodeId))
            .ToArray();
        return (player.Identity.UserNum, followers);
    }

    public async ValueTask ReturnHomeAsync(ISession session, CancellationToken cancellationToken)
    {
        if (!session.TryFeature<IPlayerSession>(out var player)
            || player.MigrationHome is not { } home || home.NodeId == leases.NodeId) return;
        var originalGeneration = player.Generation;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.OperationTimeoutMs);
            var target = await (targets ?? throw new InvalidOperationException("Home return has no target directory."))
                .ResolveAsync(home.NodeId, ClientBuild, deadline.Token).ConfigureAwait(false);
            if (target is not null && (target.NodeId != home.NodeId || target.Epoch <= 0))
                throw new InvalidDataException("Home lookup returned another destination or an invalid lease.");
            if (player.Generation != originalGeneration || session.Connection.IsClosed
                || !ReferenceEquals(online.FindByUserNum(player.Identity.UserNum), session))
                throw new InvalidOperationException("Home return belongs to a stale scene or connection.");
            // Each departure applies its own operation deadline; the lookup's must not cut the party's moves short.
            if (target is not null && await BeginPartyAsync(session,
                new(false, target.NodeId, target.Epoch, 0, Guid.Empty, true), "", MigrationKind.Home, cancellationToken).ConfigureAwait(false) is null)
                return;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (session.Connection.IsClosed || player.Phase == SessionPhase.Migrating) throw;
            logger.LogWarning("Home return for connection {ConnectionId} failed ({FailureType}).", session.Connection.Id, error.GetType().Name);
        }
        logger.LogWarning("Home return for connection {ConnectionId} was refused; the player remains on the visiting node.", session.Connection.Id);
        if (!options.HomeUnavailableNoticeEnabled) return;
        using var noticeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        noticeDeadline.CancelAfter(options.OperationTimeoutMs);
        await (notices ?? throw new InvalidOperationException("Home return has no native notice sender."))
            .SendAsync(session, new(options.HomeUnavailableNotice), noticeDeadline.Token).ConfigureAwait(false);
    }

    private async ValueTask<RoomJoinRefusal?> BeginCoreAsync(ISession session, RoomBrowserSelection selection, string password,
        MigrationKind kind, int? partyLeader, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.OperationTimeoutMs);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        Pending? pending = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (selection.Local || !selection.JoinAllowed || selection.NodeId == leases.NodeId || selection.Epoch <= 0
                || kind == MigrationKind.Room && (selection.RoomId <= 0 || selection.InstanceId == Guid.Empty) || readiness is { IsMaintenance: true }
                || session.Profile.Id != ClientBuild || session.Connection.IsClosed || leases.Current is not { } lease
                || !session.TryFeature<IPlayerSession>(out var player) || player.Identity.UserNum <= 0
                || player.LoginConnectionId != session.Connection.Id || player.Phase is not (SessionPhase.MainMenu or SessionPhase.Plaza)
                || !ReferenceEquals(online.FindByUserNum(player.Identity.UserNum), session)
                || !session.TryFeature<IAccountSession>(out var account) || account.UserNum != player.Identity.UserNum
                || _pending.ContainsKey(session.Connection.Id) || _pending.Count >= options.MaxMigrations)
                return RoomJoinRefusal.NoRoom;
            if (session.Connection.RemoteEndPoint is not IPEndPoint endpoint) return RoomJoinRefusal.NoRoom;
            var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
            if (address.AddressFamily != AddressFamily.InterNetwork) return RoomJoinRefusal.NoRoom;
            // A party travels unreserved: its leader captured the members under the scene lock, and a member whose party
            // changes meanwhile simply arrives alone. Anyone else must stay outside every party until the move ends.
            var solo = partyLeader is null ? parties?.TryReserveSolo(player.Identity.UserNum) : null;
            if (parties is not null && partyLeader is null && solo is null) return RoomJoinRefusal.NoRoom;
            pending = new(session, player, lease.Epoch, player.Phase, player.Generation, solo, clock.GetTimestamp(),
                new(Guid.NewGuid().ToString("N"), kind, leases.NodeId, selection.NodeId, selection.Epoch,
                    ClientBuild, player.Identity.UserNum, address, kind == MigrationKind.Room
                        ? new(selection.RoomId, selection.InstanceId, player.Identity.UserNum, password) : null)
                { SourceChannel = player.MigrationHome?.Channel ?? options.Channel, HomeNode = player.MigrationHome?.NodeId ?? leases.NodeId,
                    HomeScene = kind == MigrationKind.Home ? player.Phase : SessionPhase.MainMenu, PartyLeader = partyLeader }, TimeSpan.FromMilliseconds(options.OperationTimeoutMs));
            _pending.Add(session.Connection.Id, pending);
            _departures.Refresh(() => _pending.Count != 0);
            pending.Generation = player.MoveTo(SessionPhase.Migrating);
            RequireCurrent(pending, deadline.Token);
            if (!await account.IsHeldAsync(deadline.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Source account authority is unavailable.");
            RequireCurrent(pending, deadline.Token);
            // A lost reply may still have reserved a destination seat, so cancellation starts before the request.
            pending.Requested = true;
            var offer = await broker.RequestAsync(pending.Request, deadline.Token).ConfigureAwait(false);
            RequireCurrent(pending, deadline.Token);
            if (!offer.Accepted)
            {
                await ReleaseAsync(pending).ConfigureAwait(false);
                return offer.Refusal ?? RoomJoinRefusal.NoRoom;
            }
            if (offer.Ipv4!.AddressFamily != AddressFamily.InterNetwork || offer.Lifetime <= TimeSpan.Zero
                || offer.Lifetime > AccountHandoff.MaxLifetime)
                throw new InvalidDataException("The migration destination returned an invalid native offer.");
            var remaining = offer.Lifetime - clock.GetElapsedTime(pending.Created);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Destination arrival expired during preparation.");
            pending.Lifetime = TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, options.MigrationTimeoutMs));
            await handoffs.HandOffAsync(session, new(player.Identity.UserNum, pending.Request.MigrationId,
                leases.NodeId, selection.NodeId, pending.Lifetime), deadline.Token).ConfigureAwait(false);
            pending.HandedOff = true;
            RequireCurrent(pending, deadline.Token);
            await offers.SendAsync(session, new(offer.Ipv4, checked((ushort)offer.Port)), deadline.Token).ConfigureAwait(false);
            RequireCurrent(pending, deadline.Token);
            return null;
        }
        catch (Exception failure)
        {
            if (pending is not null)
            {
                try { await ReleaseAsync(pending).ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Native departure failed and cleanup requires retry.", failure, cleanup); }
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    private bool IsCurrent(Pending pending) => !pending.Session.Connection.IsClosed
        && pending.Epoch == leases.Current?.Epoch && ReferenceEquals(online.FindByUserNum(pending.Player.Identity.UserNum), pending.Session)
        && pending.Session.TryFeature<IPlayerSession>(out var player) && ReferenceEquals(player, pending.Player)
        && player.Phase == SessionPhase.Migrating && player.Generation == pending.Generation;

    private void RequireCurrent(Pending pending, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCurrent(pending) || clock.GetElapsedTime(pending.Created) >= pending.Lifetime)
            throw new InvalidOperationException("Source migration expired or its scene, connection or node lease changed.");
    }

    private async ValueTask ReleaseAsync(Pending pending)
    {
        pending.Retiring = true;
        using var deadline = new CancellationTokenSource(options.OperationTimeoutMs);
        // Cancellation precedes exact withdrawal. A destination already claiming the handoff wins atomically.
        if (pending.Requested)
        {
            await broker.CancelAsync(pending.Request.DestinationNode, pending.Request.MigrationId, deadline.Token).ConfigureAwait(false);
            pending.Requested = false;
        }
        if (!pending.Session.Connection.IsClosed)
        {
            var restored = pending.HandedOff
                ? await handoffs.TryWithdrawAsync(pending.Session, pending.Request.MigrationId, deadline.Token).ConfigureAwait(false)
                : await pending.Session.Feature<IAccountSession>().IsHeldAsync(deadline.Token).ConfigureAwait(false);
            if (restored && IsCurrent(pending) && !_disposed)
            {
                pending.HandedOff = false;
                pending.Player.MoveTo(pending.OriginalPhase);
            }
            else await pending.Session.Connection.CloseAsync("native migration source retired").ConfigureAwait(false);
        }
        pending.Solo?.Dispose();
        _pending.Remove(pending.Session.Connection.Id);
        _departures.Refresh(() => _pending.Count != 0);
    }

    internal async ValueTask SweepAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var pending in _pending.Values.ToArray())
            {
                if (!pending.Retiring && IsCurrent(pending) && clock.GetElapsedTime(pending.Created) < pending.Lifetime) continue;
                try { await ReleaseAsync(pending).ConfigureAwait(false); }
                catch (Exception error)
                {
                    logger.LogError("Native departure cleanup for connection {ConnectionId} failed ({FailureType}); retaining capacity for retry.",
                        pending.Session.Connection.Id, error.GetType().Name);
                }
            }
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask RunAsync(CancellationToken ct)
    {
        try { await ParkedLoop.RunAsync(TimeSpan.FromMilliseconds(options.CleanupIntervalMs), clock, () => _departures.WhenPresent, SweepAsync, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            List<Exception>? failures = null;
            foreach (var pending in _pending.Values.ToArray())
            {
                try { await ReleaseAsync(pending).ConfigureAwait(false); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException("Native departure unload could not release pending migrations.", failures);
        }
        finally { _gate.Release(); }
    }

    private sealed class Pending(ISession session, IPlayerSession player, long epoch, SessionPhase originalPhase,
        long generation, IDisposable? solo, long created, MigrationRequest request, TimeSpan lifetime)
    {
        internal ISession Session { get; } = session;
        internal IPlayerSession Player { get; } = player;
        internal long Epoch { get; } = epoch;
        internal SessionPhase OriginalPhase { get; } = originalPhase;
        internal long Generation { get; set; } = generation;
        internal IDisposable? Solo { get; } = solo;
        internal long Created { get; } = created;
        internal MigrationRequest Request { get; } = request;
        internal TimeSpan Lifetime { get; set; } = lifetime;
        internal bool Requested { get; set; }
        internal bool HandedOff { get; set; }
        internal bool Retiring { get; set; }
    }
}
