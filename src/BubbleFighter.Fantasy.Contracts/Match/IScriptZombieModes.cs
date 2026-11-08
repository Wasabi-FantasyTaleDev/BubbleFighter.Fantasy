using BubbleFighter.Fantasy.Contracts.Rooms;
namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Declares which native zombie rules share a dialect's single conversion exchange.</summary>
public interface IScriptZombieModes
{
    /// <summary>Gets the game protocol whose conversion bindings publish this mode selection.</summary>
    string Protocol { get; }

    /// <summary>Gets the audited modes explicitly enabled on the room dialect row.</summary>
    IReadOnlySet<GameModeId> Modes { get; }
}
