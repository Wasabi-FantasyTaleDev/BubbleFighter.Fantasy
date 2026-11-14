using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>A room's own ban list, set by its host; it lives only as long as the room.</summary>
/// <param name="Items">Banned item ids.</param>
/// <param name="Groups">Banned group names.</param>
internal sealed record BanPickRoomList(IReadOnlySet<int> Items, IReadOnlySet<string> Groups)
{
    internal static BanPickRoomList Empty { get; } = new(new HashSet<int>(), new HashSet<string>(StringComparer.Ordinal));

    internal int Count => Items.Count + Groups.Count;
}

/// <summary>Where a check runs: the build and, inside a room, its mode and password.</summary>
/// <param name="Build">The client build.</param>
/// <param name="ModeId">The room mode, or null outside a room.</param>
/// <param name="HasPassword">Whether the room has a password; false outside a room.</param>
internal sealed record BanPickPlace(string Build, int? ModeId, bool HasPassword);

/// <summary>One worn item a rule names, with the strongest rule that names it.</summary>
/// <param name="ItemId">The worn item.</param>
/// <param name="Type">The strongest rule type naming it.</param>
/// <param name="Policy">The server policy id, or null for the room's own list.</param>
internal sealed record BanPickHit(int ItemId, BanPickRuleType Type, string? Policy);

/// <summary>The hits of one seat.</summary>
/// <param name="Seat">The seat.</param>
/// <param name="Hits">Its banned or restricted worn items, in wearing order.</param>
internal sealed record BanPickSeatHits(RoomPolicySeat Seat, IReadOnlyList<BanPickHit> Hits);

/// <summary>
/// Applies the server policies and a room's list to worn items. When several rules name one item the strongest wins
/// (block, then confirm, then warn); a room ban has the strength of a confirm rule.
/// </summary>
internal static class BanPickEvaluator
{
    /// <summary>Whether a policy scope applies at a place. Outside a room only an unconditional scope applies.</summary>
    internal static bool Applies(BanPickScope scope, BanPickPlace place)
    {
        if (scope.Builds.Count > 0 && !scope.Builds.Contains(place.Build, StringComparer.Ordinal)) return false;
        if (place.ModeId is not { } mode) return scope.Unconditional;
        return (scope.Modes.Count == 0 || scope.Modes.Contains(mode)) && BanPickRoomScopes.Matches(scope.Rooms, place.HasPassword);
    }

    /// <summary>The strongest rule naming one item at a place, or null.</summary>
    internal static BanPickHit? Check(BanPickPolicy policy, BanPickItemCatalog catalog, BanPickPlace place, BanPickRoomList room, int itemId)
    {
        BanPickHit? best = null;
        var groups = policy.Groups.ToDictionary(group => group.Name, StringComparer.Ordinal);
        foreach (var entry in policy.Policies)
        {
            if (!Applies(entry.Scope, place)) continue;
            foreach (var rule in entry.Rules)
            {
                if (!rule.Items.Contains(itemId) && !rule.Groups.Any(name => catalog.InGroup(itemId, groups[name]))) continue;
                var type = BanPickRuleTypes.Get(rule.Type);
                if (best is null || type.Strength > best.Type.Strength) best = new(itemId, type, entry.Id);
            }
        }
        if (place.ModeId is not null && (room.Items.Contains(itemId) || room.Groups.Any(name => groups.TryGetValue(name, out var group)
            && catalog.InGroup(itemId, group))))
        {
            var confirm = BanPickRuleTypes.Get(BanPickRuleTypes.Confirm);
            if (best is null || confirm.Strength > best.Type.Strength) best = new(itemId, confirm, null);
        }
        return best;
    }

    /// <summary>Every seat that wears at least one named item, in seat order.</summary>
    internal static IReadOnlyList<BanPickSeatHits> Evaluate(BanPickPolicy policy, BanPickItemCatalog catalog, string build,
        RoomPolicyRoom room, BanPickRoomList list)
    {
        var place = new BanPickPlace(build, room.ModeId, room.HasPassword);
        var result = new List<BanPickSeatHits>();
        foreach (var seat in room.Seats.OrderBy(seat => seat.Slot))
        {
            var hits = Worn(seat).Select(item => Check(policy, catalog, place, list, item)).OfType<BanPickHit>().ToArray();
            if (hits.Length > 0) result.Add(new(seat, hits));
        }
        return result;
    }

    /// <summary>The character and every worn item of a seat, each once.</summary>
    internal static IEnumerable<int> Worn(RoomPolicySeat seat) =>
        (seat.CharacterId > 0 ? seat.EquippedItemIds.Prepend(seat.CharacterId) : seat.EquippedItemIds).Where(id => id > 0).Distinct();

    /// <summary>Keeps only the hits of the given strength class.</summary>
    internal static IReadOnlyList<BanPickSeatHits> Where(IReadOnlyList<BanPickSeatHits> seats, Func<BanPickRuleType, bool> select) =>
        [.. seats.Select(seat => seat with { Hits = [.. seat.Hits.Where(hit => select(hit.Type))] }).Where(seat => seat.Hits.Count > 0)];

    /// <summary>A stable identity of a set of hits, so a confirmation only covers the exact items it was asked for.</summary>
    internal static string Signature(IReadOnlyList<BanPickSeatHits> seats) => string.Join(';', seats
        .OrderBy(seat => seat.Seat.SessionId)
        .Select(seat => seat.Seat.SessionId + ":" + string.Join(',', seat.Hits.Select(hit => hit.ItemId).Order())));
}
