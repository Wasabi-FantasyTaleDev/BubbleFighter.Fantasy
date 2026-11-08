using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The optional deployment rules a master's start must meet: a minimum player count and, in red-or-blue team modes,
/// balanced teams. Both are off by default; the client has no refusal reason for them, so a refused start is only
/// logged and the room keeps waiting.
/// </summary>
internal sealed partial class RoomFlow
{
    // Under the room's gate, before the start changes any team. Null when the start may go ahead.
    private string? StartRuleRefusal(PlayRoom room)
    {
        if (_options is not { } options)
            return null;
        var players = room.Seats.Count + room.Bots.Count;
        if (players < options.StartMinPlayers)
            return $"{players} player(s), {options.StartMinPlayers} required";
        if (!options.StartRequiresTeamBalance || !room.RequiresSpawnTeams || IsZombiePvp(room)
            || (options.ZombieConversionModeId > 0 && room.ModeId == options.ZombieConversionModeId))
            return null;
        // A player without a team counts as red, the team every seat of an untouched room shows, unless the start
        // places the seats without a team on the smaller side first (PlayRoom.AssignSpawnTeams).
        var red = room.Seats.Count(seat => seat.Team == 1) + room.Bots.Count(bot => bot.Appearance.Team == 1);
        var blue = room.Seats.Count(seat => seat.Team == 2) + room.Bots.Count(bot => bot.Appearance.Team == 2);
        var unassigned = room.Seats.Count(seat => seat.Team == 0);
        if (options.AssignSpawnTeamsOnStart)
        {
            for (; unassigned > 0; unassigned--)
                if (red <= blue) red++;
                else blue++;
        }
        else
            red += unassigned;
        return Math.Abs(red - blue) > 1 ? $"teams of {red} and {blue}" : null;
    }
}
