namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Opts an acquiring factory into replacing itself with the resolved revision factory returned by its execution.</summary>
/// <remarks>Every acquired execution must implement <see cref="IPluginExecutionBinding"/>.
/// Factories that do not opt in retain their own acquisition policy even when they return a decorated execution with binding metadata.</remarks>
public interface IPluginExecutionBindingFactory : IPluginExecutionFactory
{
}

/// <summary>Optionally binds an acquired execution to the immutable factory for its resolved code revision.</summary>
/// <remarks>The host consumes this binding only when the acquiring factory implements <see cref="IPluginExecutionBindingFactory"/>.
/// It validates the bound factory before publication and retains it for later activations.
/// Returning a new factory does not transfer cleanup ownership from the acquired execution.</remarks>
public interface IPluginExecutionBinding
{
    /// <summary>Gets the non-null factory that recreates this execution's resolved revision without refreshing it.
    /// Its coordination key must match the acquiring definition, and its code provenance must remain immutable.</summary>
    IPluginExecutionFactory BoundFactory { get; }
}
