using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>One seated player as a room policy sees it, copied under the room's lock.</summary>
/// <param name="SessionId">The seat's game session.</param>
/// <param name="Slot">The seat slot.</param>
/// <param name="UserNum">The player's account id.</param>
/// <param name="Nickname">The nickname the room shows.</param>
/// <param name="CharacterId">The equipped character item, or zero when unknown.</param>
/// <param name="EquippedItemIds">The worn items, the character excluded unless the build lists it.</param>
/// <param name="Host">Whether the seat is the room host.</param>
public sealed record RoomPolicySeat(long SessionId, int Slot, int UserNum, string Nickname, int CharacterId,
    IReadOnlyList<int> EquippedItemIds, bool Host);

/// <summary>A detached view of one waiting room for a room policy.</summary>
/// <param name="Instance">The room's unique instance; a closed room's instance is never reused.</param>
/// <param name="RoomId">The room number players see.</param>
/// <param name="ModeId">The room's mode id.</param>
/// <param name="HasPassword">Whether the room has a password.</param>
/// <param name="Waiting">Whether the room is waiting rather than playing.</param>
/// <param name="Seats">The human seats.</param>
public sealed record RoomPolicyRoom(Guid Instance, int RoomId, int ModeId, bool HasPassword, bool Waiting,
    IReadOnlyList<RoomPolicySeat> Seats)
{
    /// <summary>
    /// Gets whether the start being evaluated is the client's native automatic start. No host answers it, so a policy
    /// that asks the host to confirm lets it proceed; a short safety hold whose owner resumes the start may still apply.
    /// False outside <see cref="IRoomPolicy.EvaluateStart"/>.
    /// </summary>
    public bool AutomaticStart { get; init; }

    /// <summary>
    /// Gets whether the start being evaluated repeats a held start through <see cref="IRoomPolicyHost.ResumeHeldStartAsync"/>
    /// rather than a new request from the player. A policy that counts a second request as consent must not count a
    /// replay. False outside <see cref="IRoomPolicy.EvaluateStart"/>.
    /// </summary>
    public bool ResumedStart { get; init; }
}

/// <summary>What a room policy decides about a host's start.</summary>
public enum RoomStartDecision
{
    /// <summary>The start may go ahead.</summary>
    Proceed,
    /// <summary>The start is held: the room keeps waiting and the policy is told so it can explain why.</summary>
    Hold,
}

/// <summary>
/// A deployment rule that can hold a room start and observe membership, host and appearance changes and room closure.
/// Policies are optional; a room row without any behaves as before. Every callback runs on the room's
/// serialized executor, so callbacks of one room never overlap.
/// </summary>
public interface IRoomPolicy
{
    /// <summary>
    /// Decides a start. It runs under the room's lock in the same section that moves the room out of waiting, after the
    /// room's own start checks passed, so a start it lets proceed cannot be overtaken by a change it would have held.
    /// It must not block, send or call back into the room. Every policy is asked.
    /// </summary>
    /// <param name="room">The room as it is now.</param>
    /// <param name="requesterSessionId">The session that pressed start.</param>
    RoomStartDecision EvaluateStart(RoomPolicyRoom room, long requesterSessionId);

    /// <summary>
    /// Learns the outcome of a start every policy has answered, still under the room's lock: <paramref name="proceeding"/>
    /// is true when the room is leaving waiting now and false when any policy held it. Consume one-shot consent here, not
    /// in <see cref="EvaluateStart"/>, so another policy's hold does not use it up. It must not block, send or call back.
    /// </summary>
    /// <param name="room">The room as the policies saw it.</param>
    /// <param name="requesterSessionId">The session whose start was decided.</param>
    /// <param name="proceeding">Whether the start goes ahead.</param>
    void OnStartDecided(RoomPolicyRoom room, long requesterSessionId, bool proceeding) { }

    /// <summary>Explains a held start, outside the room's lock; called only on the policies that held it.</summary>
    /// <param name="room">The room as it was when the start was held.</param>
    /// <param name="requesterSessionId">The session that pressed start.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnStartHeldAsync(RoomPolicyRoom room, long requesterSessionId, CancellationToken ct);

    /// <summary>Runs after a player has entered the room and sees it, the creator included.</summary>
    /// <param name="room">The room with the new seat.</param>
    /// <param name="sessionId">The entering player's game session.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnMemberEnteredAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct);

    /// <summary>Runs after a seated player's character or worn items changed while the room waits.</summary>
    /// <param name="room">The room with the updated seat.</param>
    /// <param name="sessionId">The player whose appearance changed.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnAppearanceChangedAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct);

    /// <summary>
    /// Runs after a player left the room, kicked or disconnected included, once the members who stay were told. A host
    /// who left has already been replaced in <paramref name="room"/>.
    /// </summary>
    /// <param name="room">The room as it is now, or null when the player was the last one and the room closes.</param>
    /// <param name="session">The departed player's game session; it is no longer seated and may be closing.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnMemberLeftAsync(RoomPolicyRoom? room, ISession session, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Runs after the host handed the room to another member.</summary>
    /// <param name="room">The room with the new host.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnHostChangedAsync(RoomPolicyRoom room, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// Runs after a round once the room is waiting for its next start again and its members were sent back into it.
    /// A start that goes ahead is reported by <see cref="OnStartDecided"/>; this is the matching end of the round.
    /// </summary>
    /// <param name="room">The waiting room.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnWaitingAgainAsync(RoomPolicyRoom room, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Runs once when the room is deleted; state kept for it is released.</summary>
    /// <param name="instance">The closed room's instance.</param>
    void OnRoomClosed(Guid instance);
}

/// <summary>
/// The room side a room policy works with, one per game protocol. The room row publishes it and calls the policies
/// registered with it; a policy never reaches into the room row's own classes.
/// </summary>
public interface IRoomPolicyHost
{
    /// <summary>Gets the game protocol whose rooms this host serves.</summary>
    string Protocol { get; }

    /// <summary>Gets the client build of those rooms.</summary>
    string ClientBuild { get; }

    /// <summary>Adds a policy; dispose to remove it. Hand the handle to the registering row's effects.</summary>
    /// <param name="policy">The policy.</param>
    IDisposable Register(IRoomPolicy policy);

    /// <summary>Returns the room the game session is seated in, or null.</summary>
    /// <param name="sessionId">A game session id.</param>
    RoomPolicyRoom? Find(long sessionId);

    /// <summary>Returns the seated game session, or null when it has left.</summary>
    /// <param name="sessionId">A game session id.</param>
    ISession? Session(long sessionId);

    /// <summary>
    /// Repeats the start the policies held, for the same room instance and host, as if the host pressed start again.
    /// False when the room, host or held start no longer match.
    /// </summary>
    /// <param name="instance">The room instance.</param>
    /// <param name="requesterSessionId">The host session whose start was held.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> ResumeHeldStartAsync(Guid instance, long requesterSessionId, CancellationToken ct);
}
