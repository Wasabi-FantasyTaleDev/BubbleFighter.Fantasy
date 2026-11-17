using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Protocol and storage bounds shared by sponsor (<c>user_host</c>) prize rooms.</summary>
public static class UserHostLimits
{
    /// <summary>Maximum profile identifier length stored on escrow and claim rows.</summary>
    public const int MaxProfileIdLength = 32;

    /// <summary>Maximum match identifier length keying one escrowed round.</summary>
    public const int MaxMatchIdLength = 128;

    /// <summary>The longest server node identity an escrow records (the cluster node id; empty on a single server).</summary>
    public const int MaxNodeIdLength = 64;

    /// <summary>Maximum claim audit note length stored in persistence.</summary>
    public const int MaxNoteLength = 128;

    /// <summary>Maximum winning seats accepted for one match settlement.</summary>
    public const int MaxWinnersPerMatch = 36;

    /// <summary>Maximum distinct online player numbers supplied to settlement.</summary>
    public const int MaxOnlinePlayers = 36;

    /// <summary>Maximum pending claims flushed during a single login sweep.</summary>
    public const int MaxPendingClaimsPerLogin = 128;

    /// <summary>Maximum stale active escrows recovered during a single startup pass.</summary>
    public const int MaxStaleEscrowsPerStartup = 256;
}

/// <summary>Well-known client actions encoded in <c>gcpUserHostSetting</c> and <c>gspUserHostSetting</c>.</summary>
public enum UserHostSettingAction
{
    /// <summary>Host opened the prize configuration dialog; notifies guests editing began.</summary>
    OpenEdit = 0,

    /// <summary>Host confirmed the room prize amount; broadcasts updated prize to members.</summary>
    ConfirmPrize = 1,

    /// <summary>Non-host member entered a sponsor room and requested current prize bounds.</summary>
    GuestQuery = 2,

    /// <summary>Host member entered a sponsor room and requested current prize bounds.</summary>
    HostQuery = 3,
}

/// <summary>Status codes consumed by <c>gspUserHostStart</c> client handlers.</summary>
public static class UserHostStartStatus
{
    /// <summary>Validation succeeded; clients lock room controls and begin the launch countdown.</summary>
    public const int Started = 0;

    /// <summary>Room state prevented a sponsor match launch.</summary>
    public const int InvalidState = -1;

    /// <summary>Host wallet lacks enough Lucci to cover the configured room prize.</summary>
    public const int InsufficientLucci = -2;

    /// <summary>Teams are unbalanced, empty, or non-referee players are not ready.</summary>
    public const int InvalidTeamsOrReady = -3;

    /// <summary>Host lacks the required host ticket item in inventory (client prompts store lookup).</summary>
    public const int MissingTicket = -4;

    /// <summary>Configured prize or mode is rejected by server policy.</summary>
    public const int PolicyRejected = -5;
}

/// <summary>C2S <c>gcpUserHostSetting</c> request decoded from client <c>action</c> and <c>prize</c> words.</summary>
/// <param name="Action">Requested setting action (<see cref="UserHostSettingAction"/>).</param>
/// <param name="Prize">Proposed prize Lucci when <paramref name="Action"/> is <see cref="UserHostSettingAction.ConfirmPrize"/>.</param>
public sealed record UserHostSettingRequested(int Action, uint Prize) : IMessage;

/// <summary>S2C <c>gspUserHostSetting</c> reply/broadcast carrying current prize and allowed bounds.</summary>
/// <param name="Action">Resulting action code echoed to client handlers.</param>
/// <param name="Prize">Current configured sponsor prize in Lucci.</param>
/// <param name="MinPrize">Configured minimum sponsor prize in Lucci.</param>
/// <param name="MaxPrize">Configured maximum sponsor prize in Lucci.</param>
public sealed record UserHostSettingUpdated(int Action, uint Prize, uint MinPrize, uint MaxPrize) : IMessage;

/// <summary>S2C <c>gspUserHostDefaultSetting</c> notification pushed at login to initialize sponsor prize bounds.</summary>
/// <param name="MinPrize">Configured minimum sponsor prize in Lucci.</param>
/// <param name="MaxPrize">Configured maximum sponsor prize in Lucci.</param>
public sealed record UserHostDefaultsPublished(uint MinPrize, uint MaxPrize) : IMessage;

