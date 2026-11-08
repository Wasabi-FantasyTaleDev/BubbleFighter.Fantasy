using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundWeaponCreated>? _weaponCreated;
    private readonly IMessageSender<RoundWeaponPicked>? _weaponPicked;

    internal async ValueTask HandleWeaponDropAsync(ISession session, RoundWeaponDropRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            Sessions.RoomSeat[] members;
            RoundWeaponCreated created;
            lock (room.Gate)
            {
                if (room.Phase != Sessions.RoomPhase.Playing || room.FindByGame(session.Id) is null) return;
                if (request.WeaponSpec < 0 || !float.IsFinite(request.X) || !float.IsFinite(request.Y) || !float.IsFinite(request.Z)
                    || !float.IsFinite(request.VelocityX) || !float.IsFinite(request.VelocityY) || !float.IsFinite(request.VelocityZ))
                    throw new InvalidOperationException("Weapon drop position, velocity or specification is invalid.");
                room.Weapons ??= new RoundWeaponState(room.StartGeneration, _options.WeaponObjectIdBase);
                var state = room.Weapons;
                if (state.Generation != room.StartGeneration) return;
                if (state.Created >= _options.MaxWeaponObjectsPerRound)
                {
                    if (!state.RefusalLogged)
                    {
                        state.RefusalLogged = true;
                        _logger.LogWarning("Room {Room}: dropped weapons reached the per-round limit {Limit}; further drops are refused.",
                            room.Id, _options.MaxWeaponObjectsPerRound);
                    }
                    return;
                }
                var id = checked(state.ObjectIdBase + ++state.Created);
                state.Objects.Add(id);
                created = new(id, request);
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _weaponCreated!.SendAsync(member.Game, created, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleWeaponPickupAsync(ISession session, RoundWeaponPickupRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            Sessions.RoomSeat[] members;
            lock (room.Gate)
            {
                if (room.Phase != Sessions.RoomPhase.Playing || room.FindByGame(session.Id) is not { } seat
                    || request.ActorSeat != seat.Slot || room.Weapons is not { } state
                    || state.Generation != room.StartGeneration || !state.Objects.Contains(request.ObjectId)) return;
                // A pickup needs the same live revision owner that successfully installed this seat's opening tables.
                if (_seedInputs?.RequiresFixedEquipment(room.ModeId) == true
                    && (_criticalAuthority is null || seat.Critical is not { } critical || !CurrentCritical(room, seat, critical)))
                {
                    if (!state.FixedSeedRefusalLogged)
                    {
                        state.FixedSeedRefusalLogged = true;
                        _logger.LogWarning("Room {Room} mode {Mode}: weapon pickup requires a healthy critical revision owner and successful opening tables.",
                            room.Id, room.ModeId);
                    }
                    return;
                }
                state.Objects.Remove(request.ObjectId);
                members = [.. room.Seats];
            }
            var picked = new RoundWeaponPicked(request.ObjectId, request.Actor);
            foreach (var member in members)
                await _weaponPicked!.SendAsync(member.Game, picked, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}

internal sealed class RoundWeaponState(int generation, int objectIdBase)
{
    internal int Generation { get; } = generation;
    internal int ObjectIdBase { get; } = objectIdBase;
    internal int Created { get; set; }
    internal bool RefusalLogged { get; set; }
    internal bool FixedSeedRefusalLogged { get; set; }
    internal HashSet<int> Objects { get; } = [];
}
