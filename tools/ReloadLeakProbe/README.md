# Assembly-context fixture probe

[中文](README.zh.md)

Build with `dotnet build tools/ReloadLeakProbe/ReloadLeakProbe.csproj -c Release`. Run the resulting DLL with `pure 8` or `leaky 8` to select a synthetic fixture and iteration count.

Each iteration uses a non-collectible assembly load context. The tool reports context counts and process resource observations after collection. It does not exercise PluginHost, prove collectible-module reclamation or establish production memory cost. Use a fresh process for each comparison.
