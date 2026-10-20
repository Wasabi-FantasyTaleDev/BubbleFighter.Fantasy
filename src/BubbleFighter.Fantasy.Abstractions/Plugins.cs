using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>
/// Marks a class as a plugin. The plugin id is the stable name referenced by the
/// <c>plugin</c> field of profile rows. A plugin class must implement <see cref="IPlugin"/>
/// and have a public parameterless constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PluginAttribute : Attribute
{
    /// <summary>Creates the attribute with the plugin id.</summary>
    /// <param name="id">Stable plugin identifier, for example <c>net.tcp</c>.</param>
    public PluginAttribute(string id) => Id = id;

    /// <summary>Gets the stable plugin identifier.</summary>
    public string Id { get; }

    /// <summary>Gets an optional human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets inclusive client ranges such as kr:1256 or kr:1100-1256.
    /// Empty means the plugin is independent of the client build.</summary>
    public string[] SupportedBuilds { get; init; } = [];

    /// <summary>
    /// Gets the client build families this plugin serves, such as <c>modern</c> or <c>legacy</c>, matched
    /// against the row's <see cref="PluginRow.Family"/>. Empty means any family. A family check lets a new
    /// build of that family, described only by its profile and packet data, use the plugin unchanged.
    /// </summary>
    public string[] SupportedFamilies { get; init; } = [];

    /// <summary>
    /// Gets the service interface types that must be present in the registry before this
    /// plugin may activate. Declared dependencies drive activation order.
    /// </summary>
    public Type[] Requires { get; init; } = [];

    /// <summary>Gets extension interfaces that require at least one provider before activation.</summary>
    public Type[] RequiresMany { get; init; } = [];

    /// <summary>Gets extension interfaces whose committed provider list may be empty.</summary>
    public Type[] OptionalMany { get; init; } = [];

    /// <summary>Gets ordered extension interfaces this row must publish during activation.</summary>
    public Type[] ProvidesMany { get; init; } = [];

    /// <summary>Gets ordered extension interfaces this row may publish for selected configurations.</summary>
    public Type[] ConditionalProvidesMany { get; init; } = [];

    /// <summary>Gets singleton interfaces this row may observe with WhenAvailable. Observation does
    /// not grant ordinary reads, replace committed dependencies, or retain a provider after the callback.</summary>
    public Type[] Observes { get; init; } = [];


    /// <summary>
    /// Gets services that may be absent. Selected providers activate first; availability
    /// is logged. Present optional services participate in unload and reload dependencies.
    /// Optional cycles fail startup instead of choosing a fallback by row order.
    /// </summary>
    public Type[] Optional { get; init; } = [];

    /// <summary>
    /// Gets services this plugin must publish during activation. Failure to publish one
    /// is an activation error and all effects from that activation are unwound.
    /// </summary>
    public Type[] Provides { get; init; } = [];

    /// <summary>
    /// Gets services this plugin publishes only in some configurations. Consumers wait
    /// for the selected row to activate, then observe whether the service was published.
    /// </summary>
    public Type[] ConditionalProvides { get; init; } = [];

    /// <summary>
    /// Gets singleton interfaces this row decorates. Each base must use RegisterDecoratable;
    /// these services are activation dependencies. Every listed layer must be installed,
    /// and undeclared decoration fails activation. Unloading this row removes only its layers.
    /// </summary>
    public Type[] Decorates { get; init; } = [];

    /// <summary>Gets explicit handler replacement targets in rowId:fully.qualified.MessageType form.
    /// Targets are activation and reload dependencies; each declaration must install an override.</summary>
    public string[] Overrides { get; init; } = [];

    /// <summary>Gets message contracts for which this row may install middleware through the remote SDK bridge.</summary>
    public string[] Middleware { get; init; } = [];
}

/// <summary>
/// A unit of behavior that activates into the shared plugin context. Every registration a
/// plugin performs should be returned as an effect (<see cref="IPluginContext.Effect(IDisposable)"/>)
/// so the kernel can unwind it on unload.
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Configures one activation and registers its owned services, subscriptions and resources.
    /// The host publishes staged registrations only after this callback and declaration checks
    /// succeed. Failure starts rollback; cleanup that fails or remains incomplete retains ownership.
    /// </summary>
    /// <param name="context">The plugin's view of the kernel.</param>
    /// <param name="cancellationToken">Cancelled if activation must abort.</param>
    ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken);
}

