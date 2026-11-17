using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;

namespace BubbleFighter.Fantasy.Lobby;

internal sealed partial class CoupleFlow
{
    private sealed class ProfileFeature : ICoupleProfile
    {
        private sealed record Snapshot(CoupleProfileState? Profile, CoupleSceneState? Scene);
        private Snapshot _current = new(null, null);
        public CoupleProfileState? Current => Volatile.Read(ref _current).Profile;
        public CoupleSceneState? Scene => Volatile.Read(ref _current).Scene;
        public CouplePartnerProfile? CapturePartner()
        {
            var snapshot = Volatile.Read(ref _current);
            return snapshot is { Profile: { } profile, Scene: { } scene } ? new(scene, profile) : null;
        }
        internal void Set(CoupleProfileState? value, CoupleSceneState? scene) =>
            Volatile.Write(ref _current, new(value, scene));
    }

    private sealed record ProfileRegistration(ISession Session, long UserNum, ProfileFeature Feature, IDisposable Handle)
    {
        public long PartnerUserNum { get; set; }
        internal volatile bool NeedsPublication;
    }
    private readonly Dictionary<long, ProfileRegistration> _profiles = [];
    // One coalesced wakeup, not one queued operation per disconnect; registrations never outlive their connections.
    private readonly Channel<byte> _presenceWake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false,
    });
    private bool _disposed;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private ValueTask RefreshAsync(ISession session, long userNum, CancellationToken ct, bool queueRetry = true) =>
        PublishProfilesAsync([new(session, userNum)], ct, queueRetry);

    private async ValueTask<PreparedProfile?> PrepareProfileAsync(ISession session, long userNum, CancellationToken ct)
    {
        ProfileRegistration registration;
        lock (_gate)
        {
            if (_disposed || session.Connection.IsClosed
                || !ReferenceEquals(online.FindByUserNum(checked((int)userNum)), session)) return null;
            // Login takeover can precede the replaced connection's close event.
            foreach (var old in _profiles.Values.Where(p => p.UserNum == userNum && p.Session.Connection.Id != session.Connection.Id).ToArray())
            {
                _profiles.Remove(old.Session.Connection.Id);
                old.Handle.Dispose();
            }
            if (!_profiles.TryGetValue(session.Connection.Id, out registration!))
            {
                var feature = new ProfileFeature();
                registration = new(session, userNum, feature, session.RegisterFeature<ICoupleProfile>(feature));
                _profiles.Add(session.Connection.Id, registration);
            }
            // A failed relationship or nickname read still needs the same bounded publication retry as a failed send.
            registration.NeedsPublication = true;
        }
        var relationship = await store.FindRelationshipAsync(Account(userNum), ct).ConfigureAwait(false);
        var partner = relationship is null ? 0 : relationship.First.UserNum == userNum
            ? relationship.Second.UserNum : relationship.First.UserNum;
        var name = partner == 0 ? "" : (await players.FindByUserNumAsync(partner, ct).ConfigureAwait(false))?.Nickname ?? "";
        var partnerOnline = partner != 0 && online.FindByUserNum(checked((int)partner)) is not null;
        CoupleProfileState? state = null;
        if (relationship is not null)
        {
            // A ring introduced after this client must not hide the persistent relationship or prevent login.
            // kr-1256 sub_5DB670 and kr-812 sub_881780 guard the display-item lookup before showing its name.
            var displayRing = rings is not null && rings.TryGetDisplayRing(relationship.RingItemId, out var mapped) ? mapped : 0;
            state = new(relationship.CreatedAt, displayRing, partnerOnline);
        }
        var scene = partner == 0 ? null : new CoupleSceneState(partner, name);
        bool sceneChanged;
        lock (_gate)
        {
            if (_disposed || session.Connection.IsClosed
                || !ReferenceEquals(online.FindByUserNum(checked((int)userNum)), session)
                || !_profiles.TryGetValue(session.Connection.Id, out var current)
                || !ReferenceEquals(current, registration)) return null;
            registration.PartnerUserNum = partner;
            sceneChanged = registration.Feature.Scene != scene;
            registration.Feature.Set(state, scene);
        }
        return new(session, userNum, state, scene, new(partnerOnline, partner, name), sceneChanged);
    }

    private async ValueTask SendProfileAsync(PreparedProfile prepared, CancellationToken ct)
    {
        if (send.Profile is not null)
            await send.Profile.SendAsync(prepared.Session, new(prepared.State), ct).ConfigureAwait(false);
        else if (publishStat is not null)
            await publishStat(prepared.Session, prepared.UserNum, ct).ConfigureAwait(false);
        // kr-1256 sub_9C1BD0 -> sub_5DC4E0, kr-812 sub_830730 -> sub_881C50: this flag selects online/offline text.
        await send.Partner.SendAsync(prepared.Session, prepared.Partner, ct).ConfigureAwait(false);
        lock (_gate)
            if (_profiles.TryGetValue(prepared.Session.Connection.Id, out var registration)
                && ReferenceEquals(registration.Session, prepared.Session))
                registration.NeedsPublication = false;
    }

    /// <summary>Releases session state synchronously and schedules a coalesced partner-presence refresh.</summary>
    public void ConnectionClosed(long connectionId)
    {
        RemoveConnection(connectionId);
        lock (_gate)
        {
            if (_profiles.Remove(connectionId, out var registration)) registration.Handle.Dispose();
        }
        _presenceWake.Writer.TryWrite(0);
    }

    /// <summary>Refreshes the remaining live partners after a disconnect without blocking the connection event bus.</summary>
    public async ValueTask SweepPresenceAsync(CancellationToken ct)
    {
        ProfileRegistration[] profiles;
        lock (_gate) profiles = [.. _profiles.Values];
        foreach (var registration in profiles)
        {
            var current = registration.Feature.Current;
            if (!registration.NeedsPublication && (current is null
                || current.PartnerOnline == (online.FindByUserNum(checked((int)registration.PartnerUserNum)) is not null))) continue;
            try
            {
                await RefreshAsync(registration.Session, registration.UserNum, ct, queueRetry: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Keep the dirty marker for the next lifecycle wakeup; retrying here would hot-loop a broken connection.
                (logger ?? NullLogger.Instance).LogError(error,
                    "Couple presence publication failed for session {Session}; continuing with other participants.", registration.Session.Id);
            }
        }
    }

    /// <summary>Runs the row-owned presence worker; repeated close events share one pending wakeup.</summary>
    public async ValueTask RunPresenceAsync(CancellationToken ct)
    {
        while (await _presenceWake.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_presenceWake.Reader.TryRead(out _)) { }
            await SweepPresenceAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes every feature registration and stops accepting presence wakeups when the lobby row unloads.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var registration in _profiles.Values) registration.Handle.Dispose();
            _profiles.Clear();
            _sent.Clear();
            _received.Clear();
            _presenceWake.Writer.TryComplete();
        }
    }
}
