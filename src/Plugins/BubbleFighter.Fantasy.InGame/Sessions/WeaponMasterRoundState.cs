using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>One weapon the round has decided to hand a seat; the round flow sends it.</summary>
/// <param name="Slot">The seat.</param>
/// <param name="Item">The drawn item id.</param>
/// <param name="Level">The seat's level when the weapon was drawn.</param>
/// <param name="Reason">Why the weapon changes.</param>
internal sealed record WeaponMasterAssignment(int Slot, int Item, int Level, WeaponMasterReason Reason);

/// <summary>
/// One WeaponMaster round's per-seat levels and weapons. Every seat starts at level 1 and an accepted score event
/// named in the rules raises its level. Like the client's own death counter (kr-1256 sub_4A73A0 counts, a new
/// slot-0 weapon through kr-1256 sub_4996C0 resets), each seat counts its deaths since its last weapon; a revival
/// request is granted only once that count has reached the client's tunable. The round never ends from here: levels
/// only decide the ranking. Decisions queue as assignments the round flow takes and sends. Callers hold the room's gate.
/// </summary>
internal sealed class WeaponMasterRoundState(WeaponMasterRules rules, Random random, int generation)
{
    private readonly Dictionary<int, SeatState> _seats = [];
    private readonly List<WeaponMasterAssignment> _pending = [];

    private sealed class SeatState
    {
        internal int Level = 1;
        internal int Experience;
        internal int DeathsSinceWeapon;
        internal long LastDeathLife = -1;
    }

    /// <summary>Gets the room's start generation this state belongs to, so a state of an older round is never reused.</summary>
    internal int Generation => generation;

    /// <summary>Reads a seat's one-based level; a seat the round has not seen is at level 1.</summary>
    internal int Level(int slot) => _seats.TryGetValue(slot, out var seat) ? seat.Level : 1;

    /// <summary>Gets the first seat that reached the last level, or null while nobody has.</summary>
    internal int? Winner { get; private set; }

    /// <summary>Gets a seat's level, experience and the two thresholds the client's gauge shows.</summary>
    internal RoundWeaponMasterProgress Progress(int slot)
    {
        var (level, experience) = _seats.TryGetValue(slot, out var seat) ? (seat.Level, seat.Experience) : (1, 0);
        return new(level, experience, rules.Threshold(level), rules.NextThreshold(level));
    }

    /// <summary>Reads how many deaths a seat has had since it was last handed a weapon.</summary>
    internal int DeathsSinceWeapon(int slot) => _seats.TryGetValue(slot, out var seat) ? seat.DeathsSinceWeapon : 0;

    /// <summary>Hands every listed seat its first weapon.</summary>
    internal void Begin(IEnumerable<int> slots)
    {
        foreach (var slot in slots)
        {
            var seat = _seats[slot] = new SeatState();
            Assign(slot, seat, WeaponMasterReason.Start);
        }
    }

    /// <summary>Records one accepted score event of a seat; a level-up hands it the new level's weapon.</summary>
    /// <param name="slot">The scoring seat.</param>
    /// <param name="kind">What the round credited.</param>
    internal void Score(int slot, WeaponMasterEvent kind)
    {
        var seat = Seat(slot);
        var points = rules.Points(kind);
        if (points == 0 || Winner is not null) return;
        seat.Experience = checked(seat.Experience + points);
        var level = rules.LevelFor(seat.Experience);
        if (level <= seat.Level) return;
        // Several thresholds crossed by one event still hand out one weapon, the new level's.
        seat.Level = level;
        Assign(slot, seat, WeaponMasterReason.LevelUp);
        if (level == rules.LevelCount) Winner = slot;
    }

    internal void Death(int slot, long life)
    {
        var seat = Seat(slot);
        if (seat.LastDeathLife == life) return;
        seat.LastDeathLife = life;
        seat.DeathsSinceWeapon++;
    }

    /// <summary>
    /// Answers a seat's revival request: when it has died the client's number of times since its last weapon, it is
    /// handed a new weapon of its current level.
    /// </summary>
    /// <param name="slot">The requesting seat.</param>
    /// <returns>Whether the request was granted.</returns>
    internal bool Revive(int slot)
    {
        var seat = Seat(slot);
        if (seat.DeathsSinceWeapon < rules.NoScoreDeathCount) return false;
        Assign(slot, seat, WeaponMasterReason.Revival);
        return true;
    }

    /// <summary>Takes the decisions made since the last call, oldest first.</summary>
    internal WeaponMasterAssignment[] TakePending()
    {
        var taken = _pending.ToArray();
        _pending.Clear();
        return taken;
    }

    private SeatState Seat(int slot)
    {
        if (_seats.TryGetValue(slot, out var seat)) return seat;
        // A seat that joined after the round began starts at level 1 with its first weapon.
        seat = _seats[slot] = new SeatState();
        Assign(slot, seat, WeaponMasterReason.Start);
        return seat;
    }

    // Every weapon goes to equipment slot 0, which resets the client's death counter; the server's count follows it.
    private void Assign(int slot, SeatState seat, WeaponMasterReason reason)
    {
        seat.DeathsSinceWeapon = 0;
        _pending.Add(new WeaponMasterAssignment(slot, rules.Draw(seat.Level, random), seat.Level, reason));
    }
}
