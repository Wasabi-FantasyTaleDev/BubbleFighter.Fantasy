using BubbleFighter.Fantasy.Abstractions;
using SharedFactory = BubbleFighter.Fantasy.Native.NativePluginExecutionFactory;

namespace BubbleFighter.Fantasy.Kernel;

internal sealed class NativePluginExecutionFactory : IPluginExecutionBindingFactory, IPluginModuleSource
{
    private readonly SharedFactory _shared;

    internal NativePluginExecutionFactory(Type type, string origin, string? mainAssemblyPath,
        Func<string, string, PluginDescriptor>? loadDescriptor = null)
    {
        _shared = new SharedFactory(type, origin, mainAssemblyPath, loadDescriptor is null ? null : (path, directory) =>
        {
            var descriptor = loadDescriptor(path, directory);
            if (descriptor.Type is null) throw new ConfigException("A native refresh did not expose its resolved implementation type.");
            return new PluginDefinition(descriptor.Declaration, new SharedFactory(descriptor.Type, descriptor.Origin, descriptor.MainAssemblyPath),
                descriptor.Origin, descriptor.Version, descriptor.ContractVersions);
        });
    }

    public string? CoordinationKey => _shared.CoordinationKey;
    public PluginModuleCode ModuleCode => _shared.ModuleCode;
    internal static PluginModuleCode ModuleCodeFor(Type type, string? path) => SharedFactory.ModuleCodeFor(type, path);
    internal static string? GetCoordinationKey(string? path) => SharedFactory.GetCoordinationKey(path);
    public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
        _shared.CreateAsync(reload, cancellationToken);
}
