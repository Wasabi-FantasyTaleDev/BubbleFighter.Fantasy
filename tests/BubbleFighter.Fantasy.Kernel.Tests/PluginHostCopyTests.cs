using System.Reflection;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Native;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>The packet tables and their YAML reader are the host's copy only when their bytes are the host's.</summary>
public sealed class PluginHostCopyTests
{
    /// <summary>
    /// An identical copy in a plugin directory resolves to the assembly the host loaded, while a copy that differs in
    /// one byte stays the plugin's own, so a plugin that ships another version keeps it.
    /// </summary>
    [Fact]
    public void OnlyAByteIdenticalDependencyIsShared()
    {
        var name = typeof(YamlDotNet.Serialization.DeserializerBuilder).Assembly.GetName();
        var hostFile = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
        var directory = Path.Combine(Path.GetTempPath(), "bff-host-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identical = Path.Combine(directory, "identical", name.Name + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(identical)!);
            File.Copy(hostFile, identical);
            var shared = NativePluginLoadContext.HostCopy(new AssemblyName(name.Name!), identical);
            Assert.Same(typeof(YamlDotNet.Serialization.DeserializerBuilder).Assembly, shared);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(shared!));

            var different = Path.Combine(directory, "different", name.Name + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(different)!);
            var bytes = File.ReadAllBytes(hostFile);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(different, bytes);
            Assert.Null(NativePluginLoadContext.HostCopy(new AssemblyName(name.Name!), different));

            Assert.Null(NativePluginLoadContext.HostCopy(new AssemblyName("NoSuchHostAssembly"), identical));

            // A byte-identical copy of any other host assembly stays the plugin's own: plugin code keeps its context.
            var kernel = typeof(PluginHost).Assembly.GetName().Name!;
            var kernelCopy = Path.Combine(directory, "kernel", kernel + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(kernelCopy)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, kernel + ".dll"), kernelCopy);
            Assert.Null(NativePluginLoadContext.HostCopy(new AssemblyName(kernel), kernelCopy));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
