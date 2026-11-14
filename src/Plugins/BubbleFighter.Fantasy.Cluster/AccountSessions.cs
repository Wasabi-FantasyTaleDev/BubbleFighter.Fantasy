using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Binds shared account occupancy to game-connection lifetime and verifies retained holds.</summary>
[Plugin("cluster.account-sessions", Category = "cluster", Names = ["en=Cluster account sessions", "zh-Hans=集群账号会话", "zh-Hant=叢集帳號工作階段", "ko=클러스터 계정 세션"], Description = "Bounded account admission and connection-owned authority verification.",
    Requires = [typeof(IAccountOwnershipStore)], Optional = [typeof(IAccountMutationScopes), typeof(IAccountHandoffStore)],
    Provides = [typeof(IAccountSessionAdmission)], ConditionalProvides = [typeof(IAccountSessionHandoff)])]
public sealed class AccountSessionsPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<AccountSessionOptions>();
        context.Services.TryGet<IAccountMutationScopes>(out var mutations);
        context.Services.TryGet<IAccountHandoffStore>(out var handoffs);
        AccountSessions? acquired = null;
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(
            acquired = new AccountSessions(context.Services.Get<IAccountOwnershipStore>(), options, context.Logger, mutations, handoffs)),
            cancellationToken).ConfigureAwait(false);
        var sessions = acquired ?? throw new InvalidOperationException("Account-session acquisition returned no resource.");
        context.Effect(context.Services.Register<IAccountSessionAdmission>(sessions));
        // Handoff is published only where the authority can record it; standalone nodes never offer migration.
        if (handoffs is not null) context.Effect(context.Services.Register<IAccountSessionHandoff>(sessions));
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => sessions.MarkClosed(e.Connection.Id)));
        await context.RunTaskAsync("account-session-verification", async ct =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.VerificationSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    await sessions.VerifyAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Bounds the verification work of account sessions; their number follows the game listeners' connection limits,
/// because a session is admitted only for an open connection and retires once its ownership is released.</summary>
public sealed class AccountSessionOptions
{
    /// <summary>Gets or sets the interval between retained-authority checks, in seconds.</summary>
    [Range(1, 60)]
    public int VerificationSeconds { get; set; } = 2;

    /// <summary>Gets or sets the maximum number of concurrent authority checks or connection retirements.</summary>
    [Range(1, 64)]
    public int VerificationConcurrency { get; set; } = 4;
}

internal sealed class AccountSessions(IAccountOwnershipStore store, AccountSessionOptions options, ILogger logger,
    IAccountMutationScopes? mutations = null, IAccountHandoffStore? handoffStore = null)
    : IAccountSessionAdmission, IAccountSessionHandoff, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    internal int RetainedSessions { get { lock (_gate) return _entries.Count; } }
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public ValueTask<bool> TryAdmitAsync(ISession session, long userNum, CancellationToken cancellationToken) =>
        AdmitAsync(session, userNum, ct => store.TryAcquireAsync(userNum, ct), cancellationToken);

    public ValueTask<bool> TryClaimAsync(ISession session, long userNum, string migrationId, CancellationToken cancellationToken)
    {
        var handoffs = handoffStore ?? throw new InvalidOperationException("Account handoff requires an IAccountHandoffStore provider.");
        if (!AccountHandoff.IsMigrationId(migrationId)) throw new ArgumentException("The migration identity is malformed.", nameof(migrationId));
        return AdmitAsync(session, userNum, ct => handoffs.TryClaimAsync(userNum, migrationId, ct), cancellationToken);
    }

    public async ValueTask HandOffAsync(ISession session, AccountHandoff handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        var handoffs = handoffStore ?? throw new InvalidOperationException("Account handoff requires an IAccountHandoffStore provider.");
        if (!handoff.IsValid()) throw new ArgumentException("The account handoff is malformed.", nameof(handoff));
        var entry = Owned(session);
        IAccountOwnership owner;
        lock (_gate)
        {
            if (entry.Closed || entry.Owner is not { } current || entry.HandoffId is not null || entry.UserNum != handoff.UserNum)
                throw new InvalidOperationException("Only an admitted connection that holds its account can hand it off.");
            // Marked before the hold is released so verification never mistakes the transfer for lost authority.
            entry.HandoffId = handoff.MigrationId;
            owner = current;
            entry.Owner = null;
        }
        entry.MutationFeature?.Dispose();
        entry.MutationFeature = null;
        try { await handoffs.HandOffAsync(owner, handoff, cancellationToken).ConfigureAwait(false); }
        catch (Exception error)
        {
            // No handoff was recorded, so this connection may take the account back the ordinary way.
            logger.LogWarning("Connection {ConnectionId} could not record its account handoff ({FailureType}).",
                session.Connection.Id, error.GetType().Name);
            if (!await RestoreAsync(entry, ct => store.TryAcquireAsync(entry.UserNum, ct)).ConfigureAwait(false))
                await session.Connection.CloseAsync("account authority lost during handoff").ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> TryWithdrawAsync(ISession session, string migrationId, CancellationToken cancellationToken)
    {
        var handoffs = handoffStore ?? throw new InvalidOperationException("Account handoff requires an IAccountHandoffStore provider.");
        var entry = Owned(session);
        lock (_gate)
            if (entry.Closed || entry.HandoffId != migrationId) return false;
        return await RestoreAsync(entry, ct => handoffs.TryWithdrawAsync(entry.UserNum, migrationId, ct), cancellationToken).ConfigureAwait(false);
    }

    private Entry Owned(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(session.Connection.Id, out var entry) || !ReferenceEquals(entry.Session, session) || !entry.Ready.Task.IsCompleted)
                throw new InvalidOperationException("The connection has no admitted account.");
            return entry;
        }
    }

    private async ValueTask<bool> RestoreAsync(Entry entry, Func<CancellationToken, ValueTask<IAccountOwnership?>> acquire,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var owner = await acquire(linked.Token).ConfigureAwait(false);
        if (owner is null) return false;
        lock (_gate)
        {
            if (!entry.Closed && !_disposed && !entry.Session.Connection.IsClosed)
            {
                entry.Owner = owner;
                entry.HandoffId = null;
                entry.MutationFeature = mutations?.Bind(entry.Session, owner);
                return true;
            }
        }
        await owner.DisposeAsync().ConfigureAwait(false);
        return false;
    }

    private async ValueTask<bool> AdmitAsync(ISession session, long userNum,
        Func<CancellationToken, ValueTask<IAccountOwnership?>> acquire, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        bool created;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (session.Connection.IsClosed) return false;
            if (_entries.TryGetValue(session.Connection.Id, out var existing))
            {
                if (!ReferenceEquals(existing.Session, session) || existing.UserNum != userNum)
                    throw new InvalidOperationException("A game connection cannot change its admitted account or session.");
                entry = existing;
                created = false;
            }
            else
            {
                entry = new Entry(this, session, userNum);
                _entries.Add(session.Connection.Id, entry);
                created = true;
            }
        }
        if (!created)
        {
            await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await entry.IsHeldAsync(cancellationToken).ConfigureAwait(false);
        }

        var accepted = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            var owner = await acquire(deadline.Token).ConfigureAwait(false);
            lock (_gate)
            {
                entry.Owner = owner;
                // Linked cancellation can lag while callbacks on the original token are draining.
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed)
                    throw new OperationCanceledException("Account admission was canceled by plugin unload.", _stop.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (owner is not null && !_disposed && !entry.Closed && !session.Connection.IsClosed)
                {
                    try
                    {
                        entry.MutationFeature = mutations?.Bind(session, owner);
                        entry.Feature = session.RegisterFeature<IAccountSession>(entry);
                        accepted = true;
                    }
                    catch (ObjectDisposedException) when (session is ISessionWithFeatures typed && typed.Features.IsClosed)
                    {
                        entry.Closed = true;
                    }
                }
            }
            return accepted;
        }
        finally
        {
            if (!accepted)
                lock (_gate) entry.Closed = true;
            entry.Ready.TrySetResult();
            if (!accepted) await RetireAsync(entry).ConfigureAwait(false);
        }
    }

    public ValueTask ReleaseAsync(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Entry? entry;
        lock (_gate)
        {
            entry = _entries.GetValueOrDefault(session.Connection.Id);
            if (entry is not null && !ReferenceEquals(entry.Session, session))
                throw new InvalidOperationException("A different session cannot release this connection's account authority.");
        }
        return entry is null ? ValueTask.CompletedTask : RetireAsync(entry);
    }

    internal void MarkClosed(long connectionId)
    {
        lock (_gate)
            if (_entries.TryGetValue(connectionId, out var entry)) entry.Closed = true;
    }

    internal async ValueTask VerifyAsync(CancellationToken cancellationToken)
    {
        Entry[] entries;
        lock (_gate) entries = [.. _entries.Values];
        await Parallel.ForEachAsync(entries, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.VerificationConcurrency,
        }, async (entry, ct) =>
        {
            await entry.Ready.Task.WaitAsync(ct).ConfigureAwait(false);
            bool ended, transferring;
            IAccountOwnership? observedOwner;
            lock (_gate)
            {
                ended = entry.Closed || entry.Session.Connection.IsClosed;
                transferring = entry.HandoffId is not null;
                observedOwner = entry.Owner;
            }
            if (!ended && transferring) return;
            if (!ended)
            {
                try { ended = !await entry.IsHeldAsync(ct).ConfigureAwait(false); }
                catch (Exception error) when (error is DbException or IOException or TimeoutException
                    || error is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("Account authority verification failed for connection {ConnectionId} ({FailureType}).",
                        entry.Session.Connection.Id, error.GetType().Name);
                    ended = true;
                }
            }
            if (!ended) return;
            lock (_gate)
                if (!entry.Closed && !entry.Session.Connection.IsClosed
                    && (entry.HandoffId is not null || !ReferenceEquals(entry.Owner, observedOwner))) return;
            if (!entry.Session.Connection.IsClosed)
            {
                logger.LogWarning("Connection {ConnectionId} lost its account authority; closing its authenticated session.", entry.Session.Connection.Id);
                await entry.Session.Connection.CloseAsync("account authority lost").ConfigureAwait(false);
            }
            await RetireAsync(entry).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[]? entries = null;
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (var entry in _entries.Values) entry.Closed = true;
                entries = [.. _entries.Values];
            }
        }
        if (entries is not null)
        {
            try
            {
                await DrainAsync(entries).ConfigureAwait(false);
                _drained.TrySetResult();
            }
            catch (Exception error) { _drained.TrySetException(error); }
        }
        await _drained.Task.ConfigureAwait(false);
    }

    private async ValueTask DrainAsync(Entry[] entries)
    {
        List<Exception>? errors = null;
        try { await _stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { (errors ??= []).Add(error); }
        foreach (var entry in entries)
        {
            try
            {
                if (!entry.Session.Connection.IsClosed)
                    await entry.Session.Connection.CloseAsync("account authority unloaded").ConfigureAwait(false);
                await RetireAsync(entry).ConfigureAwait(false);
            }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        _stop.Dispose();
        if (errors is not null) throw new AggregateException("Account sessions did not retire cleanly.", errors);
    }

    private async ValueTask RetireAsync(Entry entry)
    {
        lock (_gate) entry.Closed = true;
        await entry.Ready.Task.ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref entry.Retiring, 1, 0) == 0)
        {
            try
            {
                entry.MutationFeature?.Dispose();
                entry.Feature?.Dispose();
                if (entry.Owner is not null) await entry.Owner.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                    if (ReferenceEquals(_entries.GetValueOrDefault(entry.Session.Connection.Id), entry))
                        _entries.Remove(entry.Session.Connection.Id);
                entry.Retired.TrySetResult();
            }
            catch (Exception error) { entry.Retired.TrySetException(error); }
        }
        await entry.Retired.Task.ConfigureAwait(false);
    }

    private sealed class Entry(AccountSessions scope, ISession session, long userNum) : IAccountSession
    {
        internal ISession Session { get; } = session;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IAccountOwnership? Owner { get; set; }
        internal IDisposable? Feature { get; set; }
        internal IDisposable? MutationFeature { get; set; }
        internal bool Closed { get; set; }
        internal string? HandoffId { get; set; }
        internal int Retiring;

        public long UserNum { get; } = userNum;

        public async ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken)
        {
            IAccountOwnership owner;
            lock (scope._gate)
            {
                if (Closed || Session.Connection.IsClosed || Owner is not { } current) return false;
                owner = current;
            }
            try
            {
                var held = await owner.IsHeldAsync(cancellationToken).ConfigureAwait(false);
                lock (scope._gate)
                {
                    if (Closed || Session.Connection.IsClosed || !ReferenceEquals(Owner, owner)) return false;
                    if (held) return true;
                    if (HandoffId is null) Closed = true;
                    return false;
                }
            }
            catch
            {
                lock (scope._gate)
                    if (ReferenceEquals(Owner, owner) && HandoffId is null) Closed = true;
                throw;
            }
        }
    }
}
