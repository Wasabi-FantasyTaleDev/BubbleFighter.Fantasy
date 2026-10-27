namespace BubbleFighter.Fantasy.Contracts.Sessions;

/// <summary>The authenticated player behind a game connection.</summary>
/// <param name="UserNum">Account id; the S2C 3 login reply and every per-player record key on it.</param>
/// <param name="Username">Login name the client presented.</param>
/// <param name="Nickname">Display nickname.</param>
/// <param name="Level">Player level.</param>
public sealed record PlayerIdentity(int UserNum, string Username, string Nickname, int Level)
{
    /// <summary>Gets the committed avatar material color, independent of team and fighter slot.</summary>
    public ushort ColorIndex { get; init; }
}

/// <summary>
/// Where a game connection is in the client's scene flow. The set follows the scene installs the
/// client acts on: the login burst ends in the main menu (S2C 117 kind 1), C2S 106 enters the plaza,
/// a room install (S2C 117 kind 2) seats the player in a waiting room, and a match runs from the
/// start chain until the result stage hands the seat back to the room.
/// </summary>
public enum SessionPhase
{
    /// <summary>Login accepted; the login burst has not finished.</summary>
    Authenticated,

    /// <summary>The main menu is installed.</summary>
    MainMenu,

    /// <summary>In the plaza.</summary>
    Plaza,

    /// <summary>Seated in a waiting room.</summary>
    Room,

    /// <summary>In a match, from the start chain to the result stage.</summary>
    Match,
}

/// <summary>
/// The typed player state of one game connection, registered as a session feature by the plugin
/// that authenticates it. Other plugins read identity and phase through this contract instead of
/// by name. Phase changes are checked against one transition table, so an out-of-order change is
/// an error at the point it happens rather than a silently wrong state later.
/// </summary>
public interface IPlayerSession
{
    /// <summary>Gets the authenticated player.</summary>
    PlayerIdentity Identity { get; }

    /// <summary>
    /// Gets the connection that owns this authenticated login, or null when the provider does not identify it.
    /// Match connections may share this state but retain the original owner's id; public presence must count only that owner.
    /// </summary>
    long? LoginConnectionId => null;

    /// <summary>Updates the committed display nickname while preserving scene phase and generation.</summary>
    void ChangeNickname(string nickname) => throw new NotSupportedException("This player session cannot change its nickname.");

    /// <summary>Updates the committed avatar color while preserving identity and scene generation.</summary>
    void ChangeColorIndex(ushort colorIndex) => throw new NotSupportedException("This player session cannot change its color.");

    /// <summary>Gets the current phase.</summary>
    SessionPhase Phase { get; }

    /// <summary>
    /// Gets a counter that increases on every phase change. Work started in one phase can compare
    /// it before applying a late result, so a result from an earlier scene is recognised as stale.
    /// </summary>
    long Generation { get; }

    /// <summary>
    /// Moves to <paramref name="next"/> and returns the new generation. Staying in the current phase
    /// is a no-op. A move the transition table does not allow throws <see cref="InvalidOperationException"/>.
    /// </summary>
    long MoveTo(SessionPhase next);
}

/// <summary>Thread-safe <see cref="IPlayerSession"/> with the shared transition table.</summary>
public sealed class PlayerSession : IPlayerSession
{
    private readonly Lock _gate = new();
    private SessionPhase _phase = SessionPhase.Authenticated;
    private long _generation;
    private PlayerIdentity _identity;

    /// <summary>Creates the state for a freshly authenticated player.</summary>
    /// <param name="identity">The authenticated player.</param>
    public PlayerSession(PlayerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
    }

    /// <summary>Creates player state owned by the connection that completed authentication.</summary>
    /// <param name="identity">The authenticated identity.</param>
    /// <param name="loginConnectionId">The owning connection id, preserved when a match connection shares this state.</param>
    public PlayerSession(PlayerIdentity identity, long loginConnectionId) : this(identity) =>
        LoginConnectionId = loginConnectionId;

    /// <inheritdoc />
    public long? LoginConnectionId { get; }

    /// <inheritdoc />
    public PlayerIdentity Identity { get { lock (_gate) return _identity; } }

    /// <inheritdoc />
    public void ChangeNickname(string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        lock (_gate) _identity = _identity with { Nickname = nickname };
    }

    /// <inheritdoc />
    public void ChangeColorIndex(ushort colorIndex)
    {
        lock (_gate) _identity = _identity with { ColorIndex = colorIndex };
    }

    /// <inheritdoc />
    public SessionPhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    /// <inheritdoc />
    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    /// <inheritdoc />
    public long MoveTo(SessionPhase next)
    {
        lock (_gate)
        {
            if (next == _phase)
                return _generation;
            if (!IsAllowed(_phase, next))
                throw new InvalidOperationException(
                    $"Player {Identity.UserNum} cannot move from {_phase} to {next}.");
            _phase = next;
            return ++_generation;
        }
    }

    /// <summary>
    /// Whether the client flow allows moving from <paramref name="from"/> to <paramref name="to"/>:
    /// the login burst ends in the main menu; the menu and the plaza lead to each other and to a
    /// room; a room leads back to the plaza or into a match; a match returns to its room or, when
    /// the player leaves it, to the plaza.
    /// </summary>
    public static bool IsAllowed(SessionPhase from, SessionPhase to) => (from, to) switch
    {
        (SessionPhase.Authenticated, SessionPhase.MainMenu) => true,
        (SessionPhase.MainMenu, SessionPhase.Plaza or SessionPhase.Room) => true,
        (SessionPhase.Plaza, SessionPhase.MainMenu or SessionPhase.Room) => true,
        (SessionPhase.Room, SessionPhase.Plaza or SessionPhase.Match) => true,
        (SessionPhase.Match, SessionPhase.Room or SessionPhase.Plaza) => true,
        _ => false,
    };
}
