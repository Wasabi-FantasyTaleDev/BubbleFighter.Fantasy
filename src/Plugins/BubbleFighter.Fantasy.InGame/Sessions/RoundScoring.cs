using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// One running round's score, owned by the room and mutated only under <see cref="PlayRoom.Gate"/>.
/// A build's client reports knock-downs and finishing blows; this turns them into the round's
/// counters with the rule the recorded two-team round shows: a knock-down counts for the reporter
/// (and is what its score is built from), a finishing blow counts as a kill for the reporter and
/// the victim's team total, and a player's score is its knock-downs plus twice its kills.
/// </summary>
internal sealed class RoundScoring
{
    private readonly int[] _kills;
    private readonly int[] _knockdowns;
    private readonly int[] _deaths;
    private readonly int[] _pvePoints;
    private readonly int[] _rescues;
    private readonly int[] _lucci;
    private readonly int[] _stars;
    private readonly int?[] _pendingVictims;
    private bool _countsStars;

    /// <summary>Creates empty counters for a room of <paramref name="maxSeats"/> seats.</summary>
    /// <param name="maxSeats">The seat cap; seats outside it are ignored.</param>
    internal RoundScoring(int maxSeats)
    {
        var seats = Math.Max(0, maxSeats);
        _kills = new int[seats];
        _knockdowns = new int[seats];
        _deaths = new int[seats];
        _pvePoints = new int[seats];
        _rescues = new int[seats];
        _lucci = new int[seats];
        _stars = new int[seats];
        _pendingVictims = new int?[seats];
    }

    /// <summary>Gets each team's confirmed total, indexed by the team number minus one.</summary>
    internal int[] TeamTotals { get; } = new int[2];

    /// <summary>Reads one seat's counters; a seat outside the cap reads as all zero.</summary>
    /// <param name="slot">The seat index.</param>
    internal RoundSeatStats Stats(int slot) => slot >= 0 && slot < _kills.Length
        ? new RoundSeatStats(slot, _kills[slot], _knockdowns[slot], Score(slot), _deaths[slot])
            { Rescues = _rescues[slot], Stars = _countsStars ? _stars[slot] : null }
        : new RoundSeatStats(slot, 0, 0, 0, 0);

    /// <summary>Reads every seat's counters.</summary>
    internal RoundSeatStats[] AllStats()
    {
        var rows = new RoundSeatStats[_kills.Length];
        for (var slot = 0; slot < rows.Length; slot++)
            rows[slot] = Stats(slot);
        return rows;
    }

    /// <summary>Remembers the reporter's knockdown target until a finishing report in this round consumes it.</summary>
    internal void RememberVictim(int reporterSlot, int victim)
    {
        if (IsSeat(reporterSlot)) _pendingVictims[reporterSlot] = victim;
    }

    /// <summary>Consumes the reporter's pending target, including when an explicit finishing target supersedes it.</summary>
    internal int? TakeVictim(int reporterSlot)
    {
        if (!IsSeat(reporterSlot)) return null;
        var victim = _pendingVictims[reporterSlot];
        _pendingVictims[reporterSlot] = null;
        return victim;
    }

    /// <summary>
    /// Counts one knock-down report. The victim's side does not move here: which counter a
    /// knock-down writes on the victim is not proven for this build, so nothing is written.
    /// </summary>
    /// <param name="reporterSlot">The seat whose client reported it.</param>
    /// <returns>The updated counters, or <see langword="null"/> when the reporter is not a seat of this room.</returns>
    internal RoundSeatStats? AddKnockdown(int reporterSlot)
    {
        if (!IsSeat(reporterSlot))
            return null;
        _knockdowns[reporterSlot]++;
        return Stats(reporterSlot);
    }

    /// <summary>Counts one finishing blow: a kill for the reporter, a point for its team and a death for the seated victim.</summary>
    /// <param name="reporterSlot">The seat whose client reported it.</param>
    /// <param name="team">The reporter's team, 1 or 2, or 0 when the seat has none.</param>
    /// <param name="victimSlot">The seat that died, or <see langword="null"/> when the victim is no seat of this room.</param>
    /// <returns>The updated counters, or <see langword="null"/> when the reporter is not a seat of this room.</returns>
    internal RoundSeatStats? AddKill(int reporterSlot, int team, int? victimSlot = null)
    {
        if (!IsSeat(reporterSlot))
            return null;
        _kills[reporterSlot]++;
        if (IsSeat(victimSlot))
            _deaths[victimSlot!.Value]++;
        if (team is 1 or 2)
            TeamTotals[team - 1]++;
        return Stats(reporterSlot);
    }

    // The personal score: knock-downs plus twice the kills (57 = 21 + 2*18), and the final scoreboard repeats the
    // same three counters.
    private int Score(int slot) => _knockdowns[slot] + 2 * _kills[slot] + 2 * _rescues[slot];

    internal RoundSeatStats? AddRescue(int slot)
    {
        if (!IsSeat(slot)) return null;
        _rescues[slot]++;
        return Stats(slot);
    }

    /// <summary>Reads the Lucci one seat collected from dropped items this round; a seat outside the cap reads zero.</summary>
    /// <param name="slot">The seat index.</param>
    internal int Lucci(int slot) => IsSeat(slot) ? _lucci[slot] : 0;

    /// <summary>Adds the value of one accepted Lucci pickup, never letting the seat's total pass <paramref name="cap"/>.</summary>
    /// <param name="slot">The seat that picked the item up; one outside the cap is ignored.</param>
    /// <param name="amount">The Lucci the item is worth.</param>
    /// <param name="cap">The most Lucci one seat may collect in a round.</param>
    internal void AddLucci(int slot, int amount, int cap)
    {
        if (!IsSeat(slot) || amount <= 0) return;
        _lucci[slot] = (int)Math.Min(cap, (long)_lucci[slot] + amount);
    }

    /// <summary>Gets whether this round counts stars; it does once a star report was accepted.</summary>
    internal bool CountsStars => _countsStars;

    /// <summary>Reads the stars one seat holds; a seat outside the cap reads zero.</summary>
    /// <param name="slot">The seat index.</param>
    internal int Stars(int slot) => IsSeat(slot) ? _stars[slot] : 0;

    /// <summary>
    /// Applies one star report: the seat's count moves by <paramref name="change"/> and stays within
    /// zero and <paramref name="cap"/>, since the client keeps the stars and only its reports reach the server.
    /// </summary>
    /// <param name="slot">The reporting seat; one outside the cap is ignored.</param>
    /// <param name="change">The signed change.</param>
    /// <param name="cap">The most stars one seat may hold.</param>
    /// <returns>The updated counters, or <see langword="null"/> when the seat is outside the cap.</returns>
    internal RoundSeatStats? AddStars(int slot, int change, int cap)
    {
        if (!IsSeat(slot)) return null;
        _countsStars = true;
        _stars[slot] = (int)Math.Clamp((long)_stars[slot] + change, 0, cap);
        return Stats(slot);
    }

    internal int PvePoints(int slot) => IsSeat(slot) ? _pvePoints[slot] : 0;

    internal void AddPvePoints(int slot, int amount)
    {
        if (!IsSeat(slot) || amount <= 0) throw new InvalidOperationException("Invalid cooperative point award.");
        _pvePoints[slot] = checked(_pvePoints[slot] + amount);
    }

    private bool IsSeat(int? slot) => slot is { } value && value >= 0 && value < _kills.Length;
}
