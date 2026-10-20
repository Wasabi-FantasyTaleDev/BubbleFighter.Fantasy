using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>
/// Spawns each configured child process, asks it to describe the plugins it serves and publishes them as owned
/// definition sources, so rows can bind plugins written in other languages exactly like native ones.
/// </summary>
[Plugin("loader.process", Description = "Publishes plugin definitions served by child processes over a JSON-lines stdio protocol.",
    Requires = [typeof(IPluginHost)])]
public sealed class RemoteLoaderPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var config = RemoteLoaderConfig.Parse(context.Row);
        var host = context.Services.Get<IPluginHost>();
        foreach (var entry in config.Entries)
        {
            // Registration owns the source as an effect before the producer runs; the producer starts the process.
            await host.RegisterDefinitionSourceAsync(context, entry.SourceId, entry.MaxDefinitions, [],
                ct => RemoteSource.AcquireAsync(entry, config, context.Logger, ct), cancellationToken).ConfigureAwait(false);
        }
    }
}
