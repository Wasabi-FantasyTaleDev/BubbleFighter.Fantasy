using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// The server's proposed firing cadence for one room generation's cannons: each cannon is due one of its own
/// <c>INTERVAL</c> after a race round's clock starts and again one interval after each due time that was served or
/// skipped. This is a server policy, not a native one: no client code that reads <c>INTERVAL</c> has been found, so
/// whether it is a period is unproven, and <c>STAGE</c> is not consulted. A due cannon that still has a command
/// outstanding is skipped rather than queued, and a late due time is never made up with a burst, so the cadence cannot
/// accumulate work. The state is plain data driven by explicit millisecond times; callers hold the room's gate.
/// </summary>
internal sealed class ExtremeRacingCannonCadence(ExtremeRacingCannonSchedule schedule)
{
    private readonly long[] _due = new long[schedule.Cannons.Count];
    private readonly int[] _nextWeapon = new int[schedule.Cannons.Count];

    /// <summary>Gets whether the map has any cannon to fire.</summary>
    internal bool HasCannons => _due.Length > 0;

    /// <summary>Gets whether a race round is currently arming cannons.</summary>
    internal bool Active { get; private set; }

    /// <summary>Arms every cannon for a race round whose clock starts at <paramref name="now"/>.</summary>
    /// <param name="now">The time in milliseconds on the caller's monotonic clock.</param>
    internal void Begin(long now)
    {
        Active = true;
        for (var cannon = 0; cannon < _due.Length; cannon++)
            _due[cannon] = now + schedule.Cannons[cannon].IntervalMs;
    }

    /// <summary>Disarms every cannon, for example when the race round closes.</summary>
    internal void End() => Active = false;

    /// <summary>Gets the earliest due time among the cannons, or null when disarmed or the map has none.</summary>
    internal long? NextDue => Active && _due.Length > 0 ? _due.Min() : null;

    /// <summary>
    /// Takes the cannons that are due at <paramref name="now"/> and moves each of their due times one interval past
    /// <paramref name="now"/>. A cannon whose pair has a command outstanding yields nothing this time; the others yield
    /// the next of their listed weapons in file order, so a cannon with several weapons cycles through them.
    /// </summary>
    /// <param name="now">The time in milliseconds on the caller's monotonic clock.</param>
    /// <param name="isOutstanding">Whether a cannon number has a command that no shot has answered.</param>
    /// <returns>The cannon number and weapon of each command to issue, in cannon number order.</returns>
    internal IReadOnlyList<(int Cannon, int Weapon)> TakeDue(long now, Func<int, bool> isOutstanding)
    {
        var due = new List<(int, int)>();
        if (!Active) return due;
        for (var cannon = 0; cannon < _due.Length; cannon++)
        {
            if (_due[cannon] > now) continue;
            _due[cannon] = now + schedule.Cannons[cannon].IntervalMs;
            if (isOutstanding(cannon)) continue;
            var weapons = schedule.Cannons[cannon].Weapons;
            due.Add((cannon, weapons[_nextWeapon[cannon]].WeaponId));
            _nextWeapon[cannon] = (_nextWeapon[cannon] + 1) % weapons.Count;
        }
        return due;
    }
}
