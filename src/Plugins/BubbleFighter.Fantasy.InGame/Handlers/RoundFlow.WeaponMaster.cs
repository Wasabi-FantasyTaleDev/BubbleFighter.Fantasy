using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

// The WeaponMaster round (mode 26) is the shared round plus weapon levels earned with experience. It never withholds a
// revival; the first seat to reach the last level wins and ends the round, and when the clock runs out first the seats
// at the highest level win. Everything here reacts to events the common scoring and life paths already accepted, plus
// the client's own request for a new weapon.
internal sealed partial class RoundFlow
{
    private WeaponMasterRules? _weaponMasterRules;
    private IMessageSender<RoundEquipmentApplied>? _equipmentApplied;
    private Func<Random> _weaponMasterRandom = static () => new Random();

    /// <summary>Turns the mode's weapon logic on; the dialect's equipment sender is its only outbound wire.</summary>
    internal void ConfigureWeaponMaster(WeaponMasterRules rules, IMessageSender<RoundEquipmentApplied> sender, Func<Random>? random = null)
    {
        _weaponMasterRules = rules;
        _equipmentApplied = sender;
        if (random is not null) _weaponMasterRandom = random;
    }

    private bool IsWeaponMasterMode(PlayRoom room) => _weaponMasterRules is not null && room.ModeId == (ushort)GameModeId.WeaponMaster;

    // Called under the room gate when the round starts: every seat gets its first weapon.
    private void StartWeaponMaster(PlayRoom room, int generation)
    {
        if (!IsWeaponMasterMode(room)) return;
        var state = new WeaponMasterRoundState(_weaponMasterRules!, _weaponMasterRandom(), generation);
        state.Begin(room.Seats.Select(seat => seat.Slot));
        room.WeaponMaster = state;
    }

    // Called under the room gate by the paths that accept a score. Seats only: an NPC or computer player has no
    // weapon assignment, and its events change nothing.
    private void NoteWeaponMasterScore(PlayRoom room, int slot, WeaponMasterEvent kind)
    {
        if (room.WeaponMaster is { } state && room.Seats.Any(seat => seat.Slot == slot))
            state.Score(slot, kind);
    }

    // Called under the room gate whenever a seat's character is known dead. The life incarnation makes it count once
    // however many native reports name the same death.
    private void NoteWeaponMasterDeath(PlayRoom room, int slot)
    {
        if (room.WeaponMaster is { } state && room.Seats.FirstOrDefault(seat => seat.Slot == slot) is { Life.State: SeatLife.Dead } seat)
            state.Death(slot, seat.Life.Generation);
    }

    // Called under the room gate after a score: the first seat at the last level is the round's sole winner. Returns
    // whether this call decided it, so the caller ends the round once.
    private static bool DecideWeaponMasterWinner(PlayRoom room, WeaponMasterRoundState state)
    {
        if (state.Winner is not { } winner || room.DecidedWinnerTeam is not null || room.DecidedWinnerSlots is not null)
            return false;
        room.DecidedWinnerSlots = new HashSet<int> { winner };
        return true;
    }

    // Called under the room gate when a WeaponMaster round ends on its clock: the seats at the highest level win; when two or more
    // seats are all at the same level it is a draw.
    private static void DecideWeaponMasterRound(PlayRoom room)
    {
        if (room.WeaponMaster is not { } state || room.DecidedWinnerTeam is not null || room.DecidedWinnerSlots is not null)
            return;
        var levels = room.Seats.Select(seat => (seat.Slot, Level: state.Level(seat.Slot))).ToArray();
        var highest = levels.Length == 0 ? 0 : levels.Max(seat => seat.Level);
        room.DecidedWinnerSlots = levels.Length > 1 && levels.All(seat => seat.Level == highest)
            ? new HashSet<int>()
            : levels.Where(seat => seat.Level == highest).Select(seat => seat.Slot).ToHashSet();
    }

    /// <summary>
    /// Answers a player's revival request (kr-1256 C2S 765): when the seat has died the client tunable's number of
    /// times since its last weapon, it is handed a new weapon of its level. An early request is logged and left
    /// unanswered; the client asks again on its next death.
    /// </summary>
    internal async ValueTask HandleWeaponMasterRevivalAsync(ISession session, RoundWeaponMasterRevivalRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping || room.WeaponMaster is not { } state
                    || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session))
                    return;
                if (!state.Revive(seat.Slot))
                {
                    _logger.LogWarning("Room {Room} seat {Slot}: WeaponMaster revival requested after {Deaths} death(s); {Required} are required.",
                        room.Id, seat.Slot, state.DeathsSinceWeapon(seat.Slot), _weaponMasterRules!.NoScoreDeathCount);
                    return;
                }
            }
            await PublishWeaponMasterAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Sends the weapons the round decided on to every member, so each client applies them to that seat's character
    // (kr-1256 sub_6DD900 indexes the character table by seat).
    private async ValueTask PublishWeaponMasterAsync(PlayRoom room, CancellationToken ct)
    {
        if (_equipmentApplied is not { } sender) return;
        WeaponMasterAssignment[] pending;
        RoomSeat[] members;
        lock (room.Gate)
        {
            if (room.WeaponMaster is not { } state || room.Phase != RoomPhase.Playing) return;
            pending = state.TakePending();
            if (pending.Length == 0) return;
            members = [.. room.Seats];
        }
        foreach (var assignment in pending)
        {
            var message = new RoundEquipmentApplied(assignment.Slot, [WeaponMasterRules.WeaponSlot], [assignment.Item], (int)assignment.Reason);
            foreach (var member in members)
            {
                ct.ThrowIfCancellationRequested();
                await sender.SendAsync(member.Game, message, ct).ConfigureAwait(false);
            }
            _logger.LogInformation("Room {Room}: weapon {Item} for seat {Slot} at level {Level} ({Reason}).",
                room.Id, assignment.Item, assignment.Slot, assignment.Level, assignment.Reason);
        }
    }
}
