using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    /// <inheritdoc />
    public IReadOnlyList<PluginDefinitionInfo> Definitions
    {
        get
        {
            PluginDescriptor[] definitions;
            lock (_intentGate)
            {
                var snapshot = _configuration;
                definitions = snapshot.Catalog.Plugins.Where(definition =>
                    !snapshot.Sources.TryGetValue(definition.Id, out var source) || source.IsPublished).ToArray();
            }
            return Array.AsReadOnly(definitions.Select(definition => new PluginDefinitionInfo(definition.Id,
                definition.Description, definition.Version, Array.AsReadOnly(definition.SupportedBuilds.ToArray()),
                definition.Origin)).ToArray());
        }
    }
}
