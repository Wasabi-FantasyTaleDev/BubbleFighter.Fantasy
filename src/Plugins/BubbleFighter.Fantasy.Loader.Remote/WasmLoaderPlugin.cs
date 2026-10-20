using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>
/// Runs each configured WebAssembly module inside the host, asks it to describe the plugins it serves and publishes them
/// as owned definition sources. A module speaks the same SDK plugin protocol as a <c>loader.process</c> child, so a
/// plugin built with an SDK can run either way; <see cref="WasmChannel"/> lists the imports a module uses.
/// </summary>
[Plugin("loader.wasm", Description = "Publishes plugin definitions served by sandboxed WebAssembly modules inside the host.",
    Requires = [typeof(IPluginHost)])]
public sealed class WasmLoaderPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var config = RemoteLoaderConfig.Parse(context.Row, wasm: true);
        var host = context.Services.Get<IPluginHost>();
        foreach (var entry in config.Entries)
        {
            // Registration owns the source as an effect before the producer runs; the producer starts the module.
            await host.RegisterDefinitionSourceAsync(context, entry.SourceId, entry.MaxDefinitions, [],
                ct => RemoteSource.AcquireAsync(entry, config, context.Logger, ct), cancellationToken).ConfigureAwait(false);
        }
    }
}
