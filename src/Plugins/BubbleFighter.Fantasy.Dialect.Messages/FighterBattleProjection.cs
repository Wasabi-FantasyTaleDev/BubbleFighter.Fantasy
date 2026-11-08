using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Projects recorded no-item/item counters into the audited native primary page without changing stored totals.</summary>
internal sealed record FighterBattleProjection(int Wins, int Losses, int Draws, int Escapes,
    int Knockdowns, int Kills, int Rescues)
{
    internal static FighterBattleProjection? Create(IReadOnlyList<PlayerBattleRecord> history)
    {
        long wins = 0, losses = 0, draws = 0, escapes = 0, knockdowns = 0, kills = 0, rescues = 0;
        var seenPvp = false;
        var seenItem = false;
        try
        {
            checked
            {
                foreach (var mode in history)
                {
                    // DlgFighterInfoPage1 names No Item/Item. kr-812 sub_8E8900 and kr-1256 sub_1134B50
                    // independently identify modes 1/3; other mode pages and arena ratings are not established.
                    if (mode.ModeId != (int)GameModeId.Pvp && mode.ModeId != (int)GameModeId.Item) continue;
                    if (mode.ModeId == (int)GameModeId.Pvp)
                    {
                        if (seenPvp) return null;
                        seenPvp = true;
                    }
                    else
                    {
                        if (seenItem) return null;
                        seenItem = true;
                    }
                    if (mode.Wins < 0 || mode.Losses < 0 || mode.Draws < 0 || mode.Escapes < 0
                        || mode.Knockdowns < 0 || mode.Kills < 0 || mode.Rescues < 0) return null;
                    wins += mode.Wins;
                    losses += mode.Losses;
                    draws += mode.Draws;
                    escapes += mode.Escapes;
                    knockdowns += mode.Knockdowns;
                    kills += mode.Kills;
                    rescues += mode.Rescues;
                }
                if (!seenPvp && !seenItem) return null;
                // Native consumers include signed W*10000, W+L+D and bubble+2*(pang+help):
                // kr-812 sub_87DD90, cn-279 sub_83B330 and kr-1256 sub_5DB5C0. Refuse overflow, never scale counts.
                if (wins > int.MaxValue / 10000 || wins + losses + draws > int.MaxValue || escapes > int.MaxValue
                    || knockdowns + 2 * (kills + rescues) > int.MaxValue) return null;
                return new((int)wins, (int)losses, (int)draws, (int)escapes, (int)knockdowns, (int)kills, (int)rescues);
            }
        }
        catch (OverflowException) { return null; }
    }

    internal void AddTo(Dictionary<string, object?> values)
    {
        values["primary_wins"] = Wins;
        values["primary_losses"] = Losses;
        values["primary_draws"] = Draws;
        values["primary_escapes"] = Escapes;
        values["primary_knockdowns"] = Knockdowns;
        values["primary_kills"] = Kills;
        values["primary_rescues"] = Rescues;
    }
}
