using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Authenticates a match transport using the capability issued to its room seat.</summary>
/// <param name="Ticket">Opaque seat capability; its wire representation belongs to the dialect.</param>
/// <param name="UserNumber">Account identity presented by the client.</param>
/// <param name="Nickname">Display identity presented by the client.</param>
public sealed record MatchAuthenticate(ReadOnlyMemory<byte> Ticket, int UserNumber, string Nickname) : IMessage;

/// <summary>Presents only an opaque room-issued capability when the native join carries no account identity.</summary>
/// <param name="Ticket">Exact native key bytes extracted by the owning dialect.</param>
public sealed record MatchCapabilityAuthenticate(ReadOnlyMemory<byte> Ticket) : IMessage;

/// <summary>Reports native actor identity after capability authentication, without inventing an account field.</summary>
/// <param name="EntityId">Local scene actor copied by the native report builder.</param>
/// <param name="Nickname">Display identity copied by the native report builder.</param>
public sealed record MatchSceneIdentityRegistered(int EntityId, string Nickname) : IMessage;

/// <summary>Reports the scene identity after the match transport was authenticated.</summary>
/// <param name="UserNumber">Account owning the scene.</param>
/// <param name="EntityId">Client scene entity attached to the seat.</param>
/// <param name="Nickname">Owner display name.</param>
public sealed record MatchSceneRegistered(int UserNumber, int EntityId, string Nickname) : IMessage;

/// <summary>Names the loaded map on an already authenticated match transport.</summary>
/// <param name="EntityId">Client scene entity attached to the seat.</param>
/// <param name="Nickname">Owner display name.</param>
/// <param name="Map">Loaded scene key.</param>
public sealed record MatchMapNamed(int EntityId, string Nickname, string Map) : IMessage;

/// <summary>Acknowledges authenticated match admission; the dialect determines whether a subsequent scene report is required.</summary>
public sealed record MatchAuthenticationAccepted : IMessage;

/// <summary>Acknowledges scene registration for the authenticated actor; any required native initialization belongs to the dialect.</summary>
/// <param name="EntityId">The authenticated seat's scene entity.</param>
public sealed record MatchSceneAccepted(int EntityId) : IMessage;

/// <summary>Immutable identity leased to one authenticated match transport.</summary>
/// <param name="RoomId">Room owning the lease.</param>
/// <param name="Slot">Stable seat index inside the room.</param>
/// <param name="UserNumber">Authenticated account number.</param>
/// <param name="EntityId">Client scene entity for that seat.</param>
/// <param name="Nickname">Account display name.</param>
/// <param name="Map">Resolved scene key at authentication.</param>
/// <param name="Generation">Round generation that issued this lease; restart revokes older bindings.</param>
public sealed record MatchSeatBinding(int RoomId, int Slot, int UserNumber, int EntityId, string Nickname, string Map, int Generation);

/// <summary>Room-owned boundary for granting and revoking authenticated match connections.</summary>
public interface IMatchRoomDirectory
{
    /// <summary>
    /// Validates a seat capability and identity and binds the transport to that seat, replacing and
    /// closing any previous transport for it. The seat rewrite runs as an operation on the owning
    /// room's serialized executor, so it cannot interleave with a round start, a departure or the
    /// countdown expiry; the replaced transport is closed only after the rewrite commits, and its
    /// close raises its own departure event.
    /// </summary>
    /// <param name="session">The match transport's session; it must not already be bound to a room.</param>
    /// <param name="authentication">The presented ticket and claimed account identity, which must match the seated player.</param>
    /// <param name="ct">Checked before the bind begins; cancellation after that point does not undo a committed bind.</param>
    /// <returns>The lease for the seat, valid for the round generation that issued it.</returns>
    /// <exception cref="InvalidOperationException">The session is already bound, the capability is unknown or
    /// no longer valid for the room phase or the presented identity, or the room was removed before the bind ran.</exception>
    ValueTask<MatchSeatBinding> BindAsync(ISession session, MatchAuthenticate authentication, CancellationToken ct);
    /// <summary>
    /// Verifies that this transport still owns its lease before a subsequent message is processed: the
    /// room exists, the round generation matches, the phase is still active, the seat still references
    /// this session, and neither connection has closed.
    /// </summary>
    bool IsCurrent(ISession session, MatchSeatBinding binding);
    /// <summary>Marks scene initialization complete for the current lease.</summary>
    /// <exception cref="InvalidOperationException">The room was removed or the lease is no longer current.</exception>
    void MarkInitialized(ISession session, MatchSeatBinding binding);
    /// <summary>Revokes this transport's lease without removing its player from the room; an unknown or already detached session is ignored.</summary>
    void Release(ISession session);
}

/// <summary>Protocol-scoped room directory for native joins whose only credential is an issued opaque key.</summary>
public interface IMatchCapabilityDirectory : IMatchRoomDirectory
{
    /// <summary>Identifies the relay protocol owned by this room instance, allowing multiple builds in one host.</summary>
    string Protocol { get; }
    /// <summary>
    /// Issues one cryptographically random capability for a live game seat in the current round. The
    /// key is stored on the seat and stays valid until the round generation advances, the offer is
    /// withdrawn, or the capability binds. Issuing runs on the room's serialized executor, including
    /// the native map-load path.
    /// </summary>
    /// <param name="game">The seat's live game session.</param>
    /// <param name="ticketLength">The requested key length in bytes, bounded by the profile's configuration.</param>
    /// <param name="ct">Cancels the issuance before the capability is stored.</param>
    /// <exception cref="ArgumentOutOfRangeException">The length is outside the configured bounds.</exception>
    /// <exception cref="InvalidOperationException">The game session is not a live seat in an active round, or the
    /// current round already issued or attached a capability for that seat.</exception>
    ValueTask<ReadOnlyMemory<byte>> IssueCapabilityAsync(ISession game, int ticketLength, CancellationToken ct);
    /// <summary>
    /// Matches the exact issued key by constant-time comparison within the client profile and current
    /// round, without accepting guessed account values: the seat is found by its stored capability, not
    /// by presented identity. Binding runs on the room's serialized executor and, as in
    /// <see cref="IMatchRoomDirectory.BindAsync"/>, closes the replaced transport after the rewrite commits.
    /// </summary>
    /// <param name="session">The native match transport's session; it must not already be bound to a room.</param>
    /// <param name="authentication">The presented capability bytes.</param>
    /// <param name="ct">Checked before the bind begins.</param>
    /// <exception cref="InvalidOperationException">The session is already bound, the key is unknown, ambiguous,
    /// revoked before binding, or no longer valid in the room phase.</exception>
    ValueTask<MatchSeatBinding> BindCapabilityAsync(ISession session, MatchCapabilityAuthenticate authentication, CancellationToken ct);
}