/// <summary>Lifecycle state of a plugin row instance.</summary>
public enum PluginState
{
    /// <summary>The row was activated and its effects are live.</summary>
    Active,

    /// <summary>The row was unloaded (or is disabled) and has no live effects.</summary>
    Unloaded,

    /// <summary>Activation or owned work failed, and the row holds no effects.</summary>
    Failed,

    /// <summary>The row has closed effect admission and is awaiting resource teardown.</summary>
    Unloading,

    /// <summary>Teardown reported an error; resources cannot be assumed released and restart is refused.</summary>
    CleanupFailed,

    /// <summary>Teardown is still running beyond its observation limit or caller cancellation.</summary>
    CleanupIncomplete,

    /// <summary>Owned background work failed; resources remain owned until explicit teardown settles.</summary>
    WorkFailed,

    /// <summary>The row is desired but currently lacks required services or selected providers.</summary>
    Pending,
}

/// <summary>Snapshot information about a single instantiated plugin row.</summary>
/// <param name="RowId">The row id from the composed profile.</param>
/// <param name="Plugin">The plugin id.</param>
/// <param name="Source">The configuration layer that contributed the row.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Error">The activation, cleanup or incomplete-teardown diagnostic, when present.</param>
public sealed record PluginInstanceInfo(string RowId, string Plugin, string Source, PluginState State, string? Error)
{
    /// <summary>Gets the configured policy for executable-code refresh in the running process.</summary>
    public PluginCodeRefreshPolicy CodeRefreshPolicy { get; init; } = PluginCodeRefreshPolicy.Allow;

    /// <summary>Gets the resolved code's lifetime capability, independently of activation health.</summary>
    public PluginCodeLifetime CodeLifetime { get; init; } = PluginCodeLifetime.Unknown;

    /// <summary>Gets whether reclaiming this code requires process exit; this capability flag is not a service failure.</summary>
    public bool RestartRequiredForReclamation => CodeLifetime == PluginCodeLifetime.ProcessLifetime;

    /// <summary>Gets whether configuration removed this entry while its prior identity or resources remain owned.</summary>
    public bool IsRetiring { get; init; }
    /// <summary>Gets this plugin's declared handler replacement targets.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];
    /// <summary>Gets the plugin assembly's informational release version, including build metadata.</summary>
    public string? Version { get; init; }

    /// <summary>Gets referenced shared contract assembly versions keyed by assembly name.</summary>
    public IReadOnlyDictionary<string, string> ContractVersions { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets supported executable build ranges; empty means build-independent.</summary>
    public IReadOnlyList<string> SupportedBuilds { get; init; } = [];
}

/// <summary>
/// One row of the composed plugin tree: an instance slot bound to a plugin id, its
/// configuration mapping, and the layer that produced it.
/// </summary>
public sealed record PluginRow
{
    /// <summary>Gets validated code-refresh policy, resource capacity and teardown limits for this entry.</summary>
    public PluginLifecycleOptions Lifecycle { get; init; } = new();

    /// <summary>Gets the row id, unique within the composed profile.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the plugin id this row instantiates.</summary>
    public required string Plugin { get; init; }

    /// <summary>Gets an optional space-separated conjunction of release comparisons, such as &gt;=1.2 &lt;2.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the client identity inherited from the profile; required for build-specific plugins.</summary>
    public Protocol.ClientBuild? ClientBuild { get; init; }

    /// <summary>
    /// Gets the profile id followed by its ancestors, nearest first, from the <c>parent</c> links
    /// of <c>profile.yaml</c>. Build-specific code picks the variant of the nearest profile in this
    /// list, so a build without its own variant reuses its closest relative's. Empty for rows
    /// that were not composed from a profile.
    /// </summary>
    public IReadOnlyList<string> Lineage { get; init; } = [];

    /// <summary>
    /// Gets the client build family of the profile, from the nearest profile in <see cref="Lineage"/> that
    /// declares <c>family</c>; <c>legacy</c> when none does. Null for rows composed without a client build.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>Gets the runtime parent entry id. The parent must be active before this entry
    /// can activate, and retains ownership until its descendants finish cleanup.</summary>
    public string? ParentId { get; init; }

