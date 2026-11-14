using System.Collections.Frozen;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>The native cell phase; an installed bomb can replace a still-burning flame.</summary>
internal enum BnBBombState { Empty, Installed, Flame }

/// <summary>Whether a report is invalid, needs publication, or only names already retired placements.</summary>
internal enum BnBExplosionResult { Rejected, Applied, Retired }

/// <summary>A placement generation paired with its bounded map cell.</summary>
internal readonly record struct BnBBombIdentity(int Generation, int GridIndex);

/// <summary>A value snapshot for admission decisions without exposing mutable round cells.</summary>
internal readonly record struct BnBCellSnapshot(BnBBlockState Block, BnBItemKind Item, BnBBombState Bomb,
    int Generation, int Actor, int Reach, long FuseAtMilliseconds, int LastExplosionEvent);

/// <summary>
/// Bounded native BnB state for one playable round. Callers serialize access and publish a modified clone before
/// replacing the committed state; actor/session, movement, life, round identity and publication are caller-owned.
/// </summary>
internal sealed class BnBRoundState
{
    private readonly BnBMapData _map;
    private readonly BnBRoundRules _rules;
    private readonly FrozenDictionary<int, int> _actors;
    private readonly Cell[] _cells;
    private readonly int[] _items;
    private long _lastMilliseconds;

