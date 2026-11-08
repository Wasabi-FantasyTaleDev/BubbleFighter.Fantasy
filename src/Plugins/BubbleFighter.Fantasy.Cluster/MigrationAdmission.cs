using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed class MigrationAdmission(MigrationAdmissionOptions options, IRetainedPlayerAdmission players, IMigrationArrivals arrivals, INodeLeaseSource leases,
    IAccountSessionHandoff handoff, IAccountSessionAdmission accounts, IRoomAdmissionDirectory rooms,
    IMessageSender<MoveIntoReplied> replies, IMessageSender<PendingConnectionPromoted> promotions,
    IMessageSender<PendingPromotionCompleted>? completions,
    IHostReadiness? readiness, TimeProvider clock, ILogger logger, IMigrationHomeAdmission? home = null, IPartyDirectory? parties = null, IMigrationRoomRecovery? recovery = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly Dictionary<int, PartyArrival> _leaders = [];
    private readonly Dictionary<int, List<PartyArrival>> _followers = [];
    private bool _disposed;

    internal async ValueTask MoveAsync(ISession session, MoveIntoRequested message, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.TimeoutMs);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        Pending? pending = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (readiness is { IsMaintenance: true } || session.Connection.IsClosed
                || message.RetainedPlayerId is <= 0 or > int.MaxValue || message.StageWord > 1
                || session.TryFeature<IPlayerSession>(out _) || session.TryFeature<IAccountSession>(out _)
                || _pending.ContainsKey(session.Connection.Id) || _pending.Count >= options.MaxMigrations)
                throw new InvalidOperationException("Native migration is not permitted for this connection.");
            var continuation = session.Feature<IInitialHandshakeContinuation>();
            var arrival = arrivals.TryConsume(session.Profile.Id, message.RetainedPlayerId, session.Connection)
                ?? throw new InvalidOperationException("No matching native migration arrival exists.");
            pending = new(session, arrival, clock.GetTimestamp());
            _pending.Add(session.Connection.Id, pending);
            var request = arrival.Request;
            if (!MigrationTransport.Valid(request) || request.Guest is not null
                || request.ClientBuild != rooms.ClientBuild || request.UserNum != message.RetainedPlayerId
                || !ValidIntent(arrival) || !options.Channels.Contains(request.SourceChannel) || !CurrentLease(request))
                throw new InvalidOperationException("The native arrival does not match an authorized shared-account move.");
            pending.Solo = parties?.TryReserveSolo(checked((int)request.UserNum));
            if (parties is not null && pending.Solo is null)
                throw new InvalidOperationException("The destination player cannot be reserved outside a party.");

            // Claim before resolvers: starter inventory and wallet resolution can write to the account.
            pending.AccountAttempted = true;
            if (!await handoff.TryClaimAsync(session, request.UserNum, request.MigrationId, deadline.Token).ConfigureAwait(false))
                throw new InvalidOperationException("The destination could not claim the account handoff.");
            _ = await players.InstallAsync(session, request.UserNum, deadline.Token).ConfigureAwait(false);
            pending.PlayerInstalled = true;
            var installed = session.Feature<IPlayerSession>();
            if (installed.Identity.UserNum != request.UserNum || installed.LoginConnectionId != session.Connection.Id
                || installed.Phase != SessionPhase.Authenticated)
                throw new InvalidOperationException("Retained player installation returned incompatible session state.");
            installed.SetMigrationHome(new(request.HomeNode, request.SourceChannel));
            RequireCurrent(pending, deadline.Token);
            if (completions is null)
            {
                // kr-812 sub_81C550 promotes the pending connection but clears +2428 on the connection that receives it,
                // and kr-812 sub_812AD0 refuses later requests, the move home among them, while that flag is clear. The
                // reply after it sets the flag again (kr-812 sub_81C6F0) and stores the channel on the now-current one.
                await promotions.SendAsync(session, new(), deadline.Token).ConfigureAwait(false);
                await replies.SendAsync(session, new(true, request.SourceChannel), deadline.Token).ConfigureAwait(false);
            }
            else
            {
                // kr-1256 sub_9A6BA0 acknowledges the promotion on this connection; the reply waits for the completion.
                await promotions.SendAsync(session, new(), deadline.Token).ConfigureAwait(false);
            }
            if (completions is null) await JoinAsync(pending, deadline.Token).ConfigureAwait(false);
            else pending.AwaitingAcknowledgement = true;
            RequireCurrent(pending, deadline.Token);
            continuation.ContinueWithoutVersionVerification();
            if (completions is null)
            {
                Complete(pending);
                await RebuildPartyAsync(pending, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            await FailAsync(session, pending, failure).ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask AcknowledgeAsync(ISession session, PendingPromotionAcknowledged message, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.TimeoutMs);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        Pending? pending = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (completions is null || !_pending.TryGetValue(session.Connection.Id, out var found)
                || !ReferenceEquals(found.Session, session))
                throw new InvalidOperationException("No native promotion is awaiting this acknowledgement.");
            pending = found;
            if (!pending.AwaitingAcknowledgement || pending.Retiring)
                throw new InvalidOperationException("The native promotion acknowledgement is no longer permitted.");
            pending.AwaitingAcknowledgement = false;
            RequireCurrent(pending, deadline.Token);
            if (!await session.Feature<IAccountSession>().IsHeldAsync(deadline.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Account authority was lost before native promotion completed.");
            // kr-1256 sub_9A6BA0 sends the acknowledgement before sub_9AC490 can install the destination room.
            // The completion (kr-1256 dispatcher sub_9D2FF0 case 10) clears +2504 on this connection and
            // kr-1256 sub_99D510 refuses later requests, the move home among them, while it is clear; the reply
            // after it (kr-1256 sub_9A6D30) sets the flag again and stores the channel on the now-current one.
            await completions.SendAsync(session, new(), deadline.Token).ConfigureAwait(false);
            await replies.SendAsync(session, new(true, pending.Arrival.Request.SourceChannel), deadline.Token).ConfigureAwait(false);
            await JoinAsync(pending, deadline.Token).ConfigureAwait(false);
            Complete(pending);
            await RebuildPartyAsync(pending, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await FailAsync(session, pending, failure).ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    private bool ValidIntent(MigrationArrival arrival)
    {
        var request = arrival.Request;
        if (request.Kind == MigrationKind.Home)
            return home is not null && request.HomeNode == leases.NodeId && request.Room is null && arrival.Reservation is null;
        return request.Kind == MigrationKind.Room && request.Room is { } room && arrival.Reservation is { } reservation
            && reservation.UserNumber == request.UserNum && reservation.RoomId == room.RoomId
            && reservation.InstanceId == room.InstanceId && room.UserNumber == request.UserNum;
    }

    private void Complete(Pending pending)
    {
        pending.Solo?.Dispose();
        pending.Solo = null;
        if (pending.RecoveryGeneration is not null && !pending.Retiring) pending.RecoveryReady = true;
        else _pending.Remove(pending.Session.Connection.Id);
    }

    // Each node keeps its parties in memory, so a party that moved is joined again here. The leader and its members
    // reconnect independently; whichever arrives second completes the join, and an unmatched arrival expires once
    // every arrival accepted with it would have.
    private async ValueTask RebuildPartyAsync(Pending pending, CancellationToken ct)
    {
        var request = pending.Arrival.Request;
        if (parties is null || request.PartyLeader is not { } leader) return;
        var arrival = new PartyArrival(pending.Session, clock.GetTimestamp(), pending.Arrival.Lifetime + TimeSpan.FromMilliseconds(options.TimeoutMs));
        if (leader == request.UserNum)
        {
            if (_followers.Remove(leader, out var waiting))
                foreach (var follower in waiting)
                    await RejoinAsync(pending.Session, follower.Session, ct).ConfigureAwait(false);
            if (_leaders.ContainsKey(leader) || _leaders.Count < options.MaxMigrations) _leaders[leader] = arrival;
            else logger.LogWarning("Arrived party leader {UserNum} cannot wait for members: the arrival table is full.", leader);
            return;
        }
        if (_leaders.TryGetValue(leader, out var arrived) && !arrived.Session.Connection.IsClosed)
        {
            await RejoinAsync(arrived.Session, pending.Session, ct).ConfigureAwait(false);
            return;
        }
        if (_followers.Values.Sum(list => list.Count) >= options.MaxMigrations)
        {
            logger.LogWarning("Arrived party member {UserNum} cannot wait for leader {Leader}: the arrival table is full.", request.UserNum, leader);
            return;
        }
        if (!_followers.TryGetValue(leader, out var list)) _followers[leader] = list = [];
        list.Add(arrival);
    }

    private async ValueTask RejoinAsync(ISession leader, ISession member, CancellationToken ct)
    {
        try
        {
            if (!await parties!.RejoinAsync(leader, member, ct).ConfigureAwait(false))
                logger.LogInformation("Connection {ConnectionId} arrived alone: its leader's party could not take it back.", member.Connection.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The member is already admitted; a party it cannot rejoin leaves it playing alone, never disconnected.
            logger.LogWarning("Connection {ConnectionId} could not rejoin its party ({FailureType}).", member.Connection.Id, error.GetType().Name);
        }
    }

    private void ExpirePartyArrivals()
    {
        foreach (var (leader, arrival) in _leaders.ToArray())
            if (Expired(arrival)) _leaders.Remove(leader);
        foreach (var (leader, list) in _followers.ToArray())
            if (list.RemoveAll(Expired) > 0 && list.Count == 0) _followers.Remove(leader);
        bool Expired(PartyArrival arrival) => arrival.Session.Connection.IsClosed || clock.GetElapsedTime(arrival.Created) >= arrival.Lifetime;
    }

    private bool CurrentLease(MigrationRequest request) => request.DestinationNode == leases.NodeId
        && leases.Current is { } lease && lease.Epoch == request.DestinationEpoch;

    private void RequireCurrent(Pending pending, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pending.Session.Connection.IsClosed || !CurrentLease(pending.Arrival.Request)
            || clock.GetElapsedTime(pending.Created) >= TimeSpan.FromMilliseconds(options.TimeoutMs))
            throw new InvalidOperationException("The native migration expired or its destination lease changed.");
    }

    private async ValueTask JoinAsync(Pending pending, CancellationToken ct)
    {
        RequireCurrent(pending, ct);
        var player = pending.Session.Feature<IPlayerSession>();
        if (player.Phase != SessionPhase.Authenticated || player.Identity.UserNum != pending.Arrival.Request.UserNum
            || player.LoginConnectionId != pending.Session.Connection.Id)
            throw new InvalidOperationException("Pending migration player state changed before room admission.");
        if (pending.Arrival.Request.Kind == MigrationKind.Home)
        {
            if (home is null || !await home.EnterAsync(pending.Session, pending.Arrival.Request.HomeScene, ct).ConfigureAwait(false)
                || player.Phase is not (SessionPhase.MainMenu or SessionPhase.Plaza))
                throw new InvalidOperationException("The home node could not install the returning player's idle scene.");
        }
        else
        {
            player.MoveTo(SessionPhase.MainMenu);
            if (!await rooms.JoinAsync(pending.Session, pending.Arrival.Reservation!, ct).ConfigureAwait(false))
            {
                await rooms.CancelAsync(pending.Arrival.Reservation!, ct).ConfigureAwait(false);
                pending.SeatReleased = true;
                if (recovery is null || !await recovery.InstallMenuAsync(pending.Session, ct).ConfigureAwait(false)
                    || player.Phase != SessionPhase.MainMenu)
                    throw new InvalidOperationException("The prepared room failed and no fallback scene could be installed.");
                pending.RecoveryGeneration = player.Generation;
            }
            else pending.SeatReleased = true;
        }
    }

    private async ValueTask FailAsync(ISession session, Pending? pending, Exception failure)
    {
        if (pending is not null) pending.Retiring = true;
        try
        {
            if (pending is null) await session.Connection.CloseAsync("native migration refused").ConfigureAwait(false);
            else await ReleaseAsync(pending).ConfigureAwait(false);
        }
        catch (Exception cleanup)
        {
            throw new AggregateException("Native migration failed and its cleanup requires retry.", failure, cleanup);
        }
    }

    private async ValueTask ReleaseAsync(Pending pending)
    {
        pending.Retiring = true;
        List<Exception>? failures = null;
        try { await pending.Session.Connection.CloseAsync("native migration did not complete").ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (!pending.SeatReleased && pending.Arrival.Reservation is { } reservation)
        {
            try
            {
                using var deadline = new CancellationTokenSource(options.TimeoutMs);
                await rooms.CancelAsync(reservation, deadline.Token).ConfigureAwait(false);
                pending.SeatReleased = true;
            }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (pending.PlayerInstalled)
        {
            try { players.Remove(pending.Session); pending.PlayerInstalled = false; }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (pending.AccountAttempted)
        {
            try { await accounts.ReleaseAsync(pending.Session).ConfigureAwait(false); pending.AccountAttempted = false; }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Native migration resources could not all retire.", failures);
        Complete(pending);
    }

    internal async ValueTask SweepAsync(CancellationToken ct)
    {
        var recoveries = new List<Pending>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ExpirePartyArrivals();
            foreach (var pending in _pending.Values.ToArray())
            {
                if (pending.RecoveryReady && !pending.Retiring)
                {
                    if (pending.RecoveryRunning) continue;
                    if (pending.Session.Connection.IsClosed || !pending.Session.TryFeature<IPlayerSession>(out var player)
                        || player.Generation != pending.RecoveryGeneration)
                        _pending.Remove(pending.Session.Connection.Id);
                    else if (!pending.Session.TryFeature<IInitialHandshakeContinuation>(out _))
                    {
                        pending.RecoveryRunning = true;
                        recoveries.Add(pending);
                    }
                    continue;
                }
                if (!pending.Retiring && !pending.Session.Connection.IsClosed && CurrentLease(pending.Arrival.Request)
                    && clock.GetElapsedTime(pending.Created) < TimeSpan.FromMilliseconds(options.TimeoutMs)) continue;
                try { await ReleaseAsync(pending).ConfigureAwait(false); }
                catch (Exception error)
                {
                    logger.LogError(error, "Migration cleanup for connection {ConnectionId} failed; retaining its capacity for retry.",
                        pending.Session.Connection.Id);
                }
            }
        }
        finally { _gate.Release(); }
        // Recovery capacity remains in _pending; no scene delay holds the admission gate.
        await Task.WhenAll(recoveries.Select(pending => ReturnAfterRecoveryAsync(pending, ct))).ConfigureAwait(false);
    }

    private async Task ReturnAfterRecoveryAsync(Pending pending, CancellationToken ct)
    {
        try { await recovery!.ReturnHomeAsync(pending.Session, pending.RecoveryGeneration!.Value, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Home return after room refusal failed for connection {ConnectionId}.", pending.Session.Connection.Id);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _pending.Remove(pending.Session.Connection.Id); }
            finally { _gate.Release(); }
        }
    }

    internal async ValueTask RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.CleanupIntervalMs), clock);
        try { while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await SweepAsync(ct).ConfigureAwait(false); }
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
                if (pending.RecoveryReady && !pending.Retiring)
                {
                    _pending.Remove(pending.Session.Connection.Id);
                    continue;
                }
                try { await ReleaseAsync(pending).ConfigureAwait(false); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException("Native migration unload could not release all pending arrivals.", failures);
        }
        finally { _gate.Release(); }
    }

    private sealed record PartyArrival(ISession Session, long Created, TimeSpan Lifetime);

    private sealed class Pending(ISession session, MigrationArrival arrival, long created)
    {
        internal ISession Session { get; } = session;
        internal MigrationArrival Arrival { get; } = arrival;
        internal long Created { get; } = created;
        internal IDisposable? Solo { get; set; }
        internal bool AccountAttempted { get; set; }
        internal bool PlayerInstalled { get; set; }
        internal bool SeatReleased { get; set; }
        internal bool AwaitingAcknowledgement { get; set; }
        internal bool Retiring { get; set; }
        internal long? RecoveryGeneration { get; set; }
        internal bool RecoveryReady { get; set; }
        internal bool RecoveryRunning { get; set; }
    }
}
