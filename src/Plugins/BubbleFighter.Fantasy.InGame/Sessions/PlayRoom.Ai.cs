using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    private readonly Dictionary<int, RoomBot> _bots = [];

    internal IReadOnlyCollection<RoomBot> Bots => _bots.Values;
    internal int OccupantCount => Seats.Count + _bots.Count;

    internal RoomBot? BotAt(int slot) => _bots.GetValueOrDefault(slot);

    internal bool PutBot(RoomMemberView appearance)
    {
        if (Phase != RoomPhase.Waiting || appearance.Ai is null)
            return false;
        if (_bots.TryGetValue(appearance.Slot, out var existing))
        {
            existing.Appearance = appearance;
            return true;
        }
        if (!_members.Reserve(appearance.Slot))
            return false;
        _bots.Add(appearance.Slot, new(appearance));
        return true;
    }

    internal RoomMemberView? RemoveBot(int slot)
    {
        if (!_bots.Remove(slot, out var bot))
            return null;
        if (!_members.Release(slot))
            throw new InvalidOperationException($"Room {Id} lost the reservation for AI seat {slot}.");
        return bot.Appearance;
    }

    internal RoomMemberView[] RemoveBots()
    {
        var removed = _bots.Values.Select(bot => bot.Appearance).OrderBy(bot => bot.Slot).ToArray();
        foreach (var bot in removed)
            _ = RemoveBot(bot.Slot);
        return removed;
    }

    internal void TransferBots(RoomSeat master)
    {
        foreach (var bot in _bots.Values)
        {
            var ai = bot.Appearance.Ai ?? throw new InvalidOperationException("An AI seat lost its controller.");
            bot.Appearance = bot.Appearance with
            {
                UserNum = master.UserNum,
                Ai = ai with { ControllerSlot = master.Slot, ControllerUserNum = master.UserNum },
            };
        }
    }

    internal bool ControlsActor(RoomSeat human, int actor) => actor == RoomRegistry.SceneEntity(human.Slot)
        || _bots.Values.Any(bot => RoomRegistry.SceneEntity(bot.Appearance.Slot) == actor
            && bot.Appearance.Ai is { } ai && ai.ControllerSlot == human.Slot && ai.ControllerUserNum == human.UserNum);

    internal void ResetBotRoundState()
    {
        foreach (var bot in _bots.Values)
        {
            bot.Movement = null;
            bot.Life = SeatLife.Alive;
            bot.DeathCleaned = false;
            bot.ResetRelay = null;
        }
    }
}
