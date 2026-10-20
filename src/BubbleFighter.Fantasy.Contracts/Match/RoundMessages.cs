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

/// <summary>A dead player's client comes back at a spawn point: the one it was offered, or the one it chose when its respawn time ran out.</summary>
/// <param name="Actor">The reference the offer carried.</param>
/// <param name="SpawnIndex">The spawn point the client took.</param>
public sealed record RoundSpawnAccepted(int Actor, int SpawnIndex) : IMessage
{
    /// <summary>Gets the player seat decoded by the dialect, or null when that build has no proven actor mapping.</summary>
    public int? ActorSeat { get; init; }
}

/// <summary>Sent to every member of the round: a player is alive again.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Slot">The seat of the player who came back.</param>
/// <param name="Actor">The build's reference to that player's character, as its client sent it.</param>
/// <param name="SpawnIndex">The spawn point the player came back at.</param>
public sealed record RoundPlayerRespawned(int RoomId, int Slot, int Actor, int SpawnIndex) : IMessage;

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