    internal BnBRoundState(BnBMapData map, BnBRoundRules rules, int seed,
        IReadOnlyCollection<int> actors, int maximumActors)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumActors, 1);
        if (actors.Count == 0 || actors.Count > maximumActors || actors.Count > int.MaxValue / 3)
            throw new ArgumentException("BnB requires a nonempty, bounded participant set.", nameof(actors));
        rules.Validate();
        _map = map;
        _rules = rules;
        Seed = seed;
        var actorSlots = new Dictionary<int, int>();
        foreach (var actor in actors)
            if (!actorSlots.TryAdd(actor, actorSlots.Count)) throw new ArgumentException("BnB participant identities repeat.", nameof(actors));
        _actors = actorSlots.ToFrozenDictionary();
        _items = new int[actors.Count * 3];
        var initialItems = map.CreateInitialItems(seed);
        _cells = new Cell[map.CellCount];
        for (var index = 0; index < _cells.Length; index++)
            _cells[index] = new Cell
            {
                Block = map.InitialBlock(index), Item = initialItems[index], Generation = -1,
                Actor = -1, PlacementActor = -1, LastExplosionEvent = -1,
            };
    }

    private BnBRoundState(BnBRoundState source)
    {
        _map = source._map;
        _rules = source._rules;
        _actors = source._actors;
        _cells = (Cell[])source._cells.Clone();
        _items = (int[])source._items.Clone();
        _lastMilliseconds = source._lastMilliseconds;
        Seed = source.Seed;
        NextGeneration = source.NextGeneration;
        NextExplosionEvent = source.NextExplosionEvent;
    }

    internal int Seed { get; }
    internal int CellCount => _cells.Length;
    internal int NextGeneration { get; private set; }
    internal int NextExplosionEvent { get; private set; }
    internal BnBMapData Map => _map;
    internal BnBRoundRules Rules => _rules;
    internal BnBRoundState Clone() => new(this);
    internal bool ContainsActor(int actor) => _actors.ContainsKey(actor);

    /// <summary>Gets the next flame or block retirement time; fuse expiry alone does not mutate native cells.</summary>
    internal long? NextTimerDeadlineMilliseconds
    {
        get
        {
            long? next = null;
            foreach (var cell in _cells)
            {
                if (cell.Bomb == BnBBombState.Flame) Take(cell.FlameUntilMilliseconds);
                if (cell.Block == BnBBlockState.Destroying) Take(cell.BlockUntilMilliseconds);
            }
            return next;
            void Take(long value) { if (next is null || value < next.Value) next = value; }
        }
    }

    /// <summary>Gets the earliest installed bomb's reporting deadline with caller-configured transport grace.</summary>
    internal long? NextBombReportDeadlineMilliseconds(int reportGraceMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reportGraceMilliseconds);
        long? next = null;
        foreach (var cell in _cells)
            if (cell.Bomb == BnBBombState.Installed)
            {
                var value = Deadline(cell.FuseAtMilliseconds, reportGraceMilliseconds);
                if (next is null || value < next.Value) next = value;
            }
        return next;
    }

    /// <summary>Returns still-installed due roots in ascending grid order for this original participant.</summary>
    internal IReadOnlyList<BnBBombIdentity> DueBombs(int actor, long nowMilliseconds, int reportGraceMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reportGraceMilliseconds);
        var result = new List<BnBBombIdentity>();
        for (var index = 0; index < _cells.Length; index++)
        {
            ref var cell = ref _cells[index];
            if (cell.Bomb == BnBBombState.Installed && cell.Actor == actor
                && nowMilliseconds >= cell.FuseAtMilliseconds
                && nowMilliseconds - cell.FuseAtMilliseconds >= reportGraceMilliseconds)
                result.Add(new(cell.Generation, index));
        }
        return result;
    }

    internal BnBCellSnapshot GetCell(int gridIndex)
    {
        ref var cell = ref _cells[gridIndex];
        return new(cell.Block, cell.Item, cell.Bomb, cell.Generation, cell.Actor,
            cell.Reach, cell.FuseAtMilliseconds, cell.LastExplosionEvent);
    }

    internal int ItemCount(int actor, BnBItemKind kind)
    {
        if (kind is < BnBItemKind.Bomb or > BnBItemKind.Roller) throw new ArgumentOutOfRangeException(nameof(kind));
        return _actors.TryGetValue(actor, out var slot) ? _items[slot * 3 + (int)kind - 1] : 0;
    }

    internal int BombCapacity(int actor) => (int)Math.Min((long)ItemCount(actor, BnBItemKind.Bomb) + 1, _rules.MaximumBombCount);
    internal int ExplosionRange(int actor) => (int)Math.Min((long)ItemCount(actor, BnBItemKind.Fire) + 1, _rules.MaximumExplosionRange);
    internal float MovementSpeed(int actor) => _rules.BaseMovementSpeed
        + Math.Min(ItemCount(actor, BnBItemKind.Roller), _rules.MaximumSpeedLevel) * _rules.SpeedPerLevel;
    internal int ActiveBombCount(int actor) => _cells.Count(cell => cell.Bomb == BnBBombState.Installed && cell.Actor == actor);

    /// <summary>Distinguishes an exposed item from one already selected beneath an intact block.</summary>
    internal bool HasAvailableItem(int gridIndex) => _map.Contains(gridIndex)
        && _cells[gridIndex].Item != BnBItemKind.None
        && _cells[gridIndex].Block is BnBBlockState.Empty or BnBBlockState.Destroying;

    /// <summary>Checks server admission constraints in addition to the native occupied-cell rule.</summary>
    internal bool CanPlace(int actor, int gridIndex) => ContainsActor(actor) && _map.Contains(gridIndex)
        && _cells[gridIndex].Block is BnBBlockState.Empty or BnBBlockState.Destroying
        && _cells[gridIndex].Bomb != BnBBombState.Installed && ActiveBombCount(actor) < BombCapacity(actor);

    /// <summary>Snapshots the actor's reach and advances generation only when a placement is accepted.</summary>
    internal bool TryPlace(int actor, int gridIndex, long nowMilliseconds, out BnBBombIdentity identity)
    {
        AdvanceTimers(nowMilliseconds);
        identity = default;
        if (!CanPlace(actor, gridIndex)) return false;
        if (NextGeneration == int.MaxValue) throw new InvalidOperationException("BnB placement generation is exhausted.");
        var fuseAt = Deadline(nowMilliseconds, _rules.BombFuseMilliseconds);
        ref var cell = ref _cells[gridIndex];
        // kr-1256 sub_BFF340 rejects an installed bomb before consuming the generation and accepts flame cells.
        cell.Generation = NextGeneration++;
        cell.Bomb = BnBBombState.Installed;
        cell.Actor = cell.PlacementActor = actor;
        cell.Reach = ExplosionRange(actor);
        cell.ReachValid = true;
        cell.FuseAtMilliseconds = fuseAt;
        cell.FlameUntilMilliseconds = 0;
        identity = new(cell.Generation, gridIndex);
        return true;
    }

    /// <summary>
    /// Validates the complete report before applying its roots in order. A retained generation owned by this
    /// actor may acknowledge an already chained or expired bomb; a new installed bomb must have reached its fuse.
    /// </summary>
    internal BnBExplosionResult TryExplode(int actor, IReadOnlyList<BnBBombIdentity> bombs, long nowMilliseconds,
        int earlyToleranceMilliseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(bombs);
        ArgumentOutOfRangeException.ThrowIfNegative(earlyToleranceMilliseconds);
        AdvanceTimers(nowMilliseconds);
        if (!ContainsActor(actor) || bombs.Count == 0 || bombs.Count > _cells.Length) return BnBExplosionResult.Rejected;
        var roots = bombs.ToArray();
        var seen = new HashSet<int>();
        var hasInstalledRoot = false;
        foreach (var root in roots)
        {
            if (!_map.Contains(root.GridIndex) || root.Generation < 0 || !seen.Add(root.GridIndex)) return BnBExplosionResult.Rejected;
            ref var cell = ref _cells[root.GridIndex];
            if (cell.Generation != root.Generation || cell.PlacementActor != actor) return BnBExplosionResult.Rejected;
            if (cell.Bomb == BnBBombState.Installed && cell.FuseAtMilliseconds - nowMilliseconds > earlyToleranceMilliseconds)
                return BnBExplosionResult.Rejected;
            hasInstalledRoot |= cell.Bomb == BnBBombState.Installed;
        }
        if (!hasInstalledRoot) return BnBExplosionResult.Retired;
        if ((long)NextExplosionEvent + roots.Length > int.MaxValue)
            throw new InvalidOperationException("BnB explosion event sequence is exhausted.");
        var flameUntil = Deadline(nowMilliseconds, _rules.FlameMilliseconds);
        var blockUntil = Deadline(nowMilliseconds, _rules.BlockDestructionMilliseconds);
        foreach (var root in roots) Explode(root.GridIndex, flameUntil, blockUntil);
        return BnBExplosionResult.Applied;
    }

    /// <summary>Removes an exposed item once; false plus an empty bounded cell permits a caller-owned empty acknowledgement.</summary>
    internal bool TryPickup(int actor, int gridIndex, long nowMilliseconds, out BnBItemKind item)
    {
        AdvanceTimers(nowMilliseconds);
        item = BnBItemKind.None;
        if (!ContainsActor(actor) || !HasAvailableItem(gridIndex)) return false;
        ref var cell = ref _cells[gridIndex];
        item = cell.Item;
        var slot = _actors[actor] * 3 + (int)item - 1;
        _items[slot] = checked(_items[slot] + 1);
        // kr-1256 sub_6FC240 consumes the cell; kr-1256 sub_C012C0 stores uncapped counts for ability clamps.
        cell.Item = BnBItemKind.None;
        return true;
    }

    /// <summary>Expires flames and destroyed blocks without inventing an explosion before its echoed report.</summary>
    internal void AdvanceTimers(long nowMilliseconds)
    {
        if (nowMilliseconds < _lastMilliseconds) throw new ArgumentOutOfRangeException(nameof(nowMilliseconds), "BnB clock must be monotonic.");
        _lastMilliseconds = nowMilliseconds;
        // kr-1256 sub_BFEC20 clears these independent timers; kr-1256 sub_B440C0 only reports due fuses.
        for (var index = 0; index < _cells.Length; index++)
        {
            ref var cell = ref _cells[index];
            if (cell.Bomb == BnBBombState.Flame && nowMilliseconds >= cell.FlameUntilMilliseconds)
            {
                cell.Bomb = BnBBombState.Empty;
                cell.Actor = -1;
                cell.FuseAtMilliseconds = cell.FlameUntilMilliseconds = 0;
            }
            if (cell.Block == BnBBlockState.Destroying && nowMilliseconds >= cell.BlockUntilMilliseconds)
            {
                cell.Block = BnBBlockState.Empty;
                cell.BlockUntilMilliseconds = 0;
            }
        }
    }

    private void Explode(int root, long flameUntil, long blockUntil)
    {
        var affected = new bool[_cells.Length];
        var triggered = new bool[_cells.Length];
        var pending = new int[_cells.Length];
        var count = 0;
        if (_cells[root].ReachValid)
        {
            triggered[root] = affected[root] = true;
            pending[count++] = root;
        }
        // kr-1256 sub_C000E0 resolves the complete chain before kr-1256 sub_BFF540 changes any blockers.
        while (count > 0)
        {
            var current = pending[--count];
            var row = current / _map.Width;
            var column = current % _map.Width;
            var reach = _cells[current].Reach;
            Ray(current, _map.Width, Math.Min(reach, _map.Height - row - 1));
            Ray(current, -_map.Width, Math.Min(reach, row));
            Ray(current, 1, Math.Min(reach, _map.Width - column - 1));
            Ray(current, -1, Math.Min(reach, column));
        }
        for (var index = 0; index < _cells.Length; index++)
        {
            if (!affected[index]) continue;
            ref var cell = ref _cells[index];
            cell.LastExplosionEvent = NextExplosionEvent;
            cell.FuseAtMilliseconds = 0;
            cell.FlameUntilMilliseconds = flameUntil;
            cell.ReachValid = false;
            cell.Bomb = BnBBombState.Flame;
            // kr-1256 sub_BFF540 exposes a destructible cell's preselected item; any later operation removes it.
            if (cell.Block == BnBBlockState.Destructible)
            {
                cell.Block = BnBBlockState.Destroying;
                cell.BlockUntilMilliseconds = blockUntil;
            }
            else cell.Item = BnBItemKind.None;
        }
        // kr-1256 sub_BFFC60 advances even when the generation matched a root with no valid reach remaining.
        NextExplosionEvent++;
        return;

        void Ray(int origin, int step, int distance)
        {
            var index = origin;
            for (var offset = 0; offset < distance; offset++)
            {
                index += step;
                affected[index] = true;
                ref var cell = ref _cells[index];
                if (cell.Block is BnBBlockState.Destructible or BnBBlockState.Solid) break;
                if (!triggered[index] && cell.Bomb == BnBBombState.Installed && cell.ReachValid)
                {
                    triggered[index] = true;
                    pending[count++] = index;
                }
            }
        }
    }

    private static long Deadline(long now, int duration) => checked(now + duration);

    private struct Cell
    {
        internal BnBBlockState Block;
        internal BnBItemKind Item;
        internal BnBBombState Bomb;
        internal int Generation;
        internal int Actor;
        internal int PlacementActor;
        internal int Reach;
        internal bool ReachValid;
        internal long FuseAtMilliseconds;
        internal long FlameUntilMilliseconds;
        internal long BlockUntilMilliseconds;
        internal int LastExplosionEvent;
    }
}