/// <summary>S2C <c>gspUserHostStart</c> response gating sponsor countdown launch or client error alerts.</summary>
/// <param name="Status">Signed start status (<c>0</c> starts countdown; negative values report alerts).</param>
public sealed record UserHostStartReplied(int Status) : IMessage;

/// <summary>Lifecycle states of one persisted <c>user_host</c> match prize escrow.</summary>
public enum UserHostEscrowState
{
    /// <summary>Prize Lucci and ticket have been debited at match start and await round outcome.</summary>
    Active = 0,

    /// <summary>Match ended with a winning team and payouts have been committed.</summary>
    Settled = 1,

    /// <summary>Match ended in draw/cancellation or startup recovery and full prize was returned to the host.</summary>
    Refunded = 2,
}

/// <summary>Lifecycle states of one persisted offline sponsor prize/refund claim.</summary>
public enum UserHostClaimState
{
    /// <summary>Queued for automatic deposit into the player's wallet on next login.</summary>
    Pending = 0,

    /// <summary>Deposited atomically into the player's wallet.</summary>
    Claimed = 1,
}

/// <summary>Outcome of attempting to lock and fund a sponsor match escrow atomically.</summary>
public enum UserHostEscrowBeginOutcome
{
    /// <summary>Ticket consumed, Lucci debited from host wallet, and active escrow row recorded.</summary>
    Committed = 0,

    /// <summary>Match identifier was already escrowed earlier (idempotent no-op).</summary>
    AlreadyCommitted = 1,

    /// <summary>Host account/wallet row does not exist in persistence.</summary>
    HostMissing = 2,

    /// <summary>Host wallet Lucci balance was below the requested prize.</summary>
    InsufficientLucci = 3,

    /// <summary>Host inventory did not contain an active consumable host ticket item.</summary>
    MissingTicket = 4,
}

/// <summary>Outcome of settling or refunding a persisted sponsor match escrow.</summary>
public enum UserHostSettlementOutcome
{
    /// <summary>Escrow transitioned from active and all online credits / offline claims were persisted.</summary>
    Applied = 0,

    /// <summary>Escrow had already been settled or refunded earlier (idempotent no-op).</summary>
    AlreadyFinalized = 1,

    /// <summary>No escrow row existed for the specified profile and match identifier.</summary>
    NotFound = 2,
}

