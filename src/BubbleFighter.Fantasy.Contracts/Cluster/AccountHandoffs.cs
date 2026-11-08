using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>
/// Moves account authority between nodes of one persistent data domain without a moment where two nodes may write.
/// The source records a pending handoff while it still holds the account and only then releases the hold; until the
/// handoff deadline only the named migration may claim the account, and the source may withdraw it instead.
/// </summary>
public interface IAccountHandoffStore
{
    /// <summary>
    /// Records <paramref name="handoff"/> on the hold's own authority connection and then releases the hold.
    /// The hold is unusable afterwards, whether or not this call succeeds; failure leaves no pending handoff.
    /// </summary>
    ValueTask HandOffAsync(IAccountOwnership ownership, AccountHandoff handoff, CancellationToken cancellationToken);

    /// <summary>
    /// Acquires the account for the destination of a pending, unexpired handoff with this exact migration identity,
    /// marking it claimed. Returns null when the handoff is missing, settled, expired or the account is held elsewhere.
    /// </summary>
    ValueTask<IAccountOwnership?> TryClaimAsync(long userNum, string migrationId, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws a pending handoff with this migration identity and acquires the account for its source again.
    /// Returns null when the destination already claimed it or the account is held elsewhere.
    /// </summary>
    ValueTask<IAccountOwnership?> TryWithdrawAsync(long userNum, string migrationId, CancellationToken cancellationToken);
}

/// <summary>One pending transfer of account authority to another node.</summary>
/// <param name="UserNum">The account being transferred.</param>
/// <param name="MigrationId">An unpredictable 32-character lower-case hexadecimal identity shared by both nodes.</param>
/// <param name="SourceNode">The node releasing the account.</param>
/// <param name="DestinationNode">The only node that may claim it.</param>
/// <param name="Lifetime">How long the destination may claim it, measured by the database clock.</param>
public sealed record AccountHandoff(long UserNum, string MigrationId, string SourceNode, string DestinationNode, TimeSpan Lifetime)
{
    /// <summary>The longest accepted handoff lifetime.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Checks that the identities are well formed and the lifetime is positive and bounded.</summary>
    public bool IsValid() => UserNum > 0 && IsMigrationId(MigrationId) && IsNode(SourceNode) && IsNode(DestinationNode)
        && SourceNode != DestinationNode && Lifetime > TimeSpan.Zero && Lifetime <= MaxLifetime;

    /// <summary>Checks the 32-character lower-case hexadecimal migration identity format.</summary>
    public static bool IsMigrationId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower);

    private static bool IsNode(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>
/// Binds account handoffs to the game connections that own them. A handed-off connection keeps its socket for the
/// native move but loses write authority; a withdrawn or failed move restores authority on that same connection.
/// </summary>
public interface IAccountSessionHandoff
{
    /// <summary>Hands the connection's admitted account to another node. Writes through this connection fail afterwards.</summary>
    ValueTask HandOffAsync(ISession session, AccountHandoff handoff, CancellationToken cancellationToken);

    /// <summary>Withdraws this connection's own pending handoff and restores its authority; false when it was already claimed.</summary>
    ValueTask<bool> TryWithdrawAsync(ISession session, string migrationId, CancellationToken cancellationToken);

    /// <summary>Admits a newly connected game connection by claiming a pending handoff of the account to this node.</summary>
    ValueTask<bool> TryClaimAsync(ISession session, long userNum, string migrationId, CancellationToken cancellationToken);
}
