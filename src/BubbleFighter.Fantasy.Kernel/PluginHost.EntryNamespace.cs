using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private string? NamespaceUnavailableReason(PluginRow row)
    {
        var tree = Volatile.Read(ref _configuration).Tree;
        if (!tree.Entries.TryGetValue(row.Id, out var entry))
            return $"Runtime namespace for row '{row.Id}' is unavailable.";
        return entry.IsNamespaceReady ? null :
            $"Waiting for isolated_services contract(s): {string.Join(", ", entry.UnavailableIsolatedServices)}.";
    }

    private bool NamespaceIsReady(PluginRow row) => NamespaceUnavailableReason(row) is null;

    private void RequireNamespaceReady(PluginRow row)
    {
        if (NamespaceUnavailableReason(row) is { } reason)
            throw new ConfigException(row.Id, [reason]);
    }
}
