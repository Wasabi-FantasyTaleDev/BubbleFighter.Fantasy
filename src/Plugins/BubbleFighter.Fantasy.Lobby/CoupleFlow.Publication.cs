using System.Runtime.ExceptionServices;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;

namespace BubbleFighter.Fantasy.Lobby;

internal sealed partial class CoupleFlow
{
    private sealed record ProfileDelivery(ISession Session, long UserNum,
        Func<CancellationToken, ValueTask>? Before = null, Func<CancellationToken, ValueTask>? After = null);

    private sealed record PreparedProfile(ISession Session, long UserNum, CoupleProfileState? State,
        CoupleSceneState? Scene, CouplePartnerPublished Partner, bool SceneChanged);

    private async ValueTask PublishProfilesAsync(IReadOnlyList<ProfileDelivery> deliveries, CancellationToken ct,
        bool queueRetry = true)
    {
        if (deliveries.Count is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(deliveries), "A couple publication has one or two participants.");
        List<Exception> failures = [];
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<(ProfileDelivery Delivery, PreparedProfile Profile)> prepared = [];
            foreach (var delivery in deliveries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await PrepareProfileAsync(delivery.Session, delivery.UserNum, ct).ConfigureAwait(false) is { } profile)
                        prepared.Add((delivery, profile));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { failures.Add(error); }
            }
            // Install both committed snapshots before any scene listener or socket can observe the change.
            foreach (var (_, profile) in prepared)
                if (profile.SceneChanged && IsPreparedCurrent(profile))
                    events?.Emit(new CoupleSceneChanged(profile.Session, profile.Scene));

            foreach (var (delivery, profile) in prepared)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsPreparedCurrent(profile)) continue;
                try
                {
                    // Breakup keeps reply-before-profile; acceptance and ring changes keep profile-before-reply.
                    if (delivery.Before is { } before) await before(ct).ConfigureAwait(false);
                    await SendProfileAsync(profile, ct).ConfigureAwait(false);
                    if (delivery.After is { } after) await after(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { failures.Add(error); }
            }
        }
        finally { _refresh.Release(); }

        // One coalesced retry is allowed after request publication fails. A failed presence sweep retains its dirty
        // marker for the next lifecycle wakeup instead of repeatedly retrying a broken peer on the worker itself.
        if (failures.Count > 0 && queueRetry) _presenceWake.Writer.TryWrite(0);
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Couple publication failed for multiple participants.", failures);
    }

    private bool IsPreparedCurrent(PreparedProfile profile)
    {
        lock (_gate)
            return !_disposed && !profile.Session.Connection.IsClosed
                && ReferenceEquals(online.FindByUserNum(checked((int)profile.UserNum)), profile.Session)
                && _profiles.TryGetValue(profile.Session.Connection.Id, out var registration)
                && ReferenceEquals(registration.Session, profile.Session)
                && registration.Feature.Current == profile.State && registration.Feature.Scene == profile.Scene;
    }
}