/// <summary>Atomic command locking host ticket and prize Lucci at sponsor match start.</summary>
/// <param name="ProfileId">Deployment profile/build scope owning the match.</param>
/// <param name="MatchId">Unique match identifier for idempotent escrow and settlement.</param>
/// <param name="RoomId">Room number hosting the sponsor match.</param>
/// <param name="HostUserNum">Host account number paying ticket and prize Lucci.</param>
/// <param name="PrizeLucci">Non-negative prize Lucci escrowed from the host wallet.</param>
/// <param name="FeePercent">Configured prize tax percentage (0..100).</param>
/// <param name="TicketItemId">Consumable item identifier debited from host inventory.</param>
/// <param name="CreatedAtUnixMilliseconds">UTC timestamp when escrow begins.</param>
public sealed record UserHostEscrowBeginCommand(
    string ProfileId,
    string MatchId,
    int RoomId,
    long HostUserNum,
    int PrizeLucci,
    int FeePercent,
    int TicketItemId,
    long CreatedAtUnixMilliseconds)
{
    /// <summary>
    /// Gets the server node running the match: the cluster node id, or empty on a single server. Startup recovery
    /// refunds only its own node's escrows, so a node that restarts never refunds a match another node sharing the
    /// database is still playing.
    /// </summary>
    public string NodeId { get; init; } = "";

    /// <summary>Validates escrow start bounds before opening a transaction.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        if (ProfileId.Length > UserHostLimits.MaxProfileIdLength)
            throw new ArgumentOutOfRangeException(nameof(ProfileId), "Profile identifier exceeds maximum length.");
        ArgumentException.ThrowIfNullOrWhiteSpace(MatchId);
        if (MatchId.Length > UserHostLimits.MaxMatchIdLength)
            throw new ArgumentOutOfRangeException(nameof(MatchId), "Match identifier exceeds maximum length.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RoomId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HostUserNum);
        ArgumentOutOfRangeException.ThrowIfNegative(PrizeLucci);
        if (FeePercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(FeePercent), "Fee percentage must be between 0 and 100.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TicketItemId);
        ArgumentOutOfRangeException.ThrowIfNegative(CreatedAtUnixMilliseconds);
        ArgumentNullException.ThrowIfNull(NodeId);
        if (NodeId.Length > UserHostLimits.MaxNodeIdLength)
            throw new ArgumentOutOfRangeException(nameof(NodeId), "Node identifier exceeds maximum length.");
    }
}

/// <summary>Result of an atomic escrow begin attempt.</summary>
/// <param name="Outcome">Commit status.</param>
/// <param name="RemainingTickets">Remaining host ticket quantity when committed, otherwise null.</param>
/// <param name="HostLucciAfter">Host Lucci balance observed or remaining after debit.</param>
public sealed record UserHostEscrowBeginResult(
    UserHostEscrowBeginOutcome Outcome,
    int? RemainingTickets,
    int HostLucciAfter);

/// <summary>One winning player's net Lucci share allocated from a sponsor match.</summary>
/// <param name="UserNum">Winning player account number.</param>
/// <param name="AmountLucci">Net Lucci payout credited to this winner.</param>
public sealed record UserHostWinnerAllocation(long UserNum, int AmountLucci);

/// <summary>One recipient wallet delta or queued claim produced when finalizing an escrow.</summary>
/// <param name="UserNum">Recipient account number.</param>
/// <param name="AmountLucci">Credited Lucci amount.</param>
/// <param name="CreditedImmediately">Whether the wallet row was updated immediately rather than queued as an offline claim.</param>
/// <param name="WalletLucciAfter">Updated wallet Lucci when credited immediately, otherwise null.</param>
public sealed record UserHostPayoutEntry(
    long UserNum,
    int AmountLucci,
    bool CreditedImmediately,
    int? WalletLucciAfter);

/// <summary>Atomic command settling a won sponsor match or refunding draw/cancellation.</summary>
/// <param name="ProfileId">Deployment profile/build scope owning the match.</param>
/// <param name="MatchId">Unique match identifier originally escrowed at match start.</param>
/// <param name="IsDrawOrCancelled">True to refund the full escrowed prize back to the host instead of paying winners.</param>
/// <param name="WinnerPayouts">Per-winner net allocations when <paramref name="IsDrawOrCancelled"/> is false.</param>
/// <param name="OnlineUserNums">Distinct connected account numbers whose wallets should be credited directly.</param>
/// <param name="SettledAtUnixMilliseconds">UTC timestamp when settlement executes.</param>
public sealed record UserHostSettleCommand(
    string ProfileId,
    string MatchId,
    bool IsDrawOrCancelled,
    IReadOnlyList<UserHostWinnerAllocation> WinnerPayouts,
    IReadOnlyList<long> OnlineUserNums,
    long SettledAtUnixMilliseconds)
{
    /// <summary>Validates settlement allocations and bounds before opening a transaction.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        if (ProfileId.Length > UserHostLimits.MaxProfileIdLength)
            throw new ArgumentOutOfRangeException(nameof(ProfileId), "Profile identifier exceeds maximum length.");
        ArgumentException.ThrowIfNullOrWhiteSpace(MatchId);
        if (MatchId.Length > UserHostLimits.MaxMatchIdLength)
            throw new ArgumentOutOfRangeException(nameof(MatchId), "Match identifier exceeds maximum length.");
        ArgumentNullException.ThrowIfNull(WinnerPayouts);
        ArgumentNullException.ThrowIfNull(OnlineUserNums);
        ArgumentOutOfRangeException.ThrowIfNegative(SettledAtUnixMilliseconds);
        if (WinnerPayouts.Count > UserHostLimits.MaxWinnersPerMatch)
            throw new ArgumentOutOfRangeException(nameof(WinnerPayouts), "Winner allocation count exceeds match limit.");
        if (OnlineUserNums.Count > UserHostLimits.MaxOnlinePlayers)
            throw new ArgumentOutOfRangeException(nameof(OnlineUserNums), "Online player list exceeds room limit.");
        foreach (var winner in WinnerPayouts)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(winner.UserNum);
            ArgumentOutOfRangeException.ThrowIfNegative(winner.AmountLucci);
        }
        foreach (var online in OnlineUserNums)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(online);
    }
}

