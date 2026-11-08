namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    private CancellationTokenSource? _battleSettlementCancellation;
    private Task _battleSettlementCancellationTask = Task.CompletedTask;

    // A terminal series may be decided between presentation sequences. Its current executor operation
    // owns this short settlement independently of the leaving connection and of the animation's source.
    internal CancellationTokenSource BeginBattleSettlement(CancellationToken lifetime)
    {
        if (_battleSettlementCancellation is not null || IsStopping)
            throw new InvalidOperationException($"Room {Id} cannot begin another battle settlement.");
        _battleSettlementCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _battleSettlementCancellationTask = Task.CompletedTask;
        return _battleSettlementCancellation;
    }

    internal void CancelBattleSettlement()
    {
        if (_battleSettlementCancellation is { IsCancellationRequested: false } source)
            _battleSettlementCancellationTask = source.CancelAsync();
    }

    internal async Task ReleaseBattleSettlementAsync(CancellationTokenSource source)
    {
        Task cancellation;
        lock (Gate)
        {
            cancellation = _battleSettlementCancellationTask;
            if (ReferenceEquals(_battleSettlementCancellation, source)) _battleSettlementCancellation = null;
        }
        try { await cancellation.ConfigureAwait(false); }
        finally { source.Dispose(); }
    }
}
