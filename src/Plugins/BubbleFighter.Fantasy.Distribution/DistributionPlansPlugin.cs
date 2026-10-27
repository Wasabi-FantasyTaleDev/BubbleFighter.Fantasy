using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Distribution;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>Owns source-specific distribution selections as a separate ordinary plugin row.</summary>
[Plugin("distribution.plans", Requires = [typeof(IDistributionSources)], Provides = [typeof(IDistributionPlans)])]
public sealed class DistributionPlansPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var service = new DistributionPlans(context.Services.Get<IDistributionSources>(),
            context.Config.Bind<DistributionPlanOptions>(), context.Lifetime);
        context.Effect(service);
        context.Effect(context.Services.Register<IDistributionPlans>(service));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Controls storage and expiration of pending distribution selections.</summary>
public sealed class DistributionPlanOptions
{
    /// <summary>Gets or sets the maximum simultaneous pending plans; full capacity refuses new plans.</summary>
    [Range(1, 65536)] public int MaximumPlans { get; set; } = 256;
    /// <summary>Gets or sets the lifetime of a pending selection before a fresh preview is required.</summary>
    [Range(1, 86400)] public int PlanLifetimeSeconds { get; set; } = 1800;
}

internal sealed class DistributionPlans(IDistributionSources sources, DistributionPlanOptions options,
    CancellationToken lifetime = default, TimeProvider? clock = null) : IDistributionPlans, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DistributionPlan> _plans = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _reservations;
    private bool _disposed;

    public async ValueTask<DistributionPlan> CreateAsync(DistributionPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Package);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceId);
        if (request.SelectionFingerprint is not { Length: 64 } || request.SelectionFingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("A previously browsed selection fingerprint is required.");
        // Client packages are previews only; a server plan must not become a client installer.
        if (request.Package.Target != "server" || request.Package.Kind is not ("plugin" or "application"))
            throw new ArgumentException("Only server plugin or application selections can create a plan.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        linked.Token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Expire();
            if (_plans.Count + _reservations >= options.MaximumPlans) throw new InvalidOperationException("distribution_plan_capacity");
            _reservations++;
        }
        try
        {
            var (source, choice, asset) = await ResolveAsync(request, linked.Token).ConfigureAwait(false);
            var created = _clock.GetUtcNow();
            var plan = new DistributionPlan(Guid.NewGuid().ToString("N"), created, created.AddSeconds(options.PlanLifetimeSeconds),
                request.SourceRevision, source, choice, asset);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                linked.Token.ThrowIfCancellationRequested();
                _plans.Add(plan.Id, plan);
            }
            return plan;
        }
        finally { lock (_gate) _reservations--; }
    }

    public async ValueTask<DistributionPlan> ValidateAsync(string planId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        DistributionPlan plan;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Expire();
            if (!_plans.TryGetValue(planId, out plan!)) throw new KeyNotFoundException("The plan is missing or expired.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        linked.Token.ThrowIfCancellationRequested();
        await ResolveAsync(new(plan.SourceRevision, plan.Source.Id, plan.Selection.Package.Identity,
            plan.Selection.SelectionFingerprint, plan.Asset?.Id), linked.Token).ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Expire();
            if (!_plans.TryGetValue(plan.Id, out var current) || !ReferenceEquals(current, plan))
                throw new InvalidOperationException("distribution_plan_withdrawn");
        }
        return plan;
    }

    private async Task<(DistributionSource, DistributionDownloadChoice, RepositoryAsset?)> ResolveAsync(DistributionPlanRequest request, CancellationToken token)
    {
        var settings = sources.Settings;
        if (settings.Revision != request.SourceRevision) throw new InvalidOperationException("distribution_source_changed");
        var source = settings.Sources.SingleOrDefault(row => row.Id == request.SourceId)
            ?? throw new InvalidOperationException("distribution_source_changed");
        var catalog = await sources.BrowseAsync(request.SourceId, token).ConfigureAwait(false);
        if (catalog.Failures.Count != 0) throw new InvalidOperationException("distribution_source_unavailable");
        if (catalog.SourceRevision != request.SourceRevision || sources.Settings.Revision != request.SourceRevision)
            throw new InvalidOperationException("distribution_source_changed");
        var choice = catalog.Entries.SingleOrDefault(entry => entry.Identity == request.Package)?.Downloads
            .SingleOrDefault(offer => offer.SourceId == request.SourceId);
        if (choice is null || choice.SelectionFingerprint != request.SelectionFingerprint)
            throw new InvalidOperationException("distribution_selection_changed");
        RepositoryAsset? asset = null;
        if (choice.Package.RepositoryRelease is { } release)
        {
            if (request.RepositoryAssetId is null) throw new ArgumentException("Choose a repository attachment explicitly.");
            asset = release.Assets.SingleOrDefault(row => row.Id == request.RepositoryAssetId)
                ?? throw new InvalidOperationException("distribution_attachment_changed");
        }
        else if (request.RepositoryAssetId is not null) throw new ArgumentException("This offer does not contain repository attachments.");
        return (source, choice, asset);
    }

    public bool Remove(string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _plans.Remove(planId); }
    }

    private void Expire()
    {
        var now = _clock.GetUtcNow();
        foreach (var id in _plans.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) _plans.Remove(id);
    }

    public void Dispose() { lock (_gate) { _disposed = true; _plans.Clear(); } }
}
