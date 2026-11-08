namespace FantasyTale.Gateway;

/// <summary>Correlates fixed lifecycle events with a local listener without recording client data.</summary>
internal readonly struct ConnectionTrace(IGatewayLog log, bool enabled, string role, int port)
{
    internal void Write(string eventName)
    {
        if (enabled)
            log.Write($"bf-center-selector: connection role={role} port={port} event={eventName}.");
    }
}
