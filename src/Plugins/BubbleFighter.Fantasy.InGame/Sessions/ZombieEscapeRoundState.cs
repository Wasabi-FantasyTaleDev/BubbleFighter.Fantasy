using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Bounds script conversion authority to the participants and incarnations of one round.</summary>
internal sealed class ZombieEscapeRoundState(int generation, ISession master, IReadOnlyList<RoomSeat> seats)
{
    internal int Generation { get; } = generation;
    internal ISession Master { get; } = master;
    internal int InitialZombieLimit { get; private set; } = (seats.Count + 4) / 5;
    internal int InitialZombies { get; private set; }
    private readonly Dictionary<int, Entry> _entries = seats.ToDictionary(seat => seat.Slot,
        seat => new Entry(seat.Game, seat.Life.Generation, seat.Team));

    internal void RestoreWaitingTeams(IEnumerable<RoomSeat> members)
    {
        foreach (var member in members)
            if (_entries.TryGetValue(member.Slot, out var entry) && ReferenceEquals(member.Game, entry.Game))
                member.Team = entry.WaitingTeam;
    }

    internal bool Select(RoomSeat requester, RoomSeat target, ScriptZombieConversion conversion, int activePlayers)
    {
        if (!_entries.TryGetValue(requester.Slot, out var producer) || !ReferenceEquals(producer.Game, requester.Game)
            || !_entries.TryGetValue(target.Slot, out var entry) || !ReferenceEquals(entry.Game, target.Game)
            || conversion.Seat != target.Slot || entry.Pending is not null || conversion.WeaponItemId != 110289
            || conversion.ZombieKind is not (202 or 204 or 205 or 207)) return false;
        if (InitialZombies == 0) InitialZombieLimit = (activePlayers + 4) / 5;
        if (ReferenceEquals(requester.Game, Master) && entry.Kind is null && target.Team == 1
            && InitialZombies < InitialZombieLimit)
        {
            InitialZombies++;
        }
        else
        {
            // Local infection remains valid after a departure interrupts the master's initial selection burst.
            if (InitialZombies == 0 || !ReferenceEquals(requester.Game, target.Game)) return false;
            if (entry.Kind is not null && (conversion.ZombieKind != entry.Kind
                || target.Life.Generation <= entry.LastGeneration && target.Life.State != SeatLife.Dead)) return false;
        }
        entry.Kind = conversion.ZombieKind;
        entry.Pending = conversion;
        return true;
    }

    internal bool HasPendingConversions(IEnumerable<RoomSeat> members) => members.Any(member =>
        !member.Game.Connection.IsClosed && _entries.TryGetValue(member.Slot, out var entry) && entry.Pending is not null);

    internal bool Confirm(RoomSeat owner, RoundScriptZombieConversionReported report)
    {
        if (!_entries.TryGetValue(owner.Slot, out var entry) || !ReferenceEquals(entry.Game, owner.Game)
            || report.Seat != owner.Slot || report.Team != 34 || owner.Team != 34 || report.WeaponItemId != 110289
            || report.ZombieKind != entry.Kind || entry.Kind is null) return false;
        if (entry.Pending is not null)
        {
            if (report.Operation != 0 && !(report.Operation == 1 && owner.Life.Generation > entry.LastGeneration)) return false;
            entry.Pending = null;
        }
        else
        {
            // kr-1256 sub_AC41C0 restores the existing zombie character after its native respawn.
            if (report.Operation != 1 || owner.Life.Generation <= entry.LastGeneration) return false;
        }
        owner.Life.EndDeathByConversion();
        entry.LastGeneration = owner.Life.Generation;
        return true;
    }

    private sealed class Entry(ISession game, long generation, byte waitingTeam)
    {
        internal ISession Game { get; } = game;
        internal byte WaitingTeam { get; } = waitingTeam;
        internal long LastGeneration { get; set; } = generation;
        internal int? Kind { get; set; }
        internal ScriptZombieConversion? Pending { get; set; }
    }
}
