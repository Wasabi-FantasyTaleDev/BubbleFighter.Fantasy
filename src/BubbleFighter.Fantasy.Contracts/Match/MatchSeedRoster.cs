namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>One occupant of a starting round whose native actor every seat must be able to roll defence for.</summary>
/// <param name="EntityId">The occupant's native scene actor identifier.</param>
/// <param name="Equipment">The occupant's frozen accepted worn set, or null when the room carries none.</param>
public sealed record MatchSeedOccupant(int EntityId, MatchSeedEquipment? Equipment);

/// <summary>
/// Freezes every occupant of a starting round, in seat order, so that each seat can be sent every occupant's
/// defence table. Two rosters are equal when they hold equal occupants in the same order.
/// </summary>
public sealed class MatchSeedRoster : IEquatable<MatchSeedRoster>
{
    private readonly MatchSeedOccupant[] _occupants;

    /// <summary>Copies the occupants and rejects a roster that names an actor twice.</summary>
    /// <param name="occupants">The round's occupants in seat order.</param>
    /// <exception cref="ArgumentException">The roster is empty or repeats an actor.</exception>
    public MatchSeedRoster(IEnumerable<MatchSeedOccupant> occupants)
    {
        ArgumentNullException.ThrowIfNull(occupants);
        _occupants = occupants.ToArray();
        if (_occupants.Length == 0 || _occupants.Any(occupant => occupant is null))
            throw new ArgumentException("A seed roster needs at least one occupant.", nameof(occupants));
        if (_occupants.Select(occupant => occupant.EntityId).Distinct().Count() != _occupants.Length)
            throw new ArgumentException("A seed roster names an actor twice.", nameof(occupants));
        Occupants = Array.AsReadOnly(_occupants);
    }

    /// <summary>Gets the occupants in seat order.</summary>
    public IReadOnlyList<MatchSeedOccupant> Occupants { get; }

    /// <summary>Compares occupants in order, so a repeated round request is recognized as the same request.</summary>
    public bool Equals(MatchSeedRoster? other) => other is not null && _occupants.AsSpan().SequenceEqual(other._occupants);

    /// <summary>Compares another object's occupants in order.</summary>
    public override bool Equals(object? obj) => obj is MatchSeedRoster other && Equals(other);

    /// <summary>Hashes the occupants used by roster equality.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var occupant in _occupants) hash.Add(occupant);
        return hash.ToHashCode();
    }
}
