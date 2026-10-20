using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Exports the SDK contract inventory as a normal row, without starting a game or an SDK runtime.</summary>
[Plugin("sdk.contracts.export", Description = "Exports shared messages, events, service signatures and SDK representation gaps.")]
public sealed class ContractSchemaExportPlugin : IPlugin
{
    /// <summary>Validates the output destination and writes a deterministic schema of the loaded shared assemblies.</summary>
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var config = context.Row.Config;
        if (config is not null && config.Keys.Any(key => key != "output_path"))
            throw new ConfigException($"Row '{context.Row.Id}': only 'output_path' is supported.");
        var path = "";
        if (config?.TryGetValue("output_path", out var value) == true)
            path = value as string ?? throw new ConfigException($"Row '{context.Row.Id}': 'output_path' must be a string.");
        var document = new ContractSchema().Export().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (path.Length == 0)
            await Console.Out.WriteAsync(document.AsMemory(), cancellationToken).ConfigureAwait(false);
        else
            await File.WriteAllTextAsync(Path.GetFullPath(path), document, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }
}
