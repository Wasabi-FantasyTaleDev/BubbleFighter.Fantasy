using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Admits native script selection and owned respawn conversions for one round's participants.</summary>
internal sealed class ScriptZombieRoundState(int generation, ISession master, IReadOnlyList<RoomSeat> seats, int mode = 8)
{
    // The native scripts use human 1 and zombie 34. Ordinary mode selects one small zombie per four
    // players with weapon 110288; ghost mode selects one king with 110290. Weighted selection stays native.
    internal const byte HumanTeam = 1;
    internal const byte ZombieTeam = 34;
    internal int WeaponItemId { get; } = mode == 28 ? 110290 : 110288;
    internal int ModeId { get; } = mode;
    internal int Generation { get; } = generation;
    internal ISession Master { get; } = master;
    internal int InitialZombieLimit { get; private set; } = mode == 28 ? 1 : (seats.Count + 3) / 4;
    internal int InitialZombies { get; private set; }
    internal int? KingSlot { get; private set; }
    private readonly Dictionary<int, Entry> _entries = seats.ToDictionary(seat => seat.Slot,
        seat => new Entry(seat.Game, seat.Life.Generation, seat.Team));

    internal void RestoreWaitingTeams(IEnumerable<RoomSeat> members)
    {
        foreach (var member in members)
            if (_entries.TryGetValue(member.Slot, out var entry) && ReferenceEquals(member.Game, entry.Game))
                member.Team = entry.WaitingTeam;
    }

    internal bool Select(RoomSeat requester, RoomSeat target, ScriptZombieConversion request, int activePlayers)
    {
        if (!_entries.TryGetValue(requester.Slot, out var producer) || !ReferenceEquals(producer.Game, requester.Game)
            || !_entries.TryGetValue(target.Slot, out var entry) || !ReferenceEquals(entry.Game, target.Game)
            || request.Seat != target.Slot || entry.Pending is not null || request.WeaponItemId != WeaponItemId) return false;
        // Ghost Lua sends the same initial king twice, with weapons 110290 then 0. The zero form
        // preserves the client's existing weapon (kr-1256 sub_AD2060 / cn-774 sub_CC51C0), not a new grant.
        if (InitialZombies == 0 && ModeId == 8) InitialZombieLimit = (activePlayers + 3) / 4;
        if (ReferenceEquals(requester.Game, Master) && entry.Kind is null && target.Team == HumanTeam
            && InitialZombies < InitialZombieLimit)
        {
            if (ModeId == 28 ? request.ZombieKind is not (201 or 203) : !IsSmallZombie(request.ZombieKind)) return false;
            InitialZombies++;
            if (ModeId == 28) KingSlot = target.Slot;
        }
        else
        {
            // Both scripts infect owned humans on respawn. Ghost Lua explicitly excludes its king;
            // no living human or foreign actor receives a later selection through this operation.
            if (InitialZombies == 0 || !ReferenceEquals(requester.Game, target.Game)
                || KingSlot == target.Slot || !IsSmallZombie(request.ZombieKind)
                || target.Life.Generation <= entry.LastGeneration && target.Life.State is not (SeatLife.Dead or SeatLife.ResetPending)
                || entry.Kind is { } kind && request.ZombieKind != kind) return false;
        }
        entry.Kind = request.ZombieKind;
        entry.Pending = request;
        return true;
    }

    internal bool HasPendingConversions(IEnumerable<RoomSeat> members) => members.Any(member =>
        !member.Game.Connection.IsClosed && _entries.TryGetValue(member.Slot, out var entry) && entry.Pending is not null);

    internal bool KingDeathIsFinal(IEnumerable<RoomSeat> members) => KingSlot is { } king
        && _entries.TryGetValue(king, out var entry) && entry.Confirmed && members.Any(member => member.Slot == king
            && ReferenceEquals(member.Game, entry.Game) && member.Life.DeathCleaned);

    internal bool Confirm(RoomSeat owner, RoundScriptZombieConversionReported report)
    {
        if (!_entries.TryGetValue(owner.Slot, out var entry) || !ReferenceEquals(entry.Game, owner.Game)
            || report.Seat != owner.Slot || report.Team != ZombieTeam || owner.Team != ZombieTeam
            || entry.Kind is null) return false;
        if (report.Operation == 2)
        {
            // The native gauge owns charging. kr-1256 sub_B037D0, cn-774 sub_CA6400 and tw-454 sub_992CD0 send the
            // no-kind/zero-weapon form; this changes the current character without ending its life.
            if (!entry.Confirmed || entry.Pending is not null || entry.FeverActive || owner.Life.State != SeatLife.Alive
                || owner.Life.Generation != entry.LastGeneration
                || report.ZombieKind is not (null or -1) || report.WeaponItemId != 0) return false;
            entry.FeverActive = true;
            return true;
        }
        if (report.WeaponItemId != WeaponItemId || report.ZombieKind != entry.Kind) return false;
        if (report.Operation == 1 && entry.FeverActive && entry.Pending is null
            && owner.Life.State == SeatLife.Alive && owner.Life.Generation == entry.LastGeneration)
        {
            // kr-1256 sub_AC88D0, cn-774 sub_CAE720 and tw-454 sub_99A420 restore the base form when its timer
            // expires. Keep shared HP and life generation, just as the native consumer retains its HP ratio.
            entry.FeverActive = false;
            return true;
        }
        if (entry.Pending is not null)
        {
            if (report.Operation != 0 && !(report.Operation == 1 && owner.Life.Generation > entry.LastGeneration)) return false;
            entry.Pending = null;
        }
        else if (report.Operation != 1 || owner.Life.Generation <= entry.LastGeneration || KingSlot == owner.Slot)
        {
            // kr-1256 sub_AC41C0 restores a previously authorized zombie after native respawn.
            return false;
        }
        owner.Life.EndDeathByConversion();
        entry.LastGeneration = owner.Life.Generation;
        entry.Confirmed = true;
        entry.FeverActive = false;
        return true;
    }

    private static bool IsSmallZombie(int kind) => kind is 202 or 204 or 205 or 207;

    private sealed class Entry(ISession game, long generation, byte waitingTeam)
    {
        internal ISession Game { get; } = game;
        internal byte WaitingTeam { get; } = waitingTeam;
        internal long LastGeneration { get; set; } = generation;
        internal int? Kind { get; set; }
        internal bool Confirmed { get; set; }
        internal bool FeverActive { get; set; }
        internal ScriptZombieConversion? Pending { get; set; }
    }
}
