using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

internal sealed partial class FinalZoneItemLedger
{
    // kr-1256 sub_4FC800 sends specification 560 and consumes one item for each endpoint.
    internal const int BoobyTrapRodSpecId = 560;
    private readonly Dictionary<int, (int First, int? Second)> _pendingTraps = [];
    private readonly HashSet<int> _linkedRods = [];

    /// <summary>Debits an issued rod and allocates its endpoint only in the native first/second placement order.</summary>
    internal RoundFinalZoneTrapSpawned? TryPlaceTrap(RoundFinalZoneTrapRequested request)
    {
        if (!_initialized || request.ItemSpecId != BoobyTrapRodSpecId || request.Stage is not (1 or 3)
            || !_held.TryGetValue(request.Actor, out var inventory) || _closedInventories.Contains(request.Actor)
            || !inventory.TryGetValue(request.ItemSpecId, out var held) || _issued == _maximumObjects) return null;
        var pending = _pendingTraps.TryGetValue(request.Actor, out var pair);
        if (request.Stage == 1 ? pending : !pending || pair.Second is not null) return null;
        ValidatePosition(request.X, request.Y, request.Z);
        var id = checked(_firstObjectId + _issued);
        var item = new FinalZoneMapItem(id, request.ItemSpecId, 1, request.X, request.Y, request.Z,
            0, 0, 0, false, held.Reinforcement);
        Remove(inventory, held with { Quantity = 1 });
        _ground.Add(id, Freeze(item));
        _issued++;
        _pendingTraps[request.Actor] = request.Stage == 1 ? (id, null) : (pair.First, id);
        return new(id, request);
    }

    /// <summary>Accepts the owner's exact endpoint pair once, before the sender-inclusive native link is published.</summary>
    internal bool TryLinkTrap(RoundFinalZoneTrapLinked request)
    {
        if (_closedInventories.Contains(request.Actor) || !_pendingTraps.TryGetValue(request.Actor, out var pair)
            || pair.First != request.FirstObjectId || pair.Second != request.SecondObjectId
            || !_ground.ContainsKey(pair.First) || !_ground.ContainsKey(request.SecondObjectId)
            || !_specs.TryGetValue(BoobyTrapRodSpecId, out var spec)
            || unchecked((ushort)spec.WeaponId) != request.WeaponId) return false;
        _pendingTraps.Remove(request.Actor);
        _linkedRods.Add(pair.First);
        _linkedRods.Add(request.SecondObjectId);
        return true;
    }

    private void CancelTrapContaining(int objectId)
    {
        foreach (var actor in _pendingTraps.Where(entry => entry.Value.First == objectId
                     || entry.Value.Second == objectId).Select(entry => entry.Key).ToArray())
            _pendingTraps.Remove(actor);
    }
}
