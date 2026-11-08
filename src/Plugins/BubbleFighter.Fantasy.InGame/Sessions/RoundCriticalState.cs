using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

internal sealed class RoundCriticalState(ISession relay, MatchSeedTablesRequested opening, CancellationToken lifetime) : IDisposable
{
    private int _disposed;
    private readonly object _timerGate = new();
    private bool _timerReleased;
    internal ISession Relay { get; } = relay;
    internal MatchSeedTablesRequested Opening { get; } = opening;
    internal MatchCriticalSeedUpdate Current { get; set; } = new(opening.RoomId, opening.Generation, opening.EntityId,
        0, opening.Equipment ?? throw new InvalidOperationException("Live critical state requires opening equipment."));
    internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    internal long ActiveUntil { get; set; }
    internal bool OpeningCompleted { get; set; }
    internal bool TimerStarted { get; set; }
    internal Exception? Failure { get; set; }
    internal SemaphoreSlim TimerChanged { get; } = new(0, 1);
    internal Task TimerTask { get; set; } = Task.CompletedTask;

    internal void WakeTimer()
    {
        lock (_timerGate)
        {
            if (!_timerReleased && TimerChanged.CurrentCount == 0) TimerChanged.Release();
        }
    }

    /// <summary>Cancels the single owned expiry loop when the seat or round ends.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancellation.Cancel();
        if (!TimerStarted) ReleaseTimer();
        Cancellation.Dispose();
    }

    internal void ReleaseTimer()
    {
        lock (_timerGate)
        {
            if (_timerReleased) return;
            _timerReleased = true;
            TimerChanged.Dispose();
        }
    }
}
