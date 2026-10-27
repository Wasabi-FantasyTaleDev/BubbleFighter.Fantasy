using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Administration;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>Provides the shared native command pipeline; transports remain separate plugin adapters.</summary>
[Plugin("commands", Requires = [typeof(IPermissionService), typeof(IPermissionRegistry)],
    Provides = [typeof(ICommandRegistry), typeof(ICommandExecutor)])]
public sealed class CommandsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var service = new CommandService(context, context.Services.Get<IPermissionService>(), context.Services.Get<IPermissionRegistry>(), context.Config.Bind<CommandOptions>());
        context.Effect(service);
        context.Effect(context.Services.Register<ICommandRegistry>(service));
        context.Effect(context.Services.Register<ICommandExecutor>(service));
        return ValueTask.CompletedTask;
    }
}
