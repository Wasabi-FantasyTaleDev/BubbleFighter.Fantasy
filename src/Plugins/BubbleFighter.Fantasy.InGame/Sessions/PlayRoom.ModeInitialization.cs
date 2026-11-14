using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    internal ExtremeRacingInitializationState? RacingInitialization { get; set; }
    /// <summary>Tracks racing connection cleanup through state reset and room shutdown.</summary>
    internal Task ExtremeRacingCleanupTask { get; set; } = Task.CompletedTask;
    /// <summary>Gets or sets the worker that issues the generation's cannon commands; separate from the race's own deadline.</summary>
    internal RoomDeadlineClock? ExtremeRacingCannonClock { get; set; }
    internal BnBRuntimeState? BnBRound { get; set; }
    internal RoomDeadlineClock? BnBClock { get; set; }
    /// <summary>Tracks the sole BnB connection cleanup through state reset and room shutdown.</summary>
    internal Task BnBCleanupTask { get; set; } = Task.CompletedTask;
}

internal sealed class BnBRuntimeState(int generation, BnBRoundState ledger, RoomSeat[] members)
{
    internal int Generation { get; } = generation;
    internal BnBRoundState Ledger { get; set; } = ledger;
    internal RoomSeat[] Members { get; } = members;
    /// <summary>Shares this generation's cleanup so nested close events cannot start it twice.</summary>
    internal Task? CleanupTask { get; set; }
}

internal sealed class ExtremeRacingInitializationState(int generation, ExtremeRacingMapData map)
{
    internal int Generation { get; } = generation;
    internal ExtremeRacingMapData Map { get; } = map;
    internal HashSet<ISession> Recipients { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets this generation's outstanding cannon commands, or null when the map carries no cannon data.</summary>
    internal ExtremeRacingCannonLedger? CannonLedger { get; } = map.Cannons is null
        ? null : new(map.Cannons, map.Initialization.FirstCannonIdentity);

    /// <summary>Gets this generation's proposed firing cadence, or null when the map carries no cannon data.</summary>
    internal ExtremeRacingCannonCadence? CannonCadence { get; } = map.Cannons is null ? null : new(map.Cannons);

    /// <summary>
    /// Gets or sets the one seat that receives this generation's cannon commands. It is a server policy rather than
    /// a native role (the client executes a command on any participant with a local actor, kr-1256 sub_67B6E0): the
    /// lowest-slot initialized seat with an open game connection. A departure picks another seat for later commands only.
    /// </summary>
    internal RoomSeat? CannonOrigin { get; set; }

    /// <summary>Gets or sets whether a failed publication ended this generation's cannon schedule for good.</summary>
    internal bool CannonScheduleFailed { get; set; }
}
