using System.Diagnostics.CodeAnalysis;

namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>
/// A session that supports bounded, type-keyed state associations. Optional during the
/// migration from <see cref="ISession.Items"/> so existing session implementations remain usable.
/// </summary>
public interface ISessionWithFeatures : ISession
{
    /// <summary>Gets this connection's feature associations, closed when its protocol run ends.</summary>
    ISessionFeatures Features { get; }
}

/// <summary>
/// Thread-safe associations keyed by the exact declared reference type. Shared features use
/// interfaces from shared contracts; private plugin state may use a private class. Values are
/// borrowed references, not owned resources, and are never implicitly disposed or replaced.
/// Registration, reads, handle removal and protocol closure may race: each operation observes
/// the collection at one synchronized point. A returned reference does not lock its value.
/// </summary>
public interface ISessionFeatures
{
    /// <summary>Gets the maximum number of simultaneous type associations in this session.</summary>
    int Capacity { get; }

    /// <summary>Gets the current number of associations; closed collections always report zero.</summary>
    int Count { get; }

    /// <summary>Gets whether protocol termination has permanently closed registration admission.</summary>
    bool IsClosed { get; }

    /// <summary>
    /// Registers a non-null reference under exactly <typeparamref name="T"/> and returns an
    /// idempotent removal handle. Duplicate types and exhausted capacity throw without changing
    /// the collection. Replacement requires disposing the old handle before registering again;
    /// disposing an old handle cannot remove a later registration. Closed collections reject registration.
    /// </summary>
    /// <remarks>
    /// The caller owns the value's lifetime and synchronization. Transfer the handle to a bounded
    /// owner scope registered with IPluginContext.Effect, and remove ended sessions from that scope.
    /// Do not append one effect per connection to a long-lived plugin's effect list.
    /// </remarks>
    IDisposable Register<T>(T feature) where T : class;

    /// <summary>
    /// Borrows the reference registered under exactly <typeparamref name="T"/>. Missing types
    /// throw InvalidOperationException; a closed collection throws ObjectDisposedException.
    /// Previously borrowed references remain ordinary references after removal, with no invocation drain.
    /// </summary>
    T Get<T>() where T : class;

    /// <summary>
    /// Borrows an exact-type association if present. Returns false after closure or when absent.
    /// Removal does not invalidate a reference already returned and does not synchronize its contents.
    /// </summary>
    bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class;
}

/// <summary>Typed state access for sessions while legacy implementations migrate from Items.</summary>
public static class SessionFeatureExtensions
{
    /// <summary>
    /// Borrows the required exact-type feature. Throws NotSupportedException if the session does
    /// not implement ISessionWithFeatures; missing and closed collections retain Get's errors.
    /// </summary>
    public static T Feature<T>(this ISession session) where T : class => GetFeatures(session).Get<T>();

    /// <summary>Returns false if features are unsupported, the type is missing, or the collection is closed.</summary>
    public static bool TryFeature<T>(this ISession session, [NotNullWhen(true)] out T? feature) where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        feature = null;
        return session is ISessionWithFeatures supported && supported.Features.TryGet(out feature);
    }

    /// <summary>
    /// Registers an exact-type association and returns its removal handle. Throws NotSupportedException
    /// for legacy sessions. The handle must enter a bounded owner scope registered as a plugin effect;
    /// the value remains caller-owned. No state is stored in Items as a fallback.
    /// </summary>
    public static IDisposable RegisterFeature<T>(this ISession session, T feature) where T : class =>
        GetFeatures(session).Register(feature);

    private static ISessionFeatures GetFeatures(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session is ISessionWithFeatures supported
            ? supported.Features
            : throw new NotSupportedException($"Session '{session.Id}' does not support typed features.");
    }
}
