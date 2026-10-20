using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Process-local gifts. Every gift is kept in one list under one leaf lock; the sender's player gate is taken first
/// (the order <see cref="MemoryShopPurchaseStore"/> uses for the receiver), so a send and a delivery never wait on each
/// other in a cycle. The list is bounded by the per-player cap each send carries and by the player table's own bound;
/// system rewards add at most one claim and a few gifts per reward key, which the operator's reward table bounds.
/// </summary>
public sealed class MemoryPresentStore(MemoryPlayerTable table, MemoryPlayerRepository players) : IPresentStore
{
    private readonly object _gate = new();
    private readonly List<PresentRecord> _rows = [];
    private readonly HashSet<(string Profile, long UserNum, string Key)> _claims = [];
    private long _lastId;

    /// <inheritdoc />
    public ValueTask<PresentPlayer?> FindPlayerAsync(string nickname, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(players.FindPresentPlayer(nickname));
    }

    /// <inheritdoc />
    public ValueTask<PresentSendResult> SendAsync(PresentSendCommand command, CancellationToken ct)
    {
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var sender = table.GetOrAdd(command.Sender.UserNum);
        lock (sender.Gate)
        {
            if (table.Find(command.Receiver.UserNum) is null)
                return Done(PresentSendOutcome.ReceiverMissing);
            var wallet = sender.Money ?? command.SeedWallet;
            lock (_gate)
            {
                if (Visible(command.ProfileId, command.Receiver.UserNum, PresentBox.Received).Count() >= command.MaxPerPlayer)
                    return Done(PresentSendOutcome.ReceiverBoxFull);
                if (Visible(command.ProfileId, command.Sender.UserNum, PresentBox.Sent).Count() >= command.MaxPerPlayer)
                    return Done(PresentSendOutcome.SenderBoxFull);
                var cash = command.MoneyType == "cash";
                var balance = cash ? wallet.Cash : wallet.Lucci;
                if (balance < command.Price)
                    return Done(PresentSendOutcome.InsufficientFunds);

                var charged = cash ? wallet with { Cash = balance - command.Price } : wallet with { Lucci = balance - command.Price };
                var audit = new ShopPurchaseRecord(command.Sender.UserNum, command.StockSetId, command.ItemId,
                    command.MoneyType, command.Price, 0, command.StockKey);
                var log = sender.Purchases.Prepend(audit).Take(MemoryShopPurchaseStore.MaxRowsPerPlayer).ToList();
                var present = new PresentRecord(checked(++_lastId), command.ProfileId, command.Sender.UserNum,
                    command.Sender.Nickname, command.Receiver.UserNum, command.Receiver.Nickname, command.StockKey,
                    command.Message, command.SentAtUnixMilliseconds, PresentState.Pending);
                ct.ThrowIfCancellationRequested();
                _rows.Add(present);
                sender.Money = charged;
                sender.Purchases = log;
                return ValueTask.FromResult(new PresentSendResult(PresentSendOutcome.Sent, present, charged));
            }
        }

        static ValueTask<PresentSendResult> Done(PresentSendOutcome outcome) =>
            ValueTask.FromResult(new PresentSendResult(outcome, null, null));
    }

    /// <inheritdoc />
    public ValueTask<PresentRewardResult> GrantRewardAsync(PresentRewardCommand command, CancellationToken ct)
    {
        command.Validate();
        ct.ThrowIfCancellationRequested();
        if (table.Find(command.ReceiverUserNum) is not { } receiver)
            return ValueTask.FromResult(new PresentRewardResult(PresentRewardOutcome.ReceiverMissing, []));
        lock (_gate)
        {
            var claim = (command.ProfileId, command.ReceiverUserNum, command.ClaimKey);
            if (_claims.Contains(claim))
                return ValueTask.FromResult(new PresentRewardResult(PresentRewardOutcome.AlreadyClaimed, []));
            if (_lastId > PresentLimits.MaxId - command.StockKeys.Count)
                throw new InvalidOperationException("Gift identities have exhausted the client's unsigned word range.");
            var granted = new List<PresentRecord>(command.StockKeys.Count);
            foreach (var key in command.StockKeys)
            {
                // The system has no sent list, so the sender's view starts hidden.
                var present = new PresentRecord(_lastId + granted.Count + 1, command.ProfileId, PresentLimits.SystemSenderUserNum,
                    command.SenderNickname, command.ReceiverUserNum, receiver.Nickname, key, command.Message,
                    command.SentAtUnixMilliseconds, PresentState.Pending) { SenderHidden = true };
                granted.Add(present);
            }
            _rows.AddRange(granted);
            _claims.Add(claim);
            _lastId += granted.Count;
            return ValueTask.FromResult(new PresentRewardResult(PresentRewardOutcome.Granted, granted));
        }
    }

