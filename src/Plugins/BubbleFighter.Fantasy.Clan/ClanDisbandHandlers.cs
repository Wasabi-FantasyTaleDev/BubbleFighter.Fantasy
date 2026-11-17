using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Disbanding a clan: the master schedules it (a disband date the home then states) or cancels it before the date,
/// and a periodic sweep deletes every clan whose date has passed. The sweep is one directory step, so nodes sweeping
/// together delete each clan once; this node then tells its connected ex-members with an empty home.
/// </summary>
internal sealed class ClanDisbandHandlers(
    IClanDirectory clans,
    ClanHub hub,
    IMessageSender<ClanDisbandAnswered> results,
    IMessageSender<ClanDisbandChanged> changed,
    IMessageSender<ClanHomeSent> home,
    TimeSpan delay,
    TimeProvider clock,
    ILogger logger)
{
    /// <summary>Schedules or cancels the disband of the master's clan and tells the members.</summary>
    public async ValueTask DisbandAsync(ISession session, ClanDisbandRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var row = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var clan = row is { Role: ClanRole.Master } ? await clans.FindAsync(row.ClanId, ct).ConfigureAwait(false) : null;
        DateTime? date = request.Kind switch
        {
            ClanDisbandRequested.Request when clan is { DisbandAt: null } => clock.GetUtcNow().UtcDateTime + delay,
            _ => null,
        };
        var applies = clan is not null && request.Kind switch
        {
            ClanDisbandRequested.Request => clan.DisbandAt is null,
            ClanDisbandRequested.Cancel => clan.DisbandAt is not null,
            _ => false,
        };
        var stored = applies && await clans.UpdateAsync(clan! with { DisbandAt = date }, ct).ConfigureAwait(false);
        logger.LogInformation("Clan connection {Id} user={User} asked for disband kind {Kind}: {Result}.", session.Id, caller.UserNum, request.Kind,
            stored ? (date is null ? "cancelled" : $"due {date:O}") : "refused");
        await results.SendAsync(session, new ClanDisbandAnswered(stored ? 1 : 0, request.Kind), ct).ConfigureAwait(false);
        if (stored)
            await hub.BroadcastAsync(await clans.ListMembersAsync(clan!.ClanId, ct).ConfigureAwait(false), changed,
                new ClanDisbandChanged(1, request.Kind), seatedOnly: true, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the clans due by <paramref name="now"/> and tells their connected ex-members; returns how many rows went.</summary>
    internal async ValueTask<int> SweepAsync(DateTime now, CancellationToken ct)
    {
        var rows = await clans.DeleteDueAsync(now, ct).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => row.ClanId))
            logger.LogInformation("Clan {Clan} disbanded with {Rows} rows.", group.Key, group.Count());
        await hub.BroadcastAsync(rows, home, new ClanHomeSent(false, 0, 0, "", null), seatedOnly: false, ct).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>Starts the periodic sweep; disposing the result stops it and waits for a sweep in progress.</summary>
    public IDisposable Start(TimeSpan interval)
    {
        var stop = new CancellationTokenSource();
        var timer = new PeriodicTimer(interval, clock);
        var loop = Task.Run(async () =>
        {
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await SweepAsync(clock.GetUtcNow().UtcDateTime, stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // One failed sweep (a store hiccup) must not end the schedule; the next tick tries again.
                        logger.LogError(e, "The clan disband sweep failed.");
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        });
        return new Stopper(stop, timer, loop);
    }

    private sealed class Stopper(CancellationTokenSource stop, PeriodicTimer timer, Task loop) : IDisposable
    {
        public void Dispose()
        {
            stop.Cancel();
            timer.Dispose();
            loop.GetAwaiter().GetResult();
            stop.Dispose();
        }
    }
}
