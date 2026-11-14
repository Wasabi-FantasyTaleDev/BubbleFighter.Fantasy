using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Resolves the audited live and complete-board star destinations for explicitly listed client builds.</summary>
internal sealed record StarScoreFields(byte LiveStatKind, string LiveTeamTotals, string BoardCount,
    string BoardTeamTotals, IReadOnlyList<int> Modes, IReadOnlyList<int> TeamModes)
{
    internal static StarScoreFields? Load(PacketTables tables, string build)
    {
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("star_score_builds", out var raw)) return null;
        if (raw is not string[] builds || builds.Length == 0 || builds.Distinct(StringComparer.Ordinal).Count() != builds.Length
            || builds.Any(candidate => !tables.Builds.Contains(candidate, StringComparer.Ordinal)))
            throw new ConfigException("star_score_builds must list distinct known client builds.");
        if (!builds.Contains(build, StringComparer.Ordinal)) return null;
        if (!values.TryGetValue("star_live_stat_kind", out var kind) || kind is not string number
            || !byte.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var statKind))
            throw new ConfigException("star_live_stat_kind must name an audited byte stat kind.");
        var modes = ModesOf("star_score_modes");
        var teamModes = ModesOf("star_score_team_modes");
        if (teamModes.Any(mode => !modes.Contains(mode)))
            throw new ConfigException("star_score_team_modes must be a subset of star_score_modes.");
        PacketLayoutContract.RequireFields(tables, build, "RoundLiveScore", "Star score projection",
            ("team", "u8"), ("total", "u16"), ("scorer_slot", "u8"), ("victim_slot", "u8"),
            ("scorer_stats", "list<RoundLiveScoreStat>"), ("victim_stats", "list<RoundLiveScoreStat>"));
        PacketLayoutContract.RequireFields(tables, build, "RoundLiveScoreStat", "Star score projection",
            ("kind", "u8"), ("value", "u16"));
        return new StarScoreFields(statKind,
            Field("star_live_team_total_field", "RoundLiveScore", "list<RoundLiveScoreStat>"),
            Field("star_scoreboard_count_field", "ScoreRow", "u32"),
            Field("star_scoreboard_team_total_field", "ScoreHeadEntry", "u32"), modes, teamModes);

        string Field(string key, string structure, string type)
        {
            if (!values.TryGetValue(key, out var value) || value is not string name
                || !name.StartsWith("unknown_", StringComparison.Ordinal))
                throw new ConfigException($"{key} must name an audited unnamed score field.");
            PacketLayoutContract.RequireFields(tables, build, structure, "Star score projection", (name, type));
            return name;
        }

        int[] ModesOf(string key)
        {
            if (!values.TryGetValue(key, out var value) || value is not string[] names || names.Length is < 1 or > 16)
                throw new ConfigException($"{key} must list one to sixteen distinct native modes.");
            var result = new int[names.Length];
            for (var index = 0; index < names.Length; index++)
                if (!int.TryParse(names[index], NumberStyles.None, CultureInfo.InvariantCulture, out result[index])
                    || result[index] is <= 0 or > ushort.MaxValue)
                    throw new ConfigException($"{key} must contain positive native mode identifiers.");
            if (result.Distinct().Count() != result.Length)
                throw new ConfigException($"{key} must contain distinct native mode identifiers.");
            return result;
        }
    }
}
