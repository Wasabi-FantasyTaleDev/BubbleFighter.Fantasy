using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Identifies one native relay seed table reported by a legacy client.</summary>
public enum NativeSceneSeedKind
{
    /// <summary>The critical-hit seed table, carried as one or more 32-bit words.</summary>
    Critical,
    /// <summary>The defence seed table, carried as bytes.</summary>
    Defence,
    /// <summary>The critical-hit cancellation seed table, carried as 32-bit words.</summary>
    CriticalCancellation,
}

/// <summary>One immutable native scene seed table received from a relay connection.</summary>
/// <param name="Kind">The table family identified by its audited relay opcode.</param>
/// <param name="EntityId">The native scene actor identifier carried by the client.</param>
/// <param name="WordVectors">The table's 32-bit vectors, in wire order.</param>
/// <param name="ByteValues">The table's byte values, in wire order.</param>
public sealed record NativeSceneSeedTable(
    NativeSceneSeedKind Kind,
    int EntityId,
    IReadOnlyList<IReadOnlyList<uint>> WordVectors,
    IReadOnlyList<byte> ByteValues) : IMessage
{
    /// <summary>Gets all word values flattened in their original vector order.</summary>
    public IReadOnlyList<uint> WordValues => WordVectors.SelectMany(vector => vector).ToArray();

    /// <summary>Creates a word-table record without exposing mutable input storage.</summary>
    /// <param name="kind">The table family.</param>
    /// <param name="entityId">The native scene actor identifier.</param>
    /// <param name="values">The received words.</param>
    /// <returns>An immutable table record.</returns>
    public static NativeSceneSeedTable Words(NativeSceneSeedKind kind, int entityId, IEnumerable<uint> values) =>
        WithWordVectors(kind, entityId, [values.ToArray()]);

    /// <summary>Creates a word-table record while retaining each native vector boundary.</summary>
    /// <param name="kind">The table family.</param>
    /// <param name="entityId">The native scene actor identifier.</param>
    /// <param name="vectors">The received vectors in wire order.</param>
    /// <returns>An immutable table record.</returns>
    public static NativeSceneSeedTable WithWordVectors(NativeSceneSeedKind kind, int entityId,
        IEnumerable<IEnumerable<uint>> vectors) =>
        new(kind, entityId, vectors.Select(vector => vector.ToArray()).ToArray(), Array.Empty<byte>());

    /// <summary>Creates a byte-table record without exposing mutable input storage.</summary>
    /// <param name="kind">The table family.</param>
    /// <param name="entityId">The native scene actor identifier.</param>
    /// <param name="values">The received bytes.</param>
    /// <returns>An immutable table record.</returns>
    public static NativeSceneSeedTable Bytes(NativeSceneSeedKind kind, int entityId, IEnumerable<byte> values) =>
        new(kind, entityId, Array.Empty<IReadOnlyList<uint>>(), values.ToArray());
}

/// <summary>A seat's result in the previous round of the same room, the history the critical table keys on.</summary>
public enum MatchPreviousRound
{
    /// <summary>The seat played no earlier round in this room.</summary>
    None,
    /// <summary>The seat's team won the previous round.</summary>
    Won,
    /// <summary>The previous round was a draw for the seat.</summary>
    Drew,
    /// <summary>The seat's team lost the previous round after not losing the one before it.</summary>
    Lost,
    /// <summary>The seat's team lost the previous round and the one before it.</summary>
    LostInRow,
}

/// <summary>
/// Asks the seed authority to issue one seat's critical, defence and cancellation tables over its match connection
/// for the round that is starting. The authority chooses every value; nothing reported by a client is reused.
/// </summary>
/// <param name="RoomId">The room whose round is starting.</param>
/// <param name="Generation">The room start generation the tables belong to.</param>
/// <param name="EntityId">The seat's native scene actor identifier.</param>
/// <param name="PreviousRound">The seat's result in the previous round of the same room.</param>
public sealed record MatchSeedTablesRequested(int RoomId, int Generation, int EntityId, MatchPreviousRound PreviousRound) : IMessage;

/// <summary>
/// The game connection of the seat a match connection is bound to, published on the match session while the binding
/// lasts, so a dialect can hand the relay's peer frames to the build's game-side handlers.
/// </summary>
public interface IMatchGameLink
{
    /// <summary>Gets the bound seat's game connection.</summary>
    ISession Game { get; }
}

/// <summary>Provides the latest bounded native seed tables for live relay sessions.</summary>
public interface INativeSceneSeedStore
{
    /// <summary>Gets the latest table of one kind for a relay session and round generation.</summary>
    /// <param name="sessionId">The server session identifier of the relay connection.</param>
    /// <param name="generation">The room start generation that owns the relay lease.</param>
    /// <param name="kind">The table family to find.</param>
    /// <param name="table">The latest table when present.</param>
    /// <returns><see langword="true"/> when the current generation has reported that table.</returns>
    bool TryGet(long sessionId, int generation, NativeSceneSeedKind kind, out NativeSceneSeedTable table);

    /// <summary>Records a table for a generation, replacing only the same session and table family.</summary>
    /// <param name="sessionId">The server session identifier of the relay connection.</param>
    /// <param name="generation">The room start generation that owns the relay lease.</param>
    /// <param name="table">The validated table to retain.</param>
    void Set(long sessionId, int generation, NativeSceneSeedTable table);

    /// <summary>Removes every table associated with a relay session.</summary>
    /// <param name="sessionId">The server session identifier of the relay connection.</param>
    void Remove(long sessionId);
}
