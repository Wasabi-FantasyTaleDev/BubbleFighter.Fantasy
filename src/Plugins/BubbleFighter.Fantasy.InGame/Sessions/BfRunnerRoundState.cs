namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// One BFRunner round's race (round mode 21): the checkpoint region each seat last reached and the team whose runner
/// reached the goal first. Guarded by the room's gate; a new round starts with a fresh instance.
/// </summary>
internal sealed class BfRunnerRoundState
{
    private readonly Dictionary<int, int> _regions = [];

    /// <summary>Gets the team the host reported at the goal, or null while the race is still running.</summary>
    internal int? WinnerTeam { get; private set; }

    /// <summary>
    /// Records a seat's checkpoint region. A repeat of the region the seat already holds is not news: the client sends
    /// a region only when it differs from its last report (kr-812 sub_580E10), so a repeat is a duplicate.
    /// </summary>
    /// <returns>Whether the region is new for the seat and the race is still running.</returns>
    internal bool TryReach(int slot, int region)
    {
        if (WinnerTeam is not null || _regions.TryGetValue(slot, out var current) && current == region) return false;
        _regions[slot] = region;
        return true;
    }

    /// <summary>Records the first goal; later reports change nothing.</summary>
    /// <returns>Whether this report decided the race.</returns>
    internal bool TryFinish(int team)
    {
        if (WinnerTeam is not null) return false;
        WinnerTeam = team;
        return true;
    }
}
