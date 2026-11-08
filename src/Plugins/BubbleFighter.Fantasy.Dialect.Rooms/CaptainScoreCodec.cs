using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Translates the audited Captain report without narrowing native seat words to bytes.</summary>
internal sealed class CaptainScoreCodec
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly string _victimSeat;
    private readonly string _killerSeat;
    private readonly int _kind;

    internal CaptainScoreCodec(PacketTables tables, string build)
    {
        _tables = tables;
        _build = build;
        _kind = PlayValues.Of(tables, build).Score.CaptainKill;
        if (_kind <= 0 || !tables.ValuesOf(build).TryGetValue("captain_score_fields", out var raw)
            || raw is not Dictionary<string, object?> fields || fields.Count != 2)
            throw new ConfigException("Captain score reports require an audited kind and two captain_score_fields mappings.");
        _victimSeat = Field("victim_seat");
        _killerSeat = Field("killer_seat");
        if (_victimSeat == _killerSeat)
            throw new ConfigException("Captain score seat mappings must be distinct.");
        PacketLayoutContract.RequireFields(tables, build, "RoundScoreReport", "Captain score",
            ("unknown_8", "u32"), ("unknown_12", "u32"));
        return;

        string Field(string key)
        {
            if (!fields.TryGetValue(key, out var value) || value is not string name || string.IsNullOrWhiteSpace(name)
                || tables.Resolve(build, "RoundScoreReport").SingleOrDefault(field => field.Name == name)?.Type is not ("u8" or "u32"))
                throw new ConfigException($"Captain score requires captain_score_fields.{key} to name a byte or word field.");
            return name;
        }
    }

    /// <summary>Reads the complete native body; unrelated kinds never become gameplay reports.</summary>
    internal RoundFinishingBlowReported? Read(ReadOnlySpan<byte> body) =>
        Read(PacketCodec.ReadScalars(_tables, _build, "RoundScoreReport", body));

    /// <summary>Checks both scene references against their full-width seat values before shared ownership admission.</summary>
    internal RoundFinishingBlowReported? Read(IReadOnlyDictionary<string, object> fields)
    {
        if (Convert.ToInt32(fields["kind"]) != _kind)
            return null;
        // id-400 sub_421AE0 / cn-404 sub_426670 carry word seats; kr-1256 sub_46FB10 carries byte seats.
        var captain = PlayRoomCodec.PlayerSeatOf(unchecked((int)fields["unknown_8"]));
        var killer = PlayRoomCodec.PlayerSeatOf(unchecked((int)fields["unknown_12"]));
        if (captain < 0 || killer < 0 || captain == killer
            || Convert.ToInt32(fields[_victimSeat]) != captain || Convert.ToInt32(fields[_killerSeat]) != killer)
            return null;
        return new RoundFinishingBlowReported(killer)
        {
            Victim = captain, HasVictimReference = true, RequiresReporterOwnership = true, CaptainKill = true,
        };
    }
}