/// <summary>Result of finalizing a sponsor match escrow.</summary>
/// <param name="Outcome">Settlement transition status.</param>
/// <param name="Payouts">Per-recipient credit or offline-claim dispositions produced during settlement.</param>
public sealed record UserHostSettleResult(
    UserHostSettlementOutcome Outcome,
    IReadOnlyList<UserHostPayoutEntry> Payouts);

/// <summary>Summary of stale active sponsor escrows recovered into offline refund claims at startup.</summary>
/// <param name="RecoveredCount">Number of active escrows transitioned to refunded during this batch.</param>
/// <param name="TotalRefundedLucci">Total escrowed Lucci queued into offline host refund claims.</param>
public sealed record UserHostRecoverResult(int RecoveredCount, long TotalRefundedLucci);

/// <summary>Summary of offline sponsor prize or refund claims flushed into a player wallet on login.</summary>
/// <param name="ClaimedCount">Number of pending claim rows marked claimed.</param>
/// <param name="CreditedLucci">Total Lucci deposited into the player wallet.</param>
/// <param name="WalletAfter">Updated wallet snapshot after deposit, or null when no pending claims existed.</param>
public sealed record UserHostClaimFlushResult(
    int ClaimedCount,
    long CreditedLucci,
    Wallet.PlayerWallet? WalletAfter);

/// <summary>
/// Durable persistence contract for sponsor (<c>user_host</c>) prize escrows and offline claims.
/// Implementations commit ticket consumption + Lucci debit atomically at match start, settle
/// idempotently once at match end, recover interrupted escrows on startup, and flush offline claims
/// atomically on login.
/// </summary>
public interface IUserHostPrizeStore
{
    /// <summary>
    /// Atomically verifies host wallet balance and ticket ownership, consumes one ticket item,
    /// deducts <see cref="UserHostEscrowBeginCommand.PrizeLucci"/> from the host wallet, and records
    /// an active escrow row for <see cref="UserHostEscrowBeginCommand.MatchId"/>.
    /// </summary>
    ValueTask<UserHostEscrowBeginResult> BeginEscrowAsync(UserHostEscrowBeginCommand command, CancellationToken ct);

    /// <summary>
    /// Atomically settles an active sponsor escrow once: distributes net allocations to online winners
    /// or queues offline claims on victory, or refunds the full prize to the host on draw/cancellation.
    /// </summary>
    ValueTask<UserHostSettleResult> SettleEscrowAsync(UserHostSettleCommand command, CancellationToken ct);

    /// <summary>
    /// Transitions up to <paramref name="maxBatch"/> leftover active escrows of <paramref name="profileId"/> that
    /// <paramref name="nodeId"/> began to <see cref="UserHostEscrowState.Refunded"/> and inserts durable pending host
    /// refund claims. Escrows another node began are left to that node: its match may still be running.
    /// </summary>
    ValueTask<UserHostRecoverResult> RecoverActiveEscrowsAsync(
        string profileId,
        string nodeId,
        long recoveredAtUnixMilliseconds,
        int maxBatch,
        CancellationToken ct);

    /// <summary>
    /// Atomically transitions pending sponsor claims for <paramref name="userNum"/> in
    /// <paramref name="profileId"/> to claimed and credits the player's wallet.
    /// </summary>
    ValueTask<UserHostClaimFlushResult> FlushPendingClaimsAsync(
        string profileId,
        long userNum,
        int maxClaims,
        long claimedAtUnixMilliseconds,
        CancellationToken ct);
}

/// <summary>Protocol-scoped wire metadata published by a build's room dialect for sponsor (<c>user_host</c>) prize rooms.</summary>
public interface IUserHostDialectPolicy
{
    /// <summary>Gets the game connection protocol scope.</summary>
    string Protocol { get; }

    /// <summary>Gets the team byte assigned to the sponsor host referee seat (<c>16</c> legacy, <c>36</c> modern).</summary>
    byte RefereeTeam { get; }

    /// <summary>Gets the fixed zero-based seat index reserved for the sponsor host referee (<c>12</c>).</summary>
    int RefereeSlotIndex { get; }
}
