using System.Globalization;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Data;

internal sealed partial record ExtremeRacingMapData
{
    /// <summary>Validates every native vanish route and helper, returning only bounded per-group route counts.</summary>
    internal static int[] ReadVanishRouteCounts(IClientDataStore store, string stage, InGameOptions options)
    {
        ValidateStageDirectory(stage);
        var fields = ReadOptions(store.Read($"Maps/{stage}/option.ini", options.ExtremeRacingMaximumFileBytes),
            options.ExtremeRacingMaximumFileBytes, key => key.StartsWith("VanishArea/", StringComparison.OrdinalIgnoreCase));
        var processStage = Number(fields, "VanishArea/ProcessStage", 0, 0, 5);
        if (processStage == 0) return [];
        // kr-1256 sub_BF89A0 reads AreaGroupSize, not AreaGroupCount, and requires complete equal-length routes.
        var groups = Number(fields, "VanishArea/AreaGroupSize", 3, 1, options.ExtremeRacingMaximumVanishGroups);
        var helpers = WorldBlockFile.LoadHelpers(store, $"Maps/{stage}/track.wb",
            options.ExtremeRacingMaximumFileBytes, options.ExtremeRacingMaximumHelpers);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var helper in helpers)
            if (helper.Name.StartsWith("#eff_vanishing_", StringComparison.Ordinal) && !names.Add(helper.Name))
                throw new InvalidDataException("ExtremeRacing repeats a vanish tile helper.");
        var counts = new int[groups];
        for (var group = 0; group < groups; group++)
        {
            var total = Number(fields, $"VanishArea/TileTotalSize_{group}", 36, 1, options.ExtremeRacingMaximumVanishTiles);
            var columns = Number(fields, $"VanishArea/TileColumnSize_{group}", 6, 1, options.ExtremeRacingMaximumVanishTiles);
            _ = Number(fields, $"VanishArea/TileRowSize_{group}", 6, 1, options.ExtremeRacingMaximumVanishTiles);
            if ((long)columns * columns != total)
                throw new InvalidDataException("ExtremeRacing vanish tile count fails the native square-column check.");
            counts[group] = Number(fields, $"VanishArea/AreaCount_{group}", 10, 1, options.ExtremeRacingMaximumVanishRoutes);
            var length = -1;
            for (var route = 0; route < counts[group]; route++)
            {
                var key = $"VanishArea/Area_{group}_{route:00}";
                if (!fields.TryGetValue(key, out var text))
                    throw new InvalidDataException($"ExtremeRacing is missing native route '{key}'.");
                var entries = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (entries.Length == 0 || entries.Length > total || length >= 0 && entries.Length != length)
                    throw new InvalidDataException("ExtremeRacing vanish routes must have equal nonzero bounded lengths within each group.");
                foreach (var entry in entries)
                    if (!int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var tile) || tile < 0 || tile >= total)
                        throw new InvalidDataException("ExtremeRacing vanish route references an invalid native tile.");
                length = entries.Length;
            }
            for (var tile = 0; tile < total; tile++)
                if (!names.Contains($"#eff_vanishing_{group}_{tile:00}"))
                    throw new InvalidDataException("ExtremeRacing vanish group is missing a required native tile helper.");
        }
        return counts;
    }

    /// <summary>Draws one complete shared state according to explicit server policy, independent of the client's RNG algorithm.</summary>
    internal static RoundExtremeRacingMapState SelectMapState(IReadOnlyList<int> routes, InGameOptions options)
    {
        if (options.ValidateExtremeRacingMapState().FirstOrDefault() is { } invalid)
            throw new InvalidDataException(invalid.ErrorMessage);
        if (routes.Count > options.ExtremeRacingMaximumVanishGroups || routes.Any(count => count < 1 || count > options.ExtremeRacingMaximumVanishRoutes))
            throw new InvalidDataException("ExtremeRacing route selection exceeds configured group or route bounds.");
        var random = options.ExtremeRacingMapStateSelection == "seeded" ? new Random(options.ExtremeRacingMapStateSeed!.Value) : null;
        int Draw(int count) => random?.Next(count) ?? RandomNumberGenerator.GetInt32(count);
        // kr-1256 sub_67E4A0 indexes ten 5-by-5 bomb patterns; kr-1256 sub_BF4A70 consumes a shared stone RNG seed.
        var second = Draw(10);
        var third = Draw(10);
        var seed = Draw(int.MaxValue);
        return new(second, third, seed, Array.AsReadOnly(routes.Select(Draw).ToArray()));
    }
}
