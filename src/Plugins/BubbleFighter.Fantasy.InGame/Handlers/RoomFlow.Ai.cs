using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    public async ValueTask HandleAiAddAsync(ISession session, RoomAiAddRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async token =>
        {
            RoomSeat[] members;
            RoomSeatUpdated update;
            lock (room.Gate)
            {
                var host = room.FindByGame(session.Id);
                if (host is null || host.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting)
                {
                    _logger.LogWarning("Session {Id} attempted to change AI without waiting-room ownership.", session.Id);
                    return;
                }
                var automatic = request.Selection is RoomAiSelection.Random or RoomAiSelection.Clone;
                if (_options?.AiModeIds.Contains(room.ModeId) != true || request.Team is < 0 or > 2
                    || !Enum.IsDefined(request.Selection)
                    || (!automatic && (request.Slot < 0 || request.Slot >= room.MaxSeats))
                    || (automatic && (request.Slot != -1 || request.Difficulty != -1 || request.Preset != -1)))
                {
                    _logger.LogWarning("Room {Room} rejected AI selection in mode {Mode}, slot {Slot}, team {Team}.",
                        room.Id, room.ModeId, request.Slot, request.Team);
                    return;
                }
                var slot = automatic ? Enumerable.Range(0, room.MaxSeats).FirstOrDefault(index =>
                    !room.ClosedSlots.Contains(index) && room.Seats.All(seat => seat.Slot != index)
                        && room.BotAt(index) is null, -1) : request.Slot;
                if (slot < 0)
                    return;
                var preset = automatic ? _options.AiPresets[Random.Shared.Next(_options.AiPresets.Length)]
                    : _options.AiPresets.SingleOrDefault(candidate => candidate.Difficulty == request.Difficulty
                        && candidate.Preset == request.Preset);
                if (preset is null)
                {
                    _logger.LogWarning("Room {Room} has no configured AI loadout for difficulty {Difficulty}, preset {Preset}.",
                        room.Id, request.Difficulty, request.Preset);
                    return;
                }
                var team = request.Team ?? room.BotAt(slot)?.Appearance.Team ?? 0;
                // cn-774 sub_B68770 takes the menu team from the target seat, including zero for an empty seat.
                // Team rooms retain a replacement's team; only a new unassigned seat needs balancing.
                if (team == 0 && room.RequiresSpawnTeams)
                    team = room.BotAt(slot)?.Appearance.Team ?? 0;
                if (team == 0 && (request.Team is null || room.RequiresSpawnTeams))
                {
                    var occupied = room.Seats.Select(View).Concat(room.Bots.Where(bot => bot.Appearance.Slot != slot)
                        .Select(bot => bot.Appearance)).ToArray();
                    var first = occupied.Count(seat => seat.Team == 1);
                    var second = occupied.Count(seat => seat.Team == 2);
                    team = first == second ? Random.Shared.Next(1, 3) : first < second ? 1 : 2;
                }
                var appearance = new RoomMemberView(slot, host.UserNum, preset.Nickname, preset.Level, team, true,
                    preset.CharacterItemId, [.. preset.EquippedItemIds.Where(id => id != preset.CharacterItemId), preset.CharacterItemId])
                {
                    ColorIndex = preset.ColorIndex,
                    ItemAttributes = preset.ItemAttributes.Select(attribute => new RoomItemAttribute(attribute.ItemId,
                        attribute.Kind, attribute.Values.ToArray(), attribute.Unknown28, attribute.Unknown32)).ToArray(),
                    Ai = new(host.Slot, host.UserNum, preset.Difficulty, preset.Preset) { NicknameKey = preset.NicknameKey },
                };
                if (request.Selection == RoomAiSelection.Clone)
                {
                    var owner = View(host);
                    appearance = appearance with
                    {
                        Nickname = owner.Nickname, Level = owner.Level, CharacterItemId = owner.CharacterItemId,
                        EquippedItemIds = owner.EquippedItemIds.ToArray(), ColorIndex = owner.ColorIndex,
                        ItemAttributes = [], Ai = appearance.Ai with { NicknameKey = "" },
                    };
                }
                if (!room.PutBot(appearance))
                {
                    _logger.LogWarning("Room {Room} rejected AI seat {Slot}: it is occupied, closed or outside capacity.", room.Id, request.Slot);
                    return;
                }
                members = [.. room.Seats];
                update = new(room.Id, appearance) { Room = View(room) };
            }
            foreach (var member in members)
                await NotifyScenePeerAsync(member.Game, _seatUpdated, update, token).ConfigureAwait(false);
            await ArmWhenFullAsync(room, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask HandleAiRemoveAsync(ISession session, RoomAiRemoveRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async token =>
        {
            RoomSeat[] members;
            RoomMemberView? removed;
            lock (room.Gate)
            {
                var host = room.FindByGame(session.Id);
                if (host is null || host.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting)
                    return;
                removed = room.RemoveBot(request.Slot);
                if (removed is null)
                    return;
                members = [.. room.Seats];
            }
            await NotifyRemovedBotsAsync(room.Id, [removed], members, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask NotifyRemovedBotsAsync(int roomId, IReadOnlyList<RoomMemberView> bots,
        IReadOnlyList<RoomSeat> members, CancellationToken ct)
    {
        foreach (var bot in bots)
            foreach (var member in members)
                await NotifyScenePeerAsync(member.Game, _seatVacated, new(roomId, bot), ct).ConfigureAwait(false);
    }
}
