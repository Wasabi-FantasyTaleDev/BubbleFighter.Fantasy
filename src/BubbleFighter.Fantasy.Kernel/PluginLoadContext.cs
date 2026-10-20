using BubbleFighter.Fantasy.Native;
namespace BubbleFighter.Fantasy.Kernel;
/// <summary>Preserves the native context compatibility API while the backend owns dependency resolution.</summary>
/// <remarks>Public contexts retain process-lifetime code; only controlled tests request collectible contexts.</remarks>
public sealed class PluginLoadContext : NativePluginLoadContext
{
    /// <summary>Creates a native module context with process-lifetime code retention.</summary>
    public PluginLoadContext(string mainAssemblyPath, string name) : base(mainAssemblyPath, name) { }
    internal PluginLoadContext(string mainAssemblyPath, string name, bool isCollectible)
        : base(mainAssemblyPath, name, isCollectible) { }
}
