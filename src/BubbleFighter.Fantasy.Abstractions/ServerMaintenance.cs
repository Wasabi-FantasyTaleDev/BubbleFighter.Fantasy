namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Controls maintenance admission for services that accept new player sessions.</summary>
public interface IServerMaintenance
{
    /// <summary>Gets whether new player admission is paused.</summary>
    bool IsMaintenance { get; }

    /// <summary>Sets whether new player admission is paused.</summary>
    /// <param name="enabled">True to reject new admissions until maintenance ends.</param>
    void SetMaintenance(bool enabled);
}
