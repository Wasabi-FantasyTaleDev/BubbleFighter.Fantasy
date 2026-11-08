using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Protocol;

internal sealed class InitialHandshakeContinuation : IInitialHandshakeContinuation, IDisposable
{
    private int _state;

    internal bool Complete() => Interlocked.Exchange(ref _state, 2) == 1;

    public void ContinueWithoutVersionVerification()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Initial handshake continuation is no longer available.");
    }

    public void Dispose() => Interlocked.Exchange(ref _state, 2);
}
