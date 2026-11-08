using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A client reports that it finished loading the round's map.</summary>
public sealed record RoundLoadReported : IMessage;

/// <summary>Sent to every member each time one player finishes loading, so their clients spawn that player.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Slot">The seat index of the player who finished loading.</param>
public sealed record RoundPlayerLoaded(int RoomId, int Slot) : IMessage;

/// <summary>Sent to every member once all players have loaded: the countdown to the round's start begins.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Milliseconds">The countdown length; the start follows after it.</param>
public sealed record RoundCountdownStarted(int RoomId, int Milliseconds) : IMessage
{
    /// <summary>Gets whether this countdown releases a one-seat tutorial's native loading gate.</summary>
    public bool IsTutorial { get; init; }

    /// <summary>
    /// Gets whether the recipient's match connection failed and its round traffic moves to the game connection:
    /// the dialect then clears the loading words that select the relay (kr-1256 sub_95B910 sends over the game
    /// connection when the first word is zero).
    /// </summary>
    public bool MatchOverGame { get; init; }
}

/// <summary>Sent to every member when the countdown ran out: the round is running.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="TimeLimitMs">The round's time limit.</param>
public sealed record RoundStarted(int RoomId, int TimeLimitMs) : IMessage
{
    /// <summary>Gets whether this start releases a one-seat tutorial and mirrors its match clock.</summary>
    public bool IsTutorial { get; init; }

    /// <summary>Gets whether the round has no time limit; <see cref="TimeLimitMs"/> is then not a length.</summary>
    public bool Unlimited { get; init; }
}

/// <summary>A client asks for its own character to be added to the round's scene, naming the character's object.</summary>
/// <param name="ObjectId">The scene object of the client's own character.</param>
public sealed record RoundObjectAddRequested(int ObjectId) : IMessage;

/// <summary>Sent to the one client whose add request the round accepted.</summary>
/// <param name="ObjectId">The scene object the request named.</param>
public sealed record RoundObjectAddAcknowledged(int ObjectId) : IMessage;

/// <summary>A client reports that the round's stage is over and asks for the round to end.</summary>
public sealed record RoundEndRequested : IMessage
{
    /// <summary>Gets the verified end trigger, distinguishing cooperative wave completion from an ordinary stage report.</summary>
    public RoundEndReason Reason { get; init; } = RoundEndReason.StageReported;

    /// <summary>Gets the native cooperative stage result when the dialect recognizes its success flag.</summary>
    public bool? StageSucceeded { get; init; }
}

// The round's end message and everything else the result sequence sends live in
// RoundResultMessages.cs, so the whole result contract is read in one place.

/// <summary>
/// One packet of the build's peer-to-peer gameplay feed, which the server passes between the
/// players of a round without reading it. The code and the bytes are the sending build's own.
/// </summary>
/// <param name="Code">The feed packet's number in the sending build.</param>
/// <param name="Payload">The packet body.</param>
public sealed record RoundFeedSent(ushort Code, ReadOnlyMemory<byte> Payload) : IMessage
{
    /// <summary>Gets whether the native sender also needs the received frame to apply its local effect.</summary>
    public bool IncludeSender { get; init; }

    /// <summary>
    /// Gets whether the packet came on the match relay connection; it then goes to the other players' match links
    /// (their relay connections, or the game connection of a player whose relay failed), as on builds that play
    /// their rounds over the relay. Otherwise it goes to their game connections.
    /// </summary>
    public bool OnRelay { get; init; }

    /// <summary>
    /// Gets the characters the packet says acted (its actor word and, where either party may report, the other
    /// party's), read by the dialect from the build's layout; one of them must be the sender's own character. Null
    /// when the packet names no actor (a damager key, or a feed the dialect does not read).
    /// </summary>
    public IReadOnlyList<int>? ReportedActors { get; init; }

    /// <summary>Gets game session ids whose direct peer path replaces this relay delivery; actor and round validation still apply.</summary>
    public IReadOnlySet<long>? DirectPeerRecipients { get; init; }

    /// <summary>Gets an audited installed weapon specification reported by this frame, before room ownership checks.</summary>
    public RoundCriticalWeaponState? CriticalWeaponState { get; init; }

    /// <summary>Gets the native timed-effect beneficiary when both audited trigger gates are nonzero.</summary>
    public int? CriticalEffectActor { get; init; }

    /// <summary>Gets the audited race checkpoint whose current-round and monotonic-progress checks precede forwarding.</summary>
    public RoundExtremeRacingCheckpoint? ExtremeRacingCheckpoint { get; init; }
}

/// <summary>A feed packet from another player of the round, passed on unread.</summary>
/// <param name="Code">The feed packet's number in the sending build.</param>
/// <param name="Payload">The packet body.</param>
public sealed record RoundFeedReceived(ushort Code, ReadOnlyMemory<byte> Payload) : IMessage;

/// <summary>
/// A player's client reports its death and how long the player stays dead. Most builds then send
/// <see cref="RoundSpawnAccepted"/> by themselves when that time is over; a build whose client
/// only waits says so with <paramref name="ServerRevives"/>, and the round brings the player back.
/// </summary>
/// <param name="Actor">The build's own reference to the player's character; the logic does not read it.</param>
/// <param name="RespawnDelayMs">How long the player stays dead; it depends on what the player has equipped.</param>
/// <param name="ServerRevives">Whether the client waits for the round to bring the player back when the time is over.</param>
public sealed record RoundDeathReported(int Actor, int RespawnDelayMs, bool ServerRevives = false) : IMessage
{
    /// <summary>Gets the player seat decoded by the dialect, or null when that build has no proven actor mapping.</summary>
    public int? ActorSeat { get; init; }
}

