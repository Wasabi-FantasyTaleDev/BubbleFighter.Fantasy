using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes native round entry and signature data for ordinary and tutorial scene transitions.</summary>
internal static class RoundEntryWire
{
    /// <summary>Writes the session mode checked by the native relay join before the stage begins.</summary>
    internal static byte[] WriteSignature(PacketTables tables, string build, int mode) =>
        PacketCodec.Write(tables, build, "SessionSignatureData",
            new Dictionary<string, object?> { ["unknown_4"] = checked((ushort)mode) });

    /// <summary>Writes direct or nested stage fields in the order selected by the native entry table.</summary>
    internal static byte[] Write(PacketTables tables, string build, int unknown0, string mapKey,
        int unknown2 = 0, int unknown3 = 0, int roomState = 0,
        IReadOnlyDictionary<string, object?>? scoreboardValues = null, string stageKey = "", string mapSelection = "",
        RoundSeriesEntry? series = null, int? mapSeed = null)
    {
        if (mapSeed is not null)
            PacketLayoutContract.RequireFields(tables, build, "RoomEnterGame", "Seeded map entry", ("map_seed", "u32"));
        var seriesFields = series is null ? RoundSeriesFields.Load(tables, build) : RoundSeriesFields.Require(tables, build);
        if (series is not null) seriesFields!.Validate(series);
        if (tables.Resolve(build, "RoomEnterGame").Any(field => field.Name == "room_state"))
        {
            // kr-1256 sub_10D1D70, cn-774 sub_105EF50 and tw-454 sub_EBE3C0 read three strings;
            // cn-735 sub_9F4C00 reads one. Each dispatcher installs its named room state directly.
            PacketLayoutContract.RequireFields(tables, build, "RoomEnterGame", "Stage entry",
                ("map_key", "wstr"), ("room_state", "u32"));
            var directValues = new Dictionary<string, object?>
            {
                ["map_key"] = mapKey, ["stage_key"] = stageKey,
                ["map_selection"] = mapSelection, ["room_state"] = roomState,
            };
            if (mapSeed is { } seed) directValues["map_seed"] = seed;
            if (series is not null)
            {
                directValues[seriesFields!.EntryIndex] = series.RoundIndex;
                directValues[seriesFields.EntryAttacker] = checked((byte)series.AttackingTeam);
            }
            return PacketCodec.Write(tables, build, "RoomEnterGame", directValues);
        }
        if (scoreboardValues is null && NeedsScoreboard(tables, build))
            throw new InvalidOperationException("Room entry requires complete ScoreData values.");
        // kr-812 sub_80AE90 reads the scalar suffix; cn-324 sub_6EB6D0 and id-400 sub_782970
        // instead retain their nested ScoreData. The tables keep those distinct wire orders.
        var values = new Dictionary<string, object?>
        {
            ["unknown_4"] = unknown0, ["map_key"] = mapKey,
            ["unknown_36"] = unknown2, ["unknown_40"] = unknown3, ["unknown_44"] = roomState,
            ["unknown_3204"] = unknown2, ["unknown_3208"] = unknown3, ["unknown_3212"] = roomState,
            ["unknown_3028"] = unknown2,
            ["scoreboard"] = scoreboardValues,
            // id-400 sub_780A20 and cn-406 sub_6FEA10 initialize every unused score row as vacant.
            ["initial_score"] = scoreboardValues
                ?? RoundResultWire.ScoreboardValues(tables, build, new RoundScoreboard(0, 0, [0, 0], [])),
        };
        if (seriesFields is not null)
        {
            values[seriesFields.EntryIndex] = unknown2;
            values[seriesFields.EntryAttacker] = unknown3;
            values[seriesFields.EntryState] = roomState;
        }
        if (series is not null)
        {
            values[seriesFields!.EntryIndex] = series.RoundIndex;
            values[seriesFields.EntryAttacker] = series.AttackingTeam;
        }
        return PacketCodec.Write(tables, build, "RoomEnterGame", values);
    }

    /// <summary>Identifies an entry that requires the caller's occupied initial score rows.</summary>
    internal static bool NeedsScoreboard(PacketTables tables, string build) =>
        tables.Resolve(build, "RoomEnterGame").Any(field => field.Name == "scoreboard");
}
