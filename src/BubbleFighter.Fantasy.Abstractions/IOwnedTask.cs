namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>A supervised operation retained by its activation until work and cancellation cleanup settle.</summary>
public interface IOwnedTask : IAsyncDisposable
{
    /// <summary>Gets the operation name used in lifecycle failure diagnostics.</summary>
    string Name { get; }

    /// <summary>Gets completion after work and cancellation cleanup settle. Cancellation exceptions
    /// carrying the supplied, canceled token produce a canceled task; other errors fault it.
    /// Success returns the effect slot; failures remain recorded until owner teardown.</summary>
    Task Completion { get; }
}
