namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>
/// The shared service registry. Plugins publish services by interface type and consume
/// other plugins' services by interface type only. Single and multiple registrations
/// are distinct modes and cannot be mixed for the same service type.
/// Hosted contexts restrict access to declared contracts and retain their activation view;
/// successful teardown revokes the facade after owned work and effects have drained.
/// Hosted activation contributions remain private until the activation commits; the owner
/// can read its own staged services. Availability observers run after that commit.
/// Registry operations synchronize access to registration metadata; registered service
/// implementations remain responsible for their own threading contract.
/// </summary>
public interface IServiceRegistry
{
    /// <summary>Registers a service instance under <typeparamref name="TService"/>.
    /// A hosted activation accepts registrations while effect admission remains open,
    /// including from its owned background work; disposal removes the registration once.</summary>
    /// <typeparam name="TService">The service interface type.</typeparam>
    /// <param name="instance">The implementation instance.</param>
    /// <returns>A disposer that removes the registration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the type is already registered.</exception>
    /// <exception cref="AggregateException">For an immediately published registration, thrown after notifying pending availability callbacks
    /// when any callback failed. The registration is removed before throwing; callback side effects
    /// cannot be rolled back.</exception>
    IDisposable Register<TService>(TService instance) where TService : class;

    /// <summary>
    /// Registers a singleton behind a stable forwarding reference that supports dynamic decorators.
    /// Direct registry reads return a live reference; hosted reads retain their selected projection.
    /// Direct references see later
    /// chain edits; calls already in flight retain their snapshot. Removal invalidates new calls.
    /// </summary>
    /// <typeparam name="TService">A closed interface without pointer, byref-like or ref-return methods.</typeparam>
    /// <param name="instance">The base implementation, owned by the caller.</param>
    /// <returns>A handle removing the base registration and its chain.</returns>
    IDisposable RegisterDecoratable<TService>(TService instance) where TService : class;

    /// <summary>
    /// Adds a wrapper to a decoratable singleton. Higher priority is outermost; ties keep
    /// registration order. The factory runs once and must not call its inner reference or mutate
    /// registrations. Removal affects live references, not an already committed activation projection.
    /// Wrappers remain caller-owned; removal does not drain calls already in flight or dispose
    /// their resources. Use stateless wrappers or arrange resource lifetime outside the factory.
    /// </summary>
    /// <typeparam name="TService">The opted-in service interface.</typeparam>
    /// <param name="factory">Builds a wrapper around the supplied forwarding reference.</param>
    /// <param name="priority">Higher values wrap lower values; equal values preserve registration order.</param>
    /// <returns>A handle removing only this layer.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the service uses ordinary or multiple registration.</exception>
    IDisposable Decorate<TService>(Func<TService, TService> factory, int priority = 0) where TService : class;

    /// <summary>Adds one implementation to an explicit ordered extension point.</summary>
    /// <typeparam name="TService">The extension interface type.</typeparam>
    /// <param name="instance">The implementation to append.</param>
    /// <param name="priority">Higher priorities appear first; ties retain registration order.</param>
    /// <returns>An ownership handle removing only this registration, even for repeated instances.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a singleton or pending singleton
    /// availability subscription already uses this type.</exception>
    IDisposable RegisterMany<TService>(TService instance, int priority = 0) where TService : class;

    /// <summary>Returns a snapshot of the explicit multiple registrations, or an empty list.</summary>
    /// <typeparam name="TService">The extension interface type.</typeparam>
    /// <returns>Implementations ordered by descending priority then registration order.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the type has a singleton registration.</exception>
    IReadOnlyList<TService> GetAll<TService>() where TService : class;

    /// <summary>Returns the registered singleton. Hosted reads select this activation's
    /// declared dependency or its own staged registration before publication.</summary>
    /// <typeparam name="TService">The service interface type.</typeparam>
    /// <returns>The registered instance.</returns>
    /// <exception cref="ServiceUnavailableException">Thrown when no service is registered.</exception>
    /// <exception cref="InvalidOperationException">Thrown for an undeclared dependency or
    /// a registration mode incompatible with a singleton read.</exception>
    TService Get<TService>() where TService : class;

    /// <summary>Attempts a singleton read, enforcing declared access in a hosted context;
    /// missing services return false, while invalid access throws.</summary>
    /// <typeparam name="TService">The service interface type.</typeparam>
    /// <param name="service">Receives the instance, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a service was registered.</returns>
    /// <exception cref="InvalidOperationException">Thrown for invalid dependency access or
    /// registration mode; absence is the only condition represented by a false result.</exception>
    bool TryGet<TService>(out TService? service) where TService : class;

    /// <summary>
    /// Invokes <paramref name="callback"/> with the service instance: immediately when one is
    /// already published, otherwise when one becomes ready. Hosted publication does not roll
    /// back a committed provider because an observer failed; the observer fault is reported. The returned disposer
    /// cancels a callback that has not begun, including one awaiting dispatch after registration.
    /// Each subscription fires at most once; cancellation does not wait for a running callback.
    /// </summary>
    /// <typeparam name="TService">The service interface type.</typeparam>
    /// <param name="callback">The callback to invoke.</param>
    /// <returns>A disposer that removes a pending callback.</returns>
    IDisposable WhenAvailable<TService>(Action<TService> callback) where TService : class;
}
