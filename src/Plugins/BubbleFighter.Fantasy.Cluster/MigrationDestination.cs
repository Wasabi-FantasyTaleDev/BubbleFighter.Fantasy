using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Listeners;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Prepares destination room seats and cancels abandoned arrivals without authenticating a game socket.</summary>
[Plugin("cluster.migration-destination", Description = "Bounded destination preparation and expired arrival cleanup.",
    Requires = [typeof(IMigrationBroker), typeof(IMigrationArrivals), typeof(IMigrationArrivalCleanup), typeof(INodeLeaseSource)],
    Optional = [typeof(IAccountSessionHandoff)], OptionalMany = [typeof(IRoomAdmissionDirectory), typeof(IMigrationHomeAdmission), typeof(IListenerEndpoint)])]
public sealed class MigrationDestinationPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MigrationDestinationOptions>();
        if (!context.Services.TryGet<IAccountSessionHandoff>(out _))
            throw new ConfigException("Migration destination requires account-session handoff and a node-bound ownership store.");
        var lease = context.Services.Get<INodeLeaseSource>();
        if (lease.NodeId != options.NodeId) throw new ConfigException("Migration destination must use the heartbeat node_id.");
        ValidateListeners(options, context.Services.GetAll<IListenerEndpoint>());
        var admissions = context.Services.GetAll<IRoomAdmissionDirectory>();
        if (admissions.GroupBy(a => a.ClientBuild, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ConfigException("Migration destination requires at most one room admission directory per build.");
        var homes = context.Services.GetAll<IMigrationHomeAdmission>();
        if (homes.GroupBy(a => a.ClientBuild, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ConfigException("Migration destination requires at most one home scene directory per build.");
        var destination = new MigrationDestination(options, lease, context.Services.Get<IMigrationArrivals>(),
            context.Services.Get<IMigrationArrivalCleanup>(), admissions, Dns.GetHostAddressesAsync, context.Logger,
            homes);
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(destination), cancellationToken).ConfigureAwait(false);
        await context.RunTaskAsync("migration-arrival-cleanup", destination.RunAsync, cancellationToken).ConfigureAwait(false);
        context.Effect(context.Services.Get<IMigrationBroker>().Serve(destination.AcceptAsync, destination.CancelAsync));
    }

    internal static void ValidateListeners(MigrationDestinationOptions options, IReadOnlyList<IListenerEndpoint> listeners)
    {
        foreach (var endpoint in options.Endpoints)
        {
            var matches = listeners.Where(listener => listener.RowId == endpoint.ListenerId).ToArray();
            if (matches.Length != 1 || matches[0].Protocol != endpoint.ClientBuild + "/game")
                throw new ConfigException($"Migration endpoint '{endpoint.ClientBuild}' requires exactly one active game listener '{endpoint.ListenerId}' for that build.");
        }
    }
}

internal sealed class MigrationDestination(MigrationDestinationOptions options, INodeLeaseSource leases,
    IMigrationArrivals arrivals, IMigrationArrivalCleanup cleanup, IReadOnlyList<IRoomAdmissionDirectory> admissions,
    Func<string, CancellationToken, Task<IPAddress[]>> resolve, ILogger logger, IReadOnlyList<IMigrationHomeAdmission>? homes = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal async ValueTask<MigrationOffer> AcceptAsync(MigrationRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.OperationTimeoutMs);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!MigrationTransport.Valid(request) || request.DestinationNode != options.NodeId
                || leases.Current?.Epoch != request.DestinationEpoch) return Refused();
            var endpoint = options.Endpoints.SingleOrDefault(e => e.ClientBuild == request.ClientBuild);
            var directory = admissions.SingleOrDefault(a => a.ClientBuild == request.ClientBuild);
            if (endpoint is null || request.Kind == MigrationKind.Room && directory is null
                || request.Kind == MigrationKind.Home && (request.HomeNode != options.NodeId
                    || homes?.Count(admission => admission.ClientBuild == request.ClientBuild) != 1)) return Refused();
            IPAddress? address;
            try
            {
                address = (await resolve(endpoint.Host, deadline.Token).ConfigureAwait(false))
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (SocketException error)
            {
                logger.LogWarning("Migration endpoint DNS resolution failed for build {Build} ({FailureType}).", request.ClientBuild, error.GetType().Name);
                return Refused();
            }
            if (address is null) return Refused();
            RoomAdmissionReservation? reservation = null;
            var registered = false;
            try
            {
                if (request.Kind == MigrationKind.Room)
                {
                    var prepared = await directory!.PrepareAsync(request.Room!, deadline.Token).ConfigureAwait(false);
                    reservation = prepared.Reservation;
                    if (prepared.Refusal is { } refusal)
                    {
                        if (reservation is not null) throw new InvalidDataException("Room preparation returned both a seat and a refusal.");
                        return Refused(refusal);
                    }
                    if (reservation is null) return Refused();
                    if (reservation.Id == Guid.Empty || reservation.RoomId != request.Room!.RoomId
                        || reservation.InstanceId != request.Room.InstanceId || reservation.UserNumber != request.UserNum)
                        throw new InvalidDataException("Room preparation returned a reservation for another destination or account.");
                }
                deadline.Token.ThrowIfCancellationRequested();
                if (leases.Current?.Epoch != request.DestinationEpoch) return Refused();
                var lifetime = TimeSpan.FromMilliseconds(options.ArrivalLifetimeMs);
                try { arrivals.Expect(new(request, reservation, lifetime) { ListenerId = endpoint.ListenerId }); }
                catch (InvalidOperationException)
                {
                    logger.LogWarning("Migration {MigrationId} could not reserve arrival capacity.", request.MigrationId);
                    return Refused();
                }
                registered = true;
                return new(address, endpoint.Port, null, lifetime);
            }
            finally
            {
                if (!registered && reservation is not null)
                {
                    using var release = new CancellationTokenSource(options.OperationTimeoutMs);
                    await directory!.CancelAsync(reservation, release.Token).ConfigureAwait(false);
                }
            }
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask CancelAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (cleanup.Retire(id) is { } arrival) await ReleaseAsync(arrival, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask SweepAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var arrival in cleanup.Collect())
            {
                try { await ReleaseAsync(arrival, ct).ConfigureAwait(false); }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    logger.LogError("Migration {MigrationId} reservation cleanup failed ({FailureType}); retaining its capacity for retry.",
                        arrival.Request.MigrationId, error.GetType().Name);
                }
            }
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.CleanupIntervalMs));
        try { while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await SweepAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async ValueTask ReleaseAsync(MigrationArrival arrival, CancellationToken ct)
    {
        if (arrival.Reservation is { } reservation)
        {
            var directory = admissions.SingleOrDefault(a => a.ClientBuild == arrival.Request.ClientBuild)
                ?? throw new InvalidOperationException("An arrival reservation lost its admission directory.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.OperationTimeoutMs);
            await directory.CancelAsync(reservation, deadline.Token).ConfigureAwait(false);
        }
        cleanup.Complete(arrival);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception>? errors = null;
            using var deadline = new CancellationTokenSource(options.OperationTimeoutMs);
            foreach (var arrival in cleanup.Collect(drain: true))
            {
                try { await ReleaseAsync(arrival, deadline.Token).ConfigureAwait(false); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            if (errors is not null) throw new AggregateException("Migration destination could not release every pending reservation.", errors);
        }
        finally { _gate.Release(); }
    }

    private static MigrationOffer Refused(RoomJoinRefusal refusal = RoomJoinRefusal.NoRoom) => new(null, 0, refusal, TimeSpan.Zero);
}
