using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Where a character is in its life cycle this round, whichever protocol family reports it.</summary>
internal enum SeatLife
{
    /// <summary>Playing normally.</summary>
    Alive,

    /// <summary>Trapped in a bubble; a release or an enemy's pop decides what follows.</summary>
    Bubble,

    /// <summary>Dead; waits for its death presentation or its respawn wait before it may come back.</summary>
    Dead,

    /// <summary>
    /// The way back was granted; the client's confirmation on the granted match link, or its spawn acceptance for
    /// a revival offered without a link, completes it.
    /// </summary>
    ResetPending,
}

/// <summary>
/// One character's life in the current round: the single record every protocol family's round flow reads and
/// changes. A seat and a host-controlled bot each own one. The legacy family reports a death with its respawn
/// wait and completes it with a spawn acceptance; the modern family reports bubbles, deaths and cleanups and
/// completes a granted reset on its match link. Both move the same <see cref="State"/>; the remaining fields are
/// the pending-operation data one family or the other carries. Guarded by the room gate.
/// </summary>
internal sealed class ParticipantLife
{
    private bool _hadBubble;
    private bool _released;
    private bool _knockdownCredited;
    private bool _rescueCredited;
    private bool _deathCredited;

    /// <summary>Gets the incarnation advanced by round reset, conversion and completed respawn.</summary>
    internal long Generation { get; private set; }

    /// <summary>Gets or sets the accepted character form in this life; native respawn and conversion clear it.</summary>
    internal bool CharacterFormActive { get; set; }

    /// <summary>Gets where the character is in its life cycle.</summary>
    internal SeatLife State { get; private set; }

    /// <summary>Gets whether a dead character finished its death presentation, so a reset may be granted.</summary>
    internal bool DeathCleaned { get; private set; }

    /// <summary>Gets whether this death already published its terminal observation notice instead of a respawn.</summary>
    internal bool ObservationSent { get; private set; }

    /// <summary>Gets whether this dead actor received its terminal removal reply in the current incarnation.</summary>
    internal bool ActorRemovalSent { get; private set; }

    /// <summary>
    /// Gets the match link the latest reset was granted on, or null when the grant is not tied to a link (a legacy
    /// revival offer completes through the spawn acceptance on the game connection instead). Only meaningful while
    /// <see cref="State"/> is <see cref="SeatLife.ResetPending"/>.
    /// </summary>
    internal ISession? GrantLink { get; private set; }

    /// <summary>Gets the native actor handle a legacy death report named; a revival must name the same one.</summary>
    internal int? DeathActor { get; private set; }

    /// <summary>Gets whether the round, not the client, times this dead character's way back (the legacy server-revive wait).</summary>
    internal bool ServerTimedWait { get; private set; }

    /// <summary>Gets whether a cooperative revival arrived before the death report it applies to.</summary>
    internal bool RevivalRequestedEarly { get; private set; }

    /// <summary>Gets whether a revival offer is outstanding without a match link; a repeated death report then changes nothing.</summary>
    internal bool RevivalOffered => State == SeatLife.ResetPending && GrantLink is null;

    /// <summary>Gets whether a reset may be granted now: the character is dead and its death presentation is over.</summary>
    internal bool CanGrantReset => State == SeatLife.Dead && DeathCleaned && !ActorRemovalSent;

    /// <summary>Starts a new round alive with nothing pending.</summary>
    internal void Reset()
    {
        Generation = checked(Generation + 1);
        // kr-1256 sub_4A73A0 clears the form on character reset; kr-1256 sub_ACD450 does so on conversion.
        CharacterFormActive = false;
        State = SeatLife.Alive;
        DeathCleaned = false;
        ObservationSent = false;
        ActorRemovalSent = false;
        GrantLink = null;
        DeathActor = null;
        ServerTimedWait = false;
        RevivalRequestedEarly = false;
        _hadBubble = _released = _knockdownCredited = _rescueCredited = _deathCredited = false;
    }

    /// <summary>Traps a living character in a bubble.</summary>
    /// <returns>Whether the character was alive.</returns>
    internal bool Bubble()
    {
        if (State != SeatLife.Alive) return false;
        State = SeatLife.Bubble;
        _hadBubble = true;
        _released = _knockdownCredited = _rescueCredited = false;
        return true;
    }

    /// <summary>Kills a living character without a bubble.</summary>
    /// <returns>Whether the character was alive.</returns>
    internal bool Die()
    {
        if (State != SeatLife.Alive) return false;
        EnterDeath();
        return true;
    }

    /// <summary>Kills a bubbled character, because its bubble ran out or an opponent popped it.</summary>
    /// <returns>Whether the character was in a bubble.</returns>
    internal bool Burst()
    {
        if (State != SeatLife.Bubble) return false;
        EnterDeath();
        return true;
    }

