using BubbleFighter.Fantasy.Contracts.Progress;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>A per-mode aggregate committed with the existing round settlement receipt.</summary>
[SugarTable("player_battle_records")]
internal sealed class BattleRecordRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }
    [SugarColumn(ColumnName = "mode_id", IsPrimaryKey = true)]
    public int ModeId { get; set; }
    [SugarColumn(ColumnName = "wins")]
    public long Wins { get; set; }
    [SugarColumn(ColumnName = "draws")]
    public long Draws { get; set; }
    [SugarColumn(ColumnName = "losses")]
    public long Losses { get; set; }
    [SugarColumn(ColumnName = "escapes")]
    public long Escapes { get; set; }
    [SugarColumn(ColumnName = "knockdowns")]
    public long Knockdowns { get; set; }
    [SugarColumn(ColumnName = "kills")]
    public long Kills { get; set; }
    [SugarColumn(ColumnName = "rescues")]
    public long Rescues { get; set; }
    [SugarColumn(ColumnName = "deaths")]
    public long Deaths { get; set; }
    [SugarColumn(ColumnName = "score")]
    public long Score { get; set; }

    internal PlayerBattleRecord ToRecord()
    {
        if (UserNum <= 0 || ModeId <= 0 || Wins < 0 || Draws < 0 || Losses < 0 || Escapes < 0
            || Knockdowns < 0 || Kills < 0 || Rescues < 0 || Deaths < 0 || Score < 0)
            throw new InvalidDataException("A stored battle record has invalid identity or counters.");
        var record = new PlayerBattleRecord(ModeId, Wins, Draws, Losses, Escapes, Knockdowns, Kills, Rescues, Deaths, Score);
        _ = record.Matches;
        return record;
    }

    internal static BattleRecordRow From(long user, PlayerBattleRecord record) => new()
    {
        UserNum = user, ModeId = record.ModeId,
        Wins = record.Wins, Draws = record.Draws, Losses = record.Losses, Escapes = record.Escapes,
        Knockdowns = record.Knockdowns, Kills = record.Kills, Rescues = record.Rescues, Deaths = record.Deaths, Score = record.Score,
    };
}
