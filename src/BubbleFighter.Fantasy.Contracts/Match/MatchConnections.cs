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
    /// <summary>Validates a seat capability and identity, replacing and closing any previous transport for that seat.</summary>
    ValueTask<MatchSeatBinding> BindAsync(ISession session, MatchAuthenticate authentication, CancellationToken ct);
    /// <summary>Verifies that this transport still owns its lease before processing a subsequent message.</summary>
    bool IsCurrent(ISession session, MatchSeatBinding binding);
    /// <summary>Marks scene initialization complete for the current lease.</summary>
    void MarkInitialized(ISession session, MatchSeatBinding binding);
    /// <summary>Revokes this transport without removing its player from the room.</summary>
    void Release(ISession session);
}

/// <summary>Protocol-scoped room directory for native joins whose only credential is an issued opaque key.</summary>
public interface IMatchCapabilityDirectory : IMatchRoomDirectory
{
    /// <summary>Identifies the relay protocol owned by this room instance, allowing multiple builds in one host.</summary>
    string Protocol { get; }
    /// <summary>Issues one owned, bounded key for a live game seat in the current round; its length comes from the dialect.</summary>
    ValueTask<ReadOnlyMemory<byte>> IssueCapabilityAsync(ISession game, int ticketLength, CancellationToken ct);
    /// <summary>Matches the exact issued key within the client profile and current round, without accepting guessed account values.</summary>
    ValueTask<MatchSeatBinding> BindCapabilityAsync(ISession session, MatchCapabilityAuthenticate authentication, CancellationToken ct);
}
