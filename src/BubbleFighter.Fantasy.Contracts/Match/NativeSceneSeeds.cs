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