    /// <summary>Gets fully qualified service interface names isolated into a fresh namespace
    /// for this entry and its descendants. Other services inherit the parent's namespaces.</summary>
    public IReadOnlyList<string> IsolatedServices { get; init; } = [];

    /// <summary>
    /// Gets the row configuration as a plain mapping (string keys to scalars, nested
    /// mappings, or lists), already env-interpolated. <see langword="null"/> means no config.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Config { get; init; }

    /// <summary>Gets whether the row is disabled. Disabled rows are listed but never activated.</summary>
    public bool Disabled { get; init; }

    /// <summary>Gets a label identifying the configuration layer that produced the row.</summary>
    public required string Source { get; init; }
}

/// <summary>
/// Binds a row's YAML configuration mapping onto a POCO. Keys are written in snake_case and
/// map to PascalCase properties. Binding performs DataAnnotations validation and calls
/// <see cref="IValidatableOptions.Validate"/> when implemented.
/// </summary>
public interface IPluginConfig
{
    /// <summary>Binds the row configuration onto a new instance of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A POCO with a public parameterless constructor.</typeparam>
    /// <returns>The bound and validated options instance.</returns>
    /// <exception cref="ConfigException">Thrown when conversion or validation fails; the
    /// message lists the row id and every failure.</exception>
    T Bind<T>() where T : class, new();
}

/// <summary>Optional extra validation hook for bound options, run after DataAnnotations.</summary>
public interface IValidatableOptions
{
    /// <summary>Returns validation failures, or an empty sequence when the options are valid.</summary>
    IEnumerable<System.ComponentModel.DataAnnotations.ValidationResult> Validate();
}

/// <summary>Thrown when a profile, patch, or plugin configuration is invalid.</summary>
public sealed class ConfigException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A message describing the failure, including the row id when applicable.</param>
    public ConfigException(string message) : base(message) { }

    /// <summary>Creates the exception for one row's binding failures.</summary>
    /// <param name="rowId">The row whose configuration failed.</param>
    /// <param name="failures">The individual failures.</param>
    public ConfigException(string rowId, IReadOnlyList<string> failures)
        : base($"Invalid configuration for row '{rowId}': {string.Join("; ", failures)}")
    {
        RowId = rowId;
        Failures = failures;
    }

    /// <summary>Gets the row id, when the failure is scoped to a row.</summary>
    public string? RowId { get; }

    /// <summary>Gets the individual failures, when available.</summary>
    public IReadOnlyList<string>? Failures { get; }
}

/// <summary>Thrown when a required service is absent from the registry.</summary>
public sealed class ServiceUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="serviceType">The service interface that was requested.</param>
    public ServiceUnavailableException(Type serviceType)
        : base($"Service '{serviceType.FullName}' is not registered.") => ServiceType = serviceType;

    /// <summary>Gets the service interface that was requested.</summary>
    public Type ServiceType { get; }
}

/// <summary>
/// The plugin's view of the kernel. Services are consumed by interface type only; every
/// registration or subscription created through Services and Events automatically belongs
/// to this activation. Passing its handle to Effect again preserves that single ownership.
/// Resources obtained through other service APIs still need explicit effect ownership.
/// </summary>
public interface IPluginContext
{
    /// <summary>Gets handler targets authorized by this activation's PluginAttribute.Overrides declaration.</summary>
    IReadOnlyList<string> OverrideTargets => [];

    /// <summary>Gets the row this instance was activated from.</summary>
    PluginRow Row { get; }

    /// <summary>Gets the activation-owned service registry view. Registrations reserve resource
    /// capacity before publication and are removed during teardown.</summary>
    IServiceRegistry Services { get; }

    /// <summary>Gets the activation-owned event bus view. Subscriptions are tracked automatically,
    /// and teardown waits for their admitted callbacks to finish.</summary>
    IEventBus Events { get; }

    /// <summary>Gets the configuration binder scoped to this row's config mapping.</summary>
    IPluginConfig Config { get; }

    /// <summary>Gets a logger named after the row.</summary>
    ILogger Logger { get; }

