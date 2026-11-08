using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Resolves audited series selectors without putting native offset names in gameplay logic.</summary>
internal sealed record RoundSeriesFields(string SettingsFlags, string? SettingsIndex, string EntryIndex,
    string EntryAttacker, string EntryState, string TeamWins, int RoundsToWin)
{
    internal static RoundSeriesFields? Load(PacketTables tables, string build)
    {
        if (!tables.CapabilityOf(build, "bomb_round_series")) return null;
        if (!tables.ValuesOf(build).TryGetValue("round_series_fields", out var raw)
            || raw is not Dictionary<string, object?> fields || fields.Count != 7)
            throw new ConfigException("Bomb series requires seven audited round_series_fields mappings.");
        var directEntry = tables.Resolve(build, "RoomEnterGame").Any(field => field.Name == "room_state");
        if (!directEntry)
            PacketLayoutContract.RequireFields(tables, build, "RoomEnterGame", "Bomb round series", ("tail", "RoomEnterGameTail"));
        if (!fields.TryGetValue("rounds_to_win", out var wins) || wins is not string text
            || !int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var roundsToWin)
            || roundsToWin is < 2 or > 3)
            throw new ConfigException("Bomb series requires rounds_to_win of two or three from its native series consumer.");
        var entry = directEntry ? "RoomEnterGame" : "RoomEnterGameTail";
        var settingsIndex = directEntry && fields.GetValueOrDefault("settings_index") is null
            ? null : Field("settings_index", "GameSetting");
        var result = new RoundSeriesFields(Field("settings_flags", "GameSetting"), settingsIndex,
            Field("entry_index", entry), Field("entry_attacker", entry, directEntry ? "u8" : "u32"),
            Field("entry_state", entry),
            Field("team_wins", directEntry ? "ScoreHeadEntry" : "ScoreTeam"),
            roundsToWin);
        if (result.SettingsFlags == result.SettingsIndex || new[] { result.EntryIndex, result.EntryAttacker, result.EntryState }.Distinct().Count() != 3)
            throw new ConfigException("Bomb series selectors must use distinct fields within each structure.");
        return result;

        string Field(string key, string structure, string type = "u32")
        {
            if (!fields.TryGetValue(key, out var value) || value is not string name || string.IsNullOrWhiteSpace(name))
                throw new ConfigException($"Bomb series requires round_series_fields.{key}.");
            PacketLayoutContract.RequireFields(tables, build, structure, "Bomb round series", (name, type));
            return name;
        }
    }

    internal static RoundSeriesFields Require(PacketTables tables, string build) => Load(tables, build)
        ?? throw new ConfigException($"Build '{build}' does not support the bomb round-series projection.");

    internal void Validate(RoundSeriesEntry series)
    {
        // kr-812 sub_4FB600 uses decisive index 2; kr-1256 sub_42A4A0 uses 4. Both consume team identifiers 1/2.
        if (series.RoundIndex < 0 || series.RoundIndex > 2 * (RoundsToWin - 1) || series.AttackingTeam is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(series));
    }
}