    /// <summary>Frees a bubbled character alive.</summary>
    /// <returns>Whether the character was in a bubble.</returns>
    internal bool Release()
    {
        if (State != SeatLife.Bubble) return false;
        State = SeatLife.Alive;
        _released = true;
        return true;
    }

    /// <summary>Credits the current bubble once, including when its source arrives after the basic notification.</summary>
    internal bool CreditKnockdown()
    {
        if (!_hadBubble || _knockdownCredited) return false;
        _knockdownCredited = true;
        return true;
    }

    /// <summary>Credits a completed release once, including a rescuer supplied after a generic release.</summary>
    internal bool CreditRescue()
    {
        if (!_released || _rescueCredited) return false;
        _rescueCredited = true;
        return true;
    }

    /// <summary>Credits a dead character once until it respawns, regardless of which native death report arrived first.</summary>
    internal bool CreditDeath()
    {
        if (State != SeatLife.Dead || _deathCredited) return false;
        _deathCredited = true;
        return true;
    }

    /// <summary>Records that a dead character finished its death presentation.</summary>
    /// <returns>Whether the character was dead.</returns>
    internal bool CleanDeath()
    {
        if (State != SeatLife.Dead) return false;
        DeathCleaned = true;
        return true;
    }

    /// <summary>
    /// Records a native death report naming the character's handle and who times its way back. A client repeats
    /// the report while it waits, so a repeat refreshes the record; an outstanding revival or terminal observation ignores repeats.
    /// </summary>
    /// <param name="actor">The handle the report named.</param>
    /// <param name="serverTimed">Whether the round times the respawn.</param>
    /// <returns>Whether the report was recorded.</returns>
    internal bool RecordDeath(int actor, bool serverTimed)
    {
        if (RevivalOffered || ObservationSent || ActorRemovalSent) return false;
        EnterDeath();
        DeathActor = actor;
        ServerTimedWait = serverTimed;
        return true;
    }

    /// <summary>Marks a dead character's observation notice once without reviving it or changing its scoring evidence.</summary>
    internal bool TryObserve()
    {
        if (State != SeatLife.Dead || ObservationSent) return false;
        ObservationSent = true;
        return true;
    }

    /// <summary>Records a cleaned actor's terminal removal once while retaining its death and scoring state.</summary>
    internal bool TryRemoveActor()
    {
        if (!CanGrantReset) return false;
        ActorRemovalSent = true;
        return true;
    }

    /// <summary>Notes a cooperative revival that arrived before its death report, for that report to pick up.</summary>
    internal void RequestRevivalEarly() => RevivalRequestedEarly = true;

    /// <summary>Takes a cooperative revival that arrived early, clearing it.</summary>
    /// <returns>Whether one was waiting.</returns>
    internal bool TakeEarlyRevival()
    {
        if (!RevivalRequestedEarly) return false;
        RevivalRequestedEarly = false;
        return true;
    }

    /// <summary>
    /// Withdraws a reset granted on another match link than <paramref name="link"/>: that link's confirmation can no
    /// longer arrive, so the character is dead again and may be granted anew.
    /// </summary>
    /// <param name="link">The requester's current match link.</param>
    internal void WithdrawStaleGrant(ISession link)
    {
        if (State == SeatLife.ResetPending && !ReferenceEquals(GrantLink, link))
            State = SeatLife.Dead;
    }

    /// <summary>Grants a reset that completes when <paramref name="link"/>, or the spawn acceptance when null, confirms it.</summary>
    /// <param name="link">The match link the confirmation must arrive on, or null for a legacy revival offer.</param>
    internal void Grant(ISession? link)
    {
        State = SeatLife.ResetPending;
        GrantLink = link;
    }

    /// <summary>Restores the life state when a reset grant could not be delivered.</summary>
    internal void GrantFailed(SeatLife previous = SeatLife.Dead)
    {
        if (State != SeatLife.ResetPending) return;
        State = previous;
        GrantLink = null;
    }

    /// <summary>Whether <paramref name="link"/> may confirm the pending reset.</summary>
    /// <param name="link">The connection the confirmation arrived on.</param>
    internal bool ConfirmsGrant(ISession link) => State == SeatLife.ResetPending && ReferenceEquals(GrantLink, link);

    /// <summary>Brings the character back alive and clears everything its death left pending.</summary>
    internal void Revive() => Reset();

    /// <summary>
    /// Ends the death because the character changed team in a conversion round: it plays on alive as a zombie.
    /// A cooperative revival that arrived early stays noted, as the conversion does not answer it.
    /// </summary>
    internal void EndDeathByConversion()
    {
        var early = RevivalRequestedEarly;
        Reset();
        RevivalRequestedEarly = early;
    }

    private void EnterDeath()
    {
        State = SeatLife.Dead;
        DeathCleaned = false;
        GrantLink = null;
    }
}
