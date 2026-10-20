using System.Net;

namespace BubbleFighter.Fantasy.Launcher.Http;

// Only the transport peer is trusted; forwarding headers do not allocate request windows.
internal sealed class LauncherRequestBudget(int requestsPerMinute, int maximumAddresses, TimeProvider? clock = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Window> _windows = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private sealed record Window(long Start, int Requests);

    internal bool TryTake(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (!_windows.TryGetValue(address, out var window))
            {
                if (_windows.Count >= maximumAddresses)
                    _windows.Remove(_windows.MinBy(pair => pair.Value.Start).Key);
                window = new(now, 0);
            }
            else if (_clock.GetElapsedTime(window.Start, now) >= TimeSpan.FromMinutes(1))
                window = new(now, 0);
            if (window.Requests >= requestsPerMinute) return false;
            _windows[address] = window with { Requests = window.Requests + 1 };
            return true;
        }
    }
}
