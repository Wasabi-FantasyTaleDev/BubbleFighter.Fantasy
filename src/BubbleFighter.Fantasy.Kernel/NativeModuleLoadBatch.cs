namespace BubbleFighter.Fantasy.Kernel;

internal sealed class NativeModuleLoadBatch(BubbleFighter.Fantasy.Native.NativeModuleLoadBatch batch)
{
    internal string FailurePhase { get => batch.FailurePhase; set => batch.FailurePhase = value; }
    internal static string? GetFailureDiagnostics(Exception error) => BubbleFighter.Fantasy.Native.NativeModuleLoadBatch.GetFailureDiagnostics(error);
    internal static T Execute<T>(string operation, Func<string, string, PluginLoadContext> createContext, Func<NativeModuleLoadBatch, T> load) =>
        BubbleFighter.Fantasy.Native.NativeModuleLoadBatch.Execute(operation, (path, name) => createContext(path, name), shared => load(new(shared)));
    internal IReadOnlyList<PluginDescriptor> Load(string path, string directory) =>
        batch.Load(path, directory).Select(PluginCatalog.AdaptDefinition).ToArray();
}
