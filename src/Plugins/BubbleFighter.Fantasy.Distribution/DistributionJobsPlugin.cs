using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>Owns supervised download jobs and an exclusive staging directory through ordinary plugin effects.</summary>
[Plugin("distribution.jobs", Requires = [typeof(IDistributionPlans), typeof(IDistributionPackages), typeof(IDistributionTransport)], Provides = [typeof(IDistributionJobs)])]
public sealed class DistributionJobsPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var plans = context.Services.Get<IDistributionPlans>();
        var packages = context.Services.Get<IDistributionPackages>();
        var transport = context.Services.Get<IDistributionTransport>();
        var options = context.Config.Bind<DistributionJobOptions>();
        DistributionJobs? service = null;
        await context.AcquireEffectAsync(_ =>
        {
            service = new(plans, packages, transport, options, context.RunTaskAsync, context.Lifetime);
            return ValueTask.FromResult<IAsyncDisposable>(service);
        }, cancellationToken).ConfigureAwait(false);
        context.Effect(context.Services.Register<IDistributionJobs>(service ?? throw new InvalidOperationException("Download job ownership was not acquired.")));
    }
}

/// <summary>Configures download staging and the operator's optional free-space reserve without a fixed payload-size cutoff.</summary>
public sealed class DistributionJobOptions
{
    /// <summary>Gets or sets the exclusively owned root for download receipts and opaque payload blobs.</summary>
    [Required] public string StagingPath { get; set; } = "data/distribution-staging";

    /// <summary>Gets or sets free bytes to preserve in addition to outstanding download reservations; zero reserves no extra space.</summary>
    [Range(0L, long.MaxValue)] public long FreeSpaceReserveBytes { get; set; }

    /// <summary>Gets or sets the number of jobs in one history response; this does not limit total retained jobs or payload sizes.</summary>
    [Range(1, int.MaxValue)] public int HistoryPageSize { get; set; } = 20;
}
