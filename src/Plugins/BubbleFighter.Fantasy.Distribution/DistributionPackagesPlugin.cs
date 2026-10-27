using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>Provides source-bound file preparation as an ordinary service without loading downloaded code.</summary>
[Plugin("distribution.packages", Requires = [typeof(IDistributionPlans), typeof(IDistributionTransport)], Provides = [typeof(IDistributionPackages)])]
public sealed class DistributionPackagesPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var packages = new DistributionPackages(context.Services.Get<IDistributionPlans>(), context.Services.Get<IDistributionTransport>(),
            context.Config.Bind<DistributionPackageOptions>(), context.Lifetime);
        context.Effect(context.Services.Register<IDistributionPackages>(packages));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Controls explicit opt-in to release formats that are not yet an agreed shared contract.</summary>
public sealed class DistributionPackageOptions
{
    /// <summary>Gets or sets whether the experimental neutral release reader is enabled; existing application manifests do not need this option.</summary>
    public bool ExperimentalReleaseFormat { get; set; }
}
