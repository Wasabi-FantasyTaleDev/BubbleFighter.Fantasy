using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Host;

internal sealed class ServerRestartService(OwnedShutdownSignal shutdown) : IServerRestartService
{
    private int _requested;

    public void RequestRestart()
    {
        if (Interlocked.CompareExchange(ref _requested, 1, 0) != 0)
            throw new InvalidOperationException("A server restart is already pending.");
        shutdown.RequestRestart();
    }
}
