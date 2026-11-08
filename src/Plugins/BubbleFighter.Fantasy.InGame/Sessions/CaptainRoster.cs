namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Keeps the current and preceding Captain appointment per team, bounded by the room's two teams.</summary>
internal sealed class CaptainRoster
{
    private sealed class Term(RoomSeat seat)
    {
        internal RoomSeat Seat { get; } = seat;
        internal long LifeGeneration { get; } = seat.Life.Generation;
        internal bool Dropped { get; set; }
        internal bool Matches(RoomSeat candidate) => ReferenceEquals(Seat, candidate)
            && LifeGeneration == candidate.Life.Generation;
    }

    private readonly Dictionary<byte, (Term Current, Term? Previous)> _teams = [];

    /// <summary>Gets whether a team has an appointment, including one awaiting a living successor.</summary>
    public bool Any => _teams.Count > 0;

    /// <summary>Returns the last appointed seat, including a fallen Captain awaiting a successor.</summary>
    public int? CaptainOf(byte team) => _teams.TryGetValue(team, out var terms) ? terms.Current.Seat.Slot : null;

    /// <summary>Checks whether an undropped appointment still belongs to the connected character incarnation.</summary>
    public bool NeedsAppointment(byte team, IReadOnlyCollection<RoomSeat> seats) =>
        !_teams.TryGetValue(team, out var terms) || terms.Current.Dropped
        || !seats.Contains(terms.Current.Seat) || !terms.Current.Matches(terms.Current.Seat)
        || terms.Current.Seat.Game.Connection.IsClosed;

    /// <summary>Starts an appointment tied to this character's present life; only one previous appointment is retained.</summary>
    public void Appoint(byte team, RoomSeat seat) =>
        _teams[team] = _teams.TryGetValue(team, out var terms) ? (new Term(seat), terms.Current) : (new Term(seat), null);

    /// <summary>Clears a team that has no remaining members.</summary>
    public void Vacate(byte team) => _teams.Remove(team);

    /// <summary>Accepts the owning Captain's death once until that character respawns.</summary>
    public bool TryDrop(byte team, RoomSeat seat)
    {
        if (!_teams.TryGetValue(team, out var terms) || terms.Current.Dropped || !terms.Current.Matches(seat))
            return false;
        terms.Current.Dropped = true;
        return true;
    }

    /// <summary>Checks whether this character incarnation holds its team's current, undropped appointment.</summary>
    public bool Serves(RoomSeat seat) => _teams.TryGetValue(seat.Team, out var terms)
        && !terms.Current.Dropped && terms.Current.Matches(seat);

    /// <summary>Checks Captain identity without consuming the common life record's kill credit.</summary>
    public bool WasCaptain(byte team, RoomSeat seat) => _teams.TryGetValue(team, out var terms)
        && (terms.Current.Matches(seat) || terms.Previous?.Matches(seat) == true);
}
