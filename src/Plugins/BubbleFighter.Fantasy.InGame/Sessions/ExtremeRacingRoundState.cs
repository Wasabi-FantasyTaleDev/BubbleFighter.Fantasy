namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Where an ExtremeRacing match stands.</summary>
internal enum ExtremeRacingPhase
{
    /// <summary>No race round has begun.</summary>
    Idle,

    /// <summary>A round's race clock runs and nobody has finished it.</summary>
    Running,

    /// <summary>Someone finished the round; the finish countdown runs.</summary>
    Finishing,

    /// <summary>The round is over and its points are awarded; the next round has not begun.</summary>
    Intermission,

    /// <summary>The last round is over; the match result follows.</summary>
    Over,
}

/// <summary>Whether a goal report counted, and whether it started the finish countdown.</summary>
internal enum ExtremeRacingFinish
{
    /// <summary>The report does not count: wrong phase or round, or the seat already finished.</summary>
    Rejected,

    /// <summary>The round's first finisher: the finish countdown starts.</summary>
    First,

    /// <summary>A later finisher.</summary>
    Later,
}

/// <summary>
/// One ExtremeRacing match's rounds, finishing order and points. Each transition names the round it acts on, so
/// a clock or a queued report for a round that has already moved on changes nothing. The room replaces the state
/// when a new match starts. Callers hold the room's gate.
/// </summary>
internal sealed class ExtremeRacingRoundState
{
    /// <summary>Shares terminal connection cleanup across failed publications in this match.</summary>
    internal Task? CleanupTask { get; set; }

    private readonly List<int> _finishers = [];
    private readonly Dictionary<int, int> _totals = [];
    private readonly Dictionary<int, int> _checkpoints = [];
    private readonly Dictionary<int, long> _roundResets = [];

    /// <summary>Gets the one-based round, or 0 before the first.</summary>
    internal int Round { get; private set; }

    /// <summary>Gets the phase.</summary>
    internal ExtremeRacingPhase Phase { get; private set; }

    /// <summary>Gets the monotonic start time used to keep the finish countdown inside the original race deadline.</summary>
    internal long StartedAt { get; private set; }

    /// <summary>Gets whether the current round's clocks still run.</summary>
    internal bool IsActive => Phase is ExtremeRacingPhase.Running or ExtremeRacingPhase.Finishing;

    /// <summary>Gets the seats that finished the current round, in finishing order.</summary>
    internal IReadOnlyList<int> Finishers => _finishers;

    /// <summary>Gets a seat's points over every closed round.</summary>
    internal int TotalOf(int slot) => _totals.GetValueOrDefault(slot);

    /// <summary>Gets whether a seat finished the current round.</summary>
    internal bool HasFinished(int slot) => _finishers.Contains(slot);

    /// <summary>Begins the round after the current one, from the idle or intermission phase.</summary>
    internal bool Begin(int round)
    {
        if (Phase is not (ExtremeRacingPhase.Idle or ExtremeRacingPhase.Intermission) || round != Round + 1)
            return false;
        Round = round;
        StartedAt = Environment.TickCount64;
        _finishers.Clear();
        _checkpoints.Clear();
        _roundResets.Clear();
        Phase = ExtremeRacingPhase.Running;
        return true;
    }

    /// <summary>Allows one native relocation request for a participant's current life when a later stage begins.</summary>
    internal void ArmRoundReset(int slot, long life) => _roundResets[slot] = life;

    /// <summary>Checks an unconsumed relocation for the same active stage and life.</summary>
    internal bool HasRoundReset(int slot, long life) => IsActive && Round > 1
        && _roundResets.TryGetValue(slot, out var expected) && expected == life;

    /// <summary>Consumes a stage relocation without changing ordinary death admission.</summary>
    internal void TakeRoundReset(int slot) => _roundResets.Remove(slot);

    /// <summary>Records a seated reporter's goal of the current round once, so the list never exceeds the room's seats.</summary>
    internal ExtremeRacingFinish Finish(int slot, int round)
    {
        if (!IsActive || round != Round || _finishers.Contains(slot))
            return ExtremeRacingFinish.Rejected;
        _finishers.Add(slot);
        if (Phase == ExtremeRacingPhase.Finishing)
            return ExtremeRacingFinish.Later;
        Phase = ExtremeRacingPhase.Finishing;
        return ExtremeRacingFinish.First;
    }

    /// <summary>Accepts only forward progress in the active round; callers authenticate the bounded room seat first.</summary>
    internal bool Checkpoint(int slot, int round, int marker)
    {
        if (!IsActive || round != Round || marker is < 0 or > 9 || HasFinished(slot)
            || _checkpoints.TryGetValue(slot, out var previous) && marker <= previous)
            return false;
        _checkpoints[slot] = marker;
        return true;
    }

    /// <summary>
    /// Closes the given round once, whichever clock or report closes it first: awards points by finishing place and
    /// moves to the intermission, or to the end after the last round. Returns each finisher's round points, or null
    /// when the round is no longer active.
    /// </summary>
    internal IReadOnlyDictionary<int, int>? Close(int round, IReadOnlyList<int> placePoints, int rounds)
    {
        if (!IsActive || round != Round)
            return null;
        var awarded = new Dictionary<int, int>();
        for (var place = 0; place < _finishers.Count; place++)
        {
            var slot = _finishers[place];
            var points = place < placePoints.Count ? placePoints[place] : 0;
            awarded[slot] = points;
            _totals[slot] = checked(TotalOf(slot) + points);
        }
        Phase = Round >= rounds ? ExtremeRacingPhase.Over : ExtremeRacingPhase.Intermission;
        return awarded;
    }
}
