using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>Publishes catalog transports and release discovery as an ordinary owned plugin service.</summary>
[Plugin("distribution.sources", Provides = [typeof(IDistributionSources), typeof(IDistributionTransport)])]
public sealed class DistributionPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<DistributionOptions>();
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        context.Effect(http);
        var service = new DistributionSources(http, options, context.Lifetime);
        context.Effect(context.Services.Register<IDistributionSources>(service));
        context.Effect(context.Services.Register<IDistributionTransport>(service));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Controls public metadata transport budgets and configured catalog roots.</summary>
public sealed class DistributionOptions
{
    /// <summary>Gets or sets the public source rows available to the store.</summary>
    public DistributionSourceOptions[] Sources { get; set; } = [];

    /// <summary>Gets or sets the file holding WebUI source edits; an empty path disables editing.</summary>
    public string StatePath { get; set; } = "data/distribution-sources.json";

    /// <summary>Gets or sets the maximum number of configured sources.</summary>
    [Range(1, 8192)] public int MaximumSources { get; set; } = 512;

    /// <summary>Gets or sets the maximum size of one catalog or API response in bytes.</summary>
    [Range(1024, 67108864)] public int MaximumDocumentBytes { get; set; } = 4194304;

    /// <summary>Gets or sets the total deadline for one metadata operation, including redirects.</summary>
    [Range(1, 300000)] public int RequestTimeoutMs { get; set; } = 30000;

    /// <summary>Gets or sets the maximum number of HTTP redirects per request.</summary>
    [Range(0, 20)] public int MaximumRedirects { get; set; } = 5;

    /// <summary>Gets or sets an optional operator limit for one payload; absent a limit, the publisher's exact size bounds the transfer.</summary>
    [Range(0L, long.MaxValue)] public long? MaximumPayloadBytes { get; set; }

    /// <summary>Gets or sets the total deadline for a payload transfer, including source lookup and redirects.</summary>
    [Range(1, int.MaxValue)] public int TransferTimeoutMs { get; set; } = 1800000;

    /// <summary>Gets or sets the reserved GitHub mirror option; enabling it fails until a provider exists.</summary>
    public bool UseGithubMirror { get; set; }
}

/// <summary>Configures one credential-free HTTP, AList or GitHub catalog.</summary>
public sealed class DistributionSourceOptions
{
    /// <summary>Gets or sets the unique local source identity.</summary>
    [Required] public string Id { get; set; } = "";
    /// <summary>Gets or sets the operator-facing display name.</summary>
    [Required] public string Name { get; set; } = "";
    /// <summary>Gets or sets the transport kind: http, alist or github.</summary>
    [Required] public string Kind { get; set; } = "http";
    /// <summary>Gets or sets the public transport root URL.</summary>
    [Required] public string BaseUrl { get; set; } = "";
    /// <summary>Gets or sets the catalog path relative to the transport root.</summary>
    [Required] public string CatalogPath { get; set; } = "source.toml";
}
