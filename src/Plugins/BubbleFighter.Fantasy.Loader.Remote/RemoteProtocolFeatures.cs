namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Keeps SDK negotiation and the exported contract inventory on the same implemented capabilities.</summary>
internal static class RemoteProtocolFeatures
{
    internal static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[]
    {
        "services.call", "services.provide", "services.decorate", "services.get_all",
        "services.inner", "services.invoke", "services.cancel",
        "messages.override", "messages.use", "messages.next", "messages.invoke", "messages.cancel",
        "events.on", "events.publish", "events.next", "events.invoke", "events.cancel",
        "tasks.start", "tasks.wait", "tasks.dispose", "tasks.invoke", "tasks.cancel",
        "resources.scope", "resources.scope.dispose", "resources.acquire", "resources.dispose",
        "resources.invoke", "resources.cancel", "lifecycle.cancel",
    });
}
