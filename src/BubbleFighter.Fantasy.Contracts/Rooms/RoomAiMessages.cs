using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>The room master selects a native computer-player preset for a room seat.</summary>
/// <param name="Team">Explicit team, or null to preserve an existing bot's team or balance a new seat.</param>
/// <param name="Slot">The selected room seat.</param>
/// <param name="Difficulty">The native difficulty identifier.</param>
/// <param name="Preset">The native preset identifier within that difficulty.</param>
public sealed record RoomAiAddRequested(int? Team, int Slot, int Difficulty, int Preset) : IMessage
{
    /// <summary>Gets whether the native UI selected a preset, requested a random bot, or requested the host's appearance.</summary>
    public RoomAiSelection Selection { get; init; } = RoomAiSelection.Preset;
}

/// <summary>Identifies the room master's native computer-player selection operation independently of its wire encoding.</summary>
public enum RoomAiSelection
{
    /// <summary>Uses the explicit slot, difficulty and preset selectors.</summary>
    Preset,
    /// <summary>Chooses a configured preset and an available seat for the requested team.</summary>
    Random,
    /// <summary>Uses the master's current appearance in an available computer-player seat.</summary>
    Clone,
}

/// <summary>The room master removes a computer player without disconnecting its controlling human.</summary>
/// <param name="Slot">The computer player's room seat.</param>
public sealed record RoomAiRemoveRequested(int Slot) : IMessage;

/// <summary>Identifies the real client controlling a computer-player seat and its native preset selection.</summary>
/// <param name="ControllerSlot">The controlling human's room seat.</param>
/// <param name="ControllerUserNum">The controlling human's account number.</param>
/// <param name="Difficulty">The selected native difficulty.</param>
/// <param name="Preset">The selected native preset within the difficulty.</param>
public sealed record RoomAiIdentity(int ControllerSlot, int ControllerUserNum, int Difficulty, int Preset)
{
    /// <summary>Gets the preset name resource resolved in each recipient's Launcher language; empty preserves the configured nickname.</summary>
    public string NicknameKey { get; init; } = "";
}
