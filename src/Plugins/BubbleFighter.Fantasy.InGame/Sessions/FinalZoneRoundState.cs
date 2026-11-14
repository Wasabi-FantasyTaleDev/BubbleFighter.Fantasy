using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Match-bounded elimination publication over the shared participant life records.</summary>
internal sealed class FinalZoneRoundState(int generation, IReadOnlyList<RoomSeat> participants, bool teamMatch = false)
{
    private readonly RoomSeat[] _participants = [.. participants];
    private readonly HashSet<int> _eliminated = [];

    internal int Generation { get; } = generation;
    internal bool Finished { get; private set; }
    /// <summary>Shares the bounded connection cleanup after a publication failure in this round.</summary>
    internal Task? CleanupTask { get; set; }
    internal IReadOnlySet<int> WinnerSlots { get; private set; } = new HashSet<int>();
    internal FinalZoneItemLedger? Items { get; set; }
    internal IReadOnlyList<FinalZoneMapItem>? OpeningItems { get; set; }
    internal FinalZoneWaterRules? WaterRules { get; set; }
    internal int WaterPhase { get; set; }
    internal FinalZoneSupplyCatalog? Supplies { get; set; }
    internal FinalZoneDroneRules? DroneRules { get; set; }
    internal HashSet<int> GoldenSupplyIndexes { get; } = [];

    // A bubble is not an elimination. Only shared terminal life state or departure removes a survivor.
    internal RoundFinalZonePlayerEliminated[] Synchronize(IReadOnlyList<RoomSeat> present)
    {
        if (Finished) return [];
        var remaining = _participants.Where(seat => !_eliminated.Contains(seat.Slot)
            && present.Contains(seat) && !seat.Game.Connection.IsClosed && seat.Life.State != SeatLife.Dead).ToArray();
        var changes = _participants.Where(seat => !_eliminated.Contains(seat.Slot) && !remaining.Contains(seat)).ToArray();
        var notices = new RoundFinalZonePlayerEliminated[changes.Length];
        var count = _participants.Length - _eliminated.Count;
        for (var index = 0; index < changes.Length; index++)
        {
            var seat = changes[index];
            _eliminated.Add(seat.Slot);
            notices[index] = new(RoomRegistry.SceneEntity(seat.Slot), --count);
        }
        if (remaining.Length == 0)
            Finished = true;
        else if (teamMatch)
        {
            // kr-1256 sub_9E1710 selects the team strategy for mode 25; kr-1256 sub_C6B6C0 uses its team result.
            var winningTeam = remaining[0].Team;
            if (remaining.All(seat => seat.Team == winningTeam) && _participants.Any(seat => seat.Team != winningTeam))
            {
                Finished = true;
                WinnerSlots = _participants.Where(seat => seat.Team == winningTeam).Select(seat => seat.Slot).ToHashSet();
            }
        }
        else if (remaining.Length == 1 && _participants.Length > 1)
        {
            Finished = true;
            WinnerSlots = new HashSet<int> { remaining[0].Slot };
        }
        return notices;
    }
}