    /// <summary>Gets the activation lifetime token. Teardown first closes admission of new
    /// effects, then cancels this token, then drains callbacks and releases owned resources.</summary>
    CancellationToken Lifetime { get; }

    /// <summary>Tracks a disposer for reverse-order teardown. Disposing the returned handle
    /// early joins the same cleanup; removal from inside its own callback does not wait for that
    /// callback to return. Register ownership immediately after acquiring an external resource.</summary>
    /// <param name="disposer">The disposer to run on unload.</param>
    /// <returns>An ownership handle; repeated disposal joins the same cleanup and observes its result.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the row is unloading or unloaded,
    /// after disposing the rejected effect to avoid a resource leak.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the activation's shared effect
    /// capacity is exhausted; the rejected resource is disposed before the error is reported.</exception>
    /// <exception cref="AggregateException">Thrown when both admission and release of the
    /// rejected resource fail; the exception retains both failures.</exception>
    IDisposable Effect(IDisposable disposer);

    /// <summary>Tracks a synchronous cleanup action that runs at most once, either on early
    /// disposal or during teardown. The disposing caller runs the action and observes its error.</summary>
    /// <param name="dispose">The action to run on unload.</param>
    /// <returns>A disposer wrapping the action.</returns>
    IDisposable Effect(Action dispose);

    /// <summary>Reserves ownership before invoking an asynchronous resource factory. The factory
    /// receives the activation lifetime token. A late result is released before admission fails.
    /// Repeated release calls join the same operation and observe the same failure.</summary>
    /// <param name="acquire">Creates a resource and returns the operation that releases it.</param>
    /// <returns>An ownership handle that supports early asynchronous release.</returns>
    ValueTask<IAsyncDisposable> AcquireEffectAsync(Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire);

    /// <summary>Reserves ownership before a cancellable factory. The combined caller and lifetime
    /// token remains valid until resource release. Cancellation before admission reclaims late
    /// results before failing; after admission it signals the token without removing ownership.</summary>
    ValueTask<IAsyncDisposable> AcquireEffectAsync(
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellationToken);

    /// <summary>Creates a child resource lifetime with ownership reserved before construction.
    /// All descendants share this activation's resource capacity; a child is not a service realm.</summary>
    ValueTask<IResourceScope> CreateResourceScopeAsync(string name);