    /// <inheritdoc />
    public ValueTask<PresentPage> ListReceivedAsync(string profileId, long userNum, int start, int count, CancellationToken ct) =>
        List(profileId, userNum, PresentBox.Received, start, count, ct);

    /// <inheritdoc />
    public ValueTask<PresentPage> ListSentAsync(string profileId, long userNum, int start, int count, CancellationToken ct) =>
        List(profileId, userNum, PresentBox.Sent, start, count, ct);

    private ValueTask<PresentPage> List(string profileId, long userNum, PresentBox box, int start, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, PresentLimits.MaxPageCount);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var all = Visible(profileId, userNum, box).OrderByDescending(row => row.Id).ToList();
            return ValueTask.FromResult(new PresentPage(all.Count, all.Skip(start).Take(count).ToArray()));
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CountPendingAsync(string profileId, long userNum, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(Visible(profileId, userNum, PresentBox.Received).Count(row => row.State == PresentState.Pending));
    }

    /// <inheritdoc />
    public ValueTask<PresentRecord?> FindReceivedAsync(string profileId, long userNum, long id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(Visible(profileId, userNum, PresentBox.Received).FirstOrDefault(row => row.Id == id));
    }

    /// <inheritdoc />
    public ValueTask<bool> DiscardAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        Change(profileId, userNum, id, PresentBox.Received, pendingOnly: true, ct);

    /// <inheritdoc />
    public ValueTask<bool> DeleteReceivedAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        Change(profileId, userNum, id, PresentBox.Received, pendingOnly: false, ct);

    /// <inheritdoc />
    public ValueTask<bool> DeleteSentAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        Change(profileId, userNum, id, PresentBox.Sent, pendingOnly: false, ct);

    private ValueTask<bool> Change(string profileId, long userNum, long id, PresentBox box, bool pendingOnly, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var index = _rows.FindIndex(row => row.Id == id && row.ProfileId == profileId && IsVisible(row, userNum, box));
            if (index < 0 || pendingOnly && _rows[index].State != PresentState.Pending)
                return ValueTask.FromResult(false);
            var row = _rows[index];
            row = box == PresentBox.Received
                ? row with
                {
                    ReceiverHidden = true,
                    State = row.State == PresentState.Pending && pendingOnly ? PresentState.Discarded : row.State,
                }
                : row with { SenderHidden = true };
            if (row.SenderHidden && row.ReceiverHidden)
                _rows.RemoveAt(index);
            else
                _rows[index] = row;
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    /// Marks a gift taken, for the purchase that delivers it. The caller holds the receiving player's gate. Throws
    /// <see cref="PresentUnavailableException"/> without changing anything when the gift is not pending for the player.
    /// </summary>
    internal void AcceptLocked(long id, long receiverUserNum)
    {
        lock (_gate)
        {
            var index = _rows.FindIndex(row => row.Id == id && row.ReceiverUserNum == receiverUserNum
                && !row.ReceiverHidden && row.State == PresentState.Pending);
            if (index < 0)
                throw new PresentUnavailableException(id);
            _rows[index] = _rows[index] with { State = PresentState.Accepted };
        }
    }

    private IEnumerable<PresentRecord> Visible(string profileId, long userNum, PresentBox box) =>
        _rows.Where(row => row.ProfileId == profileId && IsVisible(row, userNum, box));

    private static bool IsVisible(PresentRecord row, long userNum, PresentBox box) => box == PresentBox.Received
        ? row.ReceiverUserNum == userNum && !row.ReceiverHidden
        : row.SenderUserNum == userNum && !row.SenderHidden;
}