/// <summary>A client asks which spawn point to use; the answer is <see cref="RoundSpawnOffered"/>.</summary>
/// <param name="Actor">
/// The build's own reference to the player's character. The logic does not read it: it is handed
/// back in the answers so the client can match them to the character that asked.
/// </param>
public sealed record RoundRespawnRequested(int Actor) : IMessage
{
    /// <summary>Gets the player seat decoded by the dialect, or null when that build has no proven actor mapping.</summary>
    public int? ActorSeat { get; init; }
}

/// <summary>
/// Sent to a dead player: the spawn point the server offers. The client answers with
/// <see cref="RoundSpawnAccepted"/>.
/// </summary>
/// <param name="Actor">The reference the request carried.</param>
/// <param name="SpawnIndex">The offered spawn point.</param>
public sealed record RoundSpawnOffered(int Actor, int SpawnIndex) : IMessage
{
    /// <summary>Gets whether the client explicitly requested an index reply rather than a server revival prompt.</summary>
    public bool IndexRequested { get; init; }
}

/// <summary>Who receives the grant that answers a <see cref="RoundSpawnAccepted"/>.</summary>
public enum RoundRespawnAudience
{
    /// <summary>Every member of the round, whose clients each bring the character back.</summary>
    Room,

    /// <summary>Only the requester, whose client brings its character back and tells the others itself.</summary>
    Requester,
}

/// <summary>What completes a granted respawn.</summary>
public enum RoundRespawnCompletion
{
    /// <summary>The grant itself: the character is alive once it is sent.</summary>
    Grant,

    /// <summary>The requester's confirmation on the match link the grant was given on.</summary>
    MatchConfirmation,
}

/// <summary>Which respawn requests the round grants.</summary>
public enum RoundRespawnAdmission
{
    /// <summary>Any request naming the sender's own character, whatever its life; a wrong owner is dropped.</summary>
    Owner,

    /// <summary>
    /// Only a dead character whose death presentation is over, in a round whose lives the clients report; the
    /// sender may name a character it controls, and naming another seat's character is refused loudly.
    /// </summary>
    CleanedDeath,
}

/// <summary>A build's rules for granting a respawn, stated in its build data and carried by each request.</summary>
/// <param name="Audience">Who receives the grant.</param>
/// <param name="Completion">What completes the grant.</param>
/// <param name="Admission">Which requests are granted.</param>
public sealed record RoundRespawnRules(RoundRespawnAudience Audience, RoundRespawnCompletion Completion, RoundRespawnAdmission Admission)
{
    /// <summary>Gets the rules of a build whose grant goes to the whole round and revives at once, for any owned request.</summary>
    public static RoundRespawnRules RoomGrant { get; } = new(RoundRespawnAudience.Room, RoundRespawnCompletion.Grant, RoundRespawnAdmission.Owner);
}

/// <summary>
/// A player's client asks to bring its character back into the round: at the spawn point it was offered or chose, or
/// with a native request body the grant repeats. The build's <see cref="Rules"/> decide whether and how it is granted;
/// the answer is <see cref="RoundPlayerRespawned"/>.
/// </summary>
/// <param name="Actor">The reference the request names.</param>
/// <param name="SpawnIndex">The spawn point the client took, or 0 for a build whose request names none.</param>
public sealed record RoundSpawnAccepted(int Actor, int SpawnIndex) : IMessage
{
    /// <summary>Gets the player seat decoded by the dialect, or null when that build has no proven actor mapping.</summary>
    public int? ActorSeat { get; init; }

    /// <summary>Gets the native request body for a build whose grant repeats it; empty otherwise.</summary>
    public ReadOnlyMemory<byte> Request { get; init; }

    /// <summary>Gets the build's rules for granting this request.</summary>
    public RoundRespawnRules Rules { get; init; } = RoundRespawnRules.RoomGrant;
}

/// <summary>The grant of a <see cref="RoundSpawnAccepted"/>, sent to the audience its rules name.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Slot">The seat of the player who came back, or of the controller for a bot.</param>
/// <param name="Actor">The build's reference to that character, as its client sent it.</param>
/// <param name="SpawnIndex">The spawn point the player came back at.</param>
public sealed record RoundPlayerRespawned(int RoomId, int Slot, int Actor, int SpawnIndex) : IMessage
{
    /// <summary>Gets the request body being granted, for a build whose grant repeats it; empty otherwise.</summary>
    public ReadOnlyMemory<byte> Request { get; init; }
}

/// <summary>
/// A player finished taunting a dead player. On a build whose round times the respawn, each taunt
/// changes how long the dead player still waits.
/// </summary>
/// <param name="VictimActor">The build's own reference to the dead player's character, as its death report carried it.</param>
public sealed record RoundTauntReported(int VictimActor) : IMessage;

/// <summary>Sent to a dead player whose wait the round changed, so its own countdown agrees.</summary>
/// <param name="Actor">The build's own reference to the dead player's character.</param>
/// <param name="DeltaMs">The change in milliseconds; negative shortens the wait.</param>
public sealed record RoundRespawnWaitChanged(int Actor, int DeltaMs) : IMessage;