    /// <summary>Reserves resource ownership before starting background work. Normal completion
    /// returns the slot; faults remain owned and are reported to the host. Disposing the returned
    /// task requests cancellation and joins the same work without retrying it.</summary>
    /// <param name="name">A nonempty operation name for diagnostics.</param>
    /// <param name="work">Work that observes its supplied cancellation token.</param>
    /// <param name="cancellationToken">Additional cancellation of the owned operation.</param>
    /// <returns>The supervised task handle, including its observable completion.</returns>
    ValueTask<IOwnedTask> RunTaskAsync(string name, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a row reload: every row that was unloaded and re-activated.
/// </summary>
/// <param name="ReloadedRowIds">The target row first, then any dependent rows that had to
/// be cascaded, in re-activation order.</param>
public sealed record ReloadResult(IReadOnlyList<string> ReloadedRowIds);

/// <summary>
/// Kernel-owned service that enumerates plugin row instances and drives row-level
/// load/unload/reload. Registered by the kernel itself so admin plugins can drive it.
/// Immediate operations on independent dependency scopes may run concurrently. Conflicting
/// immediate operations and lifecycle reentry fail promptly; desired-state requests coalesce.
/// </summary>
public interface IPluginHost
{
    /// <summary>Reserves an activation-owned source before acquisition and admits its complete definition batch atomically.
    /// The returned registration revokes selection before draining consumers. Caller cancellation cancels acquisition or
    /// observation without abandoning owned resources. Alternate hosts may reject this capability.</summary>
    /// <param name="owner">The active or activating context that owns the source registration as an effect.</param>
    /// <param name="sourceId">The diagnostic identity of the source within its owning activation.</param>
    /// <param name="definitionCapacity">The maximum definition count reserved before the producer runs.</param>
    /// <param name="coordinationKeys">The complete non-null coordination keys reserved before backend acquisition.</param>
    /// <param name="acquire">The bounded producer that returns the owned candidate batch.</param>
    /// <param name="cancellationToken">Cancels this caller's acquisition or observation while ownership remains tracked.</param>
    /// <returns>The identity-bearing registration that controls revocation and observes cleanup.</returns>
    ValueTask<IPluginDefinitionRegistration> RegisterDefinitionSourceAsync(IPluginContext owner, string sourceId,
        int definitionCapacity, IReadOnlyList<string> coordinationKeys,
        Func<CancellationToken, ValueTask<IPluginDefinitionBatch>> acquire, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host does not support definition source registration.");

    /// <summary>Gets copied diagnostics for accepted, preparing, or retiring definition sources.</summary>
    IReadOnlyList<PluginDefinitionSourceInfo> DefinitionSources => [];

    /// <summary>Gets a copied snapshot of currently accepted and visible plugin definitions, excluding revoked sources.</summary>
    IReadOnlyList<PluginDefinitionInfo> Definitions => [];

    /// <summary>Accepts a complete desired entry tree within the host's startup capacity.
    /// Added entries use catalog definitions; removed identities remain reserved until owned cleanup drains.
    /// Config, ParentId and IsolatedServices may change for surviving entries; Source is ignored.
    /// Equivalent in-flight submissions join; competing or stale submissions fail before admission.
    /// Cancellation stops observation, not accepted work. Alternate hosts may reject this capability.</summary>
    /// <param name="candidate">The complete desired row set, with fully constructed configuration graphs.</param>
    /// <param name="cancellationToken">Cancels this caller's observation without undoing accepted configuration.</param>
    /// <returns>The configuration revision and its observed convergence result.</returns>
    ValueTask<PluginConfigurationResult> ReconcileConfigurationAsync(IReadOnlyList<PluginRow> candidate, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host does not support configuration reconciliation.");

    /// <summary>Sets a row's desired state without waiting. Requests for the same row coalesce;
    /// dependents suspend before a provider stops and resume when their requirements return.
    /// Accepted work proceeds even when the caller does not observe the receipt.
    /// A restart requires an enabled target; it also starts a currently disabled row.</summary>
    /// <param name="rowId">The configured row identity.</param>
    /// <param name="enabled">Whether the row should be active when its dependencies permit.</param>
    /// <param name="restart">Requests a fresh activation even when the enabled row is already active.</param>
    /// <returns>A receipt that distinguishes convergence, missing dependencies and supersession.</returns>
    /// <exception cref="ArgumentException">Thrown when restart is requested with enabled set to false.</exception>
    IPluginTransition RequestState(string rowId, bool enabled, bool restart = false);

    /// <summary>Gets a copied diagnostic snapshot of known row instances. Entries may retain
    /// incomplete or failed cleanup; an entry's presence does not imply that it is active.</summary>
    IReadOnlyList<PluginInstanceInfo> Instances { get; }

    /// <summary>Unloads and re-activates a row, cascading to any live rows whose declared
    /// requirements consume services the target row published: dependents unload first in
    /// reverse activation order, the target reloads, then dependents re-activate in their
    /// original activation order. For plugins loaded from an isolated load context the
    /// assembly is reloaded as well.</summary>
    /// <param name="rowId">The row to reload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ReloadResult"/> listing every row that was re-activated.</returns>
    ValueTask<ReloadResult> ReloadAsync(string rowId, CancellationToken ct);

    /// <summary>Closes effect admission, cancels the lifetime token, then drains callbacks
    /// and disposes a row's effects in reverse order.
    /// Refuses with <see cref="InvalidOperationException"/> when another live row depends on
    /// a service this row published. Timeout or cancellation of observation retains the activation
    /// in CleanupIncomplete; a later unload joins the same cleanup. CleanupFailed refuses restart.</summary>
    /// <param name="rowId">The row to unload.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask UnloadAsync(string rowId, CancellationToken ct);

    /// <summary>Activates an unloaded row, failing when requirements are missing. It does not
    /// wait for future providers. A newer conflicting target may cancel an in-progress activation.</summary>
    /// <param name="rowId">The row to load.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask LoadAsync(string rowId, CancellationToken ct);
}
