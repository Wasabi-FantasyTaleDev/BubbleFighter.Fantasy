using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>The room owns objective progress; a client reports only its own location, never a score or owner.</summary>
internal sealed class ControlPointRoundState(int nodeCount, int playableMask)
{
    // kr-1256 sub_CE0EE0 / kr-1256 sub_CE0A20: five ordered slots; the gauge for a stage with 1..5 regions is 120.
    internal const int FullGauge = 120, WinScore = 500;
    private sealed class Point
    {
        internal int Owner, Attacker, Progress;
    }
    private readonly Point[] _points = Enumerable.Range(0, nodeCount is >= 1 and <= 5 ? nodeCount :
        throw new ArgumentOutOfRangeException(nameof(nodeCount))).Select(_ => new Point()).ToArray();
    private readonly Dictionary<int, (long Session, int Team, int Node, long Life)> _members = [];
    private readonly Dictionary<int, long> _synced = [];
    private readonly int _playable = nodeCount is >= 1 and <= 5 && playableMask > 0 && (playableMask >> nodeCount) == 0
        ? playableMask : throw new ArgumentOutOfRangeException(nameof(playableMask));
    private readonly int[] _scores = new int[2];
    internal int[] Totals => [.. _scores];
    internal int? Winner { get; private set; }

    internal bool HasSnapshot(int slot, long session) => _synced.TryGetValue(slot, out var sent) && sent == session;
    internal void SnapshotSent(int slot, long session) => _synced[slot] = session;
    internal void InvalidateSnapshots() => _synced.Clear();

    internal bool Move(int slot, long session, int team, int node, long life)
    {
        if (Winner.HasValue || team is not (1 or 2) || node != -1 &&
            (node < 1 || node > _points.Length || (_playable & (1 << (node - 1))) == 0)) return false;
        if (node == -1) return _members.Remove(slot);
        var next = (session, team, node, life);
        if (_members.TryGetValue(slot, out var old) && next == old) return false;
        _members[slot] = next;
        return true;
    }

    internal bool RemoveAbsent(Func<int, long, int, long, bool> present)
    {
        var removed = false;
        foreach (var (slot, member) in _members.ToArray())
            if (!present(slot, member.Session, member.Team, member.Life)) removed |= _members.Remove(slot);
        return removed;
    }

    internal RoundControlPointsUpdated Snapshot() => new(_points.Select((p, i) =>
        new ControlPointView(p.Owner, p.Attacker, Seats(i + 1, 1), Seats(i + 1, 2),
            p.Attacker == 1 ? p.Progress : 0, p.Attacker == 2 ? p.Progress : 0)).ToArray());

    private int[] Seats(int node, int team) => [.. _members.Where(kv => kv.Value.Team == team && kv.Value.Node == node)
        .Select(kv => kv.Key).Order()];

    internal (bool NodesChanged, bool ScoreChanged) Tick(int captureUnits, int pointsPerOwnedNode)
    {
        if (Winner.HasValue) return default;
        Span<int> gains = stackalloc int[2];
        gains.Clear();
        var changed = false;
        for (var i = 0; i < _points.Length; i++)
        {
            var point = _points[i];
            var first = 0;
            var second = 0;
            foreach (var member in _members.Values)
                if (member.Node == i + 1) { if (member.Team == 1) first++; else second++; }
            var uncontested = first > 0 && second == 0 ? 1 : second > 0 && first == 0 ? 2 : 0;
            if (point.Owner != 0 && (point.Owner == 1 ? second : first) == 0)
                gains[point.Owner - 1] += pointsPerOwnedNode;
            if (first != 0 && second != 0) continue;
            if (uncontested == 0 || uncontested == point.Owner)
            {
                changed |= point.Attacker != 0;
                point.Attacker = point.Progress = 0;
                continue;
            }
            if (point.Attacker != uncontested) point.Progress = 0;
            point.Attacker = uncontested;
            point.Progress = Math.Min(FullGauge, point.Progress + captureUnits * (first + second));
            changed = true;
            if (point.Progress == FullGauge)
            {
                point.Owner = uncontested;
                point.Attacker = point.Progress = 0;
            }
        }
        var finish1 = _scores[0] + gains[0] >= WinScore;
        var finish2 = _scores[1] + gains[1] >= WinScore;
        if (finish1 || finish2)
        {
            // Compare the fraction of this interval each team needed. A simultaneous finish is a draw.
            var order = ((WinScore - _scores[0]) * gains[1]).CompareTo((WinScore - _scores[1]) * gains[0]);
            Winner = !finish2 ? 1 : !finish1 ? 2 : order < 0 ? 1 : order > 0 ? 2 : 0;
            // Stop both counters at the same instant; a team arriving later must not be shown as a co-winner.
            var winner = Winner == 2 ? 1 : 0;
            var left = WinScore - _scores[winner];
            for (var i = 0; i < gains.Length; i++) _scores[i] += gains[i] * left / gains[winner];
        }
        else for (var i = 0; i < gains.Length; i++) _scores[i] += gains[i];
        return (changed, gains[0] != 0 || gains[1] != 0);
    }
}
