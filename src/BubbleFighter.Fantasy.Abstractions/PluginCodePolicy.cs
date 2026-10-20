namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Controls whether an entry may refresh executable code inside the running process.</summary>
public enum PluginCodeRefreshPolicy
{
    /// <summary>Permits an explicit code refresh through the configured execution backend.</summary>
    Allow = 0,

    /// <summary>Requires a process restart instead of refreshing this entry's executable code in place.</summary>
    RestartRequired = 1,
}

/// <summary>Describes the known lifetime capability of an entry's resolved executable code.</summary>
public enum PluginCodeLifetime
{
    /// <summary>The backend has not supplied a known code lifetime capability.</summary>
    Unknown = 0,

    /// <summary>The resolved code remains loaded for the process lifetime and requires a restart for reclamation.</summary>
    ProcessLifetime = 1,

    /// <summary>The resolved code supports collection after every holder releases it; collection is not guaranteed or already complete.</summary>
    Collectible = 2,
}
