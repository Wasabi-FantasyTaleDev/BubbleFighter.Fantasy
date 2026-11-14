using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;
using BubbleFighter.Fantasy.Contracts.Installation;

namespace BubbleFighter.Fantasy.Installation;

/// <summary>Owns installation authority and administrator sessions as replaceable, unloadable services.</summary>
[Plugin("installation", Category = "admin", Names = ["en=Setup assistant", "zh-Hans=安装向导", "zh-Hant=安裝助理", "ko=설치 도우미"], Requires = [typeof(IInstallationRuntime)], RequiresMany = [typeof(IInstallationDatabase)],
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
