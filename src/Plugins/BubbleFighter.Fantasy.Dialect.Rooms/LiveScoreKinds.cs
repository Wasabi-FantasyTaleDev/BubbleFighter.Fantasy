using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Resolves the native stat identifiers for the build's live seat counters.</summary>
internal sealed record LiveScoreKinds(byte Score, byte Knockdowns, byte Kills, byte Deaths, byte? Stars, byte? CollectedLucci)
{
    private static readonly ConditionalWeakTable<PacketTables, ConcurrentDictionary<string, LiveScoreKinds>> Resolved = new();

    /// <summary>Validates and retains a build's counter identifiers for one immutable packet snapshot.</summary>
    internal static LiveScoreKinds Require(PacketTables tables, string build) =>
        Resolved.GetValue(tables, static _ => new(StringComparer.Ordinal))
            .GetOrAdd(build, static (name, snapshot) => Read(snapshot, name), tables);

    private static LiveScoreKinds Read(PacketTables tables, string build)
    {
        if (!tables.ValuesOf(build).TryGetValue("live_score_kinds", out var raw)
            || raw is not Dictionary<string, object?> kinds || kinds.Count != 6
            || kinds.Keys.Any(key => key is not ("score" or "knockdowns" or "kills" or "deaths" or "stars" or "collected_lucci")))
            throw new ConfigException($"Build '{build}' requires six named live_score_kinds entries.");
        var result = new LiveScoreKinds(Required("score"), Required("knockdowns"), Required("kills"), Required("deaths"),
            Optional("stars"), Optional("collected_lucci"));
        byte?[] values = [result.Score, result.Knockdowns, result.Kills, result.Deaths, result.Stars, result.CollectedLucci];
        var present = values.Where(value => value.HasValue).ToArray();
        if (present.Distinct().Count() != present.Length)
            throw new ConfigException($"Build '{build}' live_score_kinds must use distinct native identifiers.");
        if (result.CollectedLucci is null && RoundResultWire.CollectedLucciField(tables, build) is not null)
            throw new ConfigException($"Build '{build}' requires live_score_kinds.collected_lucci for its admitted pickup counter.");
        PacketLayoutContract.RequireFields(tables, build, "RoundLiveScoreStat", "Live score counters",
            ("kind", "u8"), ("value", "u16"));
        return result;

        byte Required(string name) => Optional(name)
            ?? throw new ConfigException($"Build '{build}' requires live_score_kinds.{name}.");

        byte? Optional(string name)
        {
            if (kinds[name] is null) return null;
            if (kinds[name] is string value && byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kind))
                return kind;
            throw new ConfigException($"Build '{build}' live_score_kinds.{name} must be a byte identifier or null.");
        }
    }
}
