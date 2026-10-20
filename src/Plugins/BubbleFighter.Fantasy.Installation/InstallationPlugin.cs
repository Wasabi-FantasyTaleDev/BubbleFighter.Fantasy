using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Installation;

namespace BubbleFighter.Fantasy.Installation;

/// <summary>Owns installation authority and administrator sessions as replaceable, unloadable services.</summary>
[Plugin("installation", Requires = [typeof(IInstallationRuntime)], RequiresMany = [typeof(IInstallationDatabase)],
    Provides = [typeof(IInstallationService), typeof(IAdminAccessControl)])]
public sealed class InstallationPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var service = new InstallationService(context, context.Config.Bind<InstallationOptions>(),
            context.Services.Get<IInstallationRuntime>(), context.Services.GetAll<IInstallationDatabase>());
        context.Effect(service);
        context.Effect(context.Services.Register<IInstallationService>(service));
        context.Effect(context.Services.Register<IAdminAccessControl>(service));
        await service.ResumeAsync().ConfigureAwait(false);
    }
}
