namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Publishes a prepared resource result at the effect ownership release boundary.</summary>
internal interface IResourceReleaseObserver
{
    /// <summary>Completes infrastructure signals while the scope gate is held; implementations must not throw, block, or invoke user callbacks.</summary>
    void OnReleaseSettled();
}
