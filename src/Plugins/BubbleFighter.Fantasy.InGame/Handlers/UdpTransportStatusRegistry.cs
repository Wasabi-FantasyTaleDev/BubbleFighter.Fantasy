using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Handlers;

// Rooms must not wait for the UDP rows: those read the rooms' roster, so declaring them as a dependency deadlocks startup.
internal sealed class UdpTransportStatusRegistry(string protocol) : IUdpTransportStatusRegistry
{
    private readonly Lock _gate = new();
    private readonly List<IUdpTransportStatus> _reporters = [];

    public string Protocol => protocol;

    public IDisposable Register(IUdpTransportStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Protocol != protocol) throw new InvalidOperationException("The UDP status reporter belongs to another game protocol.");
        lock (_gate) _reporters.Add(status);
        return new Registration(this, status);
    }

    internal IReadOnlyList<IUdpTransportStatus> Snapshot()
    {
        lock (_gate) return [.. _reporters];
    }

    private sealed class Registration(UdpTransportStatusRegistry owner, IUdpTransportStatus status) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) owner._reporters.Remove(status);
        }
    }
}
