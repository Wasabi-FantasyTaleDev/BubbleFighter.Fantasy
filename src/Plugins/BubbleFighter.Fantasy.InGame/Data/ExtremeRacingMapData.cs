using System.Globalization;
using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Explicit operator assignments for one map; helper resources do not supply the coin-round map.</summary>
internal sealed record ExtremeRacingMapPolicy(uint FirstCannonIdentity, uint FirstCoinIdentity,
    uint FirstStoneDisposerIdentity, int CoinVariant, IReadOnlyList<int> CoinRounds);

/// <summary>Deployment bounds applied before reading map resources or creating initialization collections.</summary>
internal sealed record ExtremeRacingMapLimits(int MaximumFileBytes, int MaximumHelpers, int MaximumObjects);

/// <summary>A validated map's native counts, numeric coin positions and complete one-time initialization.</summary>
internal sealed record ExtremeRacingMapData(int StageCount, int CannonPairCount,
    IReadOnlyList<Vector3> CoinPositions, RoundExtremeRacingInitialized Initialization)
{
    /// <summary>Gets the validated cannon data, or null when the cannon relay is not enabled for this map.</summary>
    internal ExtremeRacingCannonSchedule? Cannons { get; init; }

    /// <summary>Reads bounded native map resources and combines them with explicit per-map server coin policy.</summary>
    internal static ExtremeRacingMapData Load(IClientDataStore store, string stageDirectory,
        ExtremeRacingMapPolicy policy, ExtremeRacingMapLimits limits, IReadOnlyCollection<int> reservedActorIdentities)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.CoinRounds);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(reservedActorIdentities);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumFileBytes, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumHelpers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumObjects, 1);
        if (string.IsNullOrWhiteSpace(stageDirectory) || stageDirectory.Contains(':') || stageDirectory.Contains('\\')
            || stageDirectory.Any(char.IsControl)
            || stageDirectory.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("ExtremeRacing requires a relative map Directory without traversal.");
        if (policy.CoinVariant != 0)
            throw new InvalidDataException("ExtremeRacing initialization currently supports only explicitly selected coin variant zero.");
        // kr-1256 sub_1155540 has 32 fixed bytes and two eight-byte map entries per coin.
        if (policy.CoinRounds.Count > limits.MaximumObjects || policy.CoinRounds.Count > (ushort.MaxValue - 32) / 16)
            throw new InvalidDataException("ExtremeRacing coin policy exceeds its object or packet limit.");

        var optionPath = $"Maps/{stageDirectory}/option.ini";
        var values = ReadOptions(store.Read(optionPath, limits.MaximumFileBytes), limits.MaximumFileBytes);
        // kr-1256 sub_67B920 supplies these native fallbacks when the corresponding option key is absent.
        var cannonCount = Number(values, "Cannon/PairCount", 9, 0, limits.MaximumObjects);
        var coinCount = Number(values, "Coin/PairCount", 33, 0, limits.MaximumObjects);
        var stageCount = Number(values, "SavePoint/StageCount", 5, 1, limits.MaximumHelpers);
        var stoneStage = Number(values, "GrenadeStoneArea/ProcessStage", null, 0, stageCount);
        // kr-1256 sub_BF4DB0 loads stone_disappear helpers only when ProcessStage is positive; its count fallback is 18.
        var stoneCount = stoneStage == 0 ? 0 : Number(values, "GrenadeStoneArea/DisappearCount", 18, 0, limits.MaximumObjects);
        var objectCount = 2L * cannonCount + coinCount + stoneCount;
        if (objectCount > limits.MaximumObjects || coinCount != policy.CoinRounds.Count)
            throw new InvalidDataException("ExtremeRacing object limit or complete coin-round assignment does not match the map.");
        if (policy.CoinRounds.Any(round => round < 1 || round > stageCount))
            throw new InvalidDataException("ExtremeRacing coin rounds must lie within the native map StageCount.");

        var ranges = new[]
        {
            IdentityRange(policy.FirstCannonIdentity, 2L * cannonCount),
            IdentityRange(policy.FirstCoinIdentity, coinCount),
            IdentityRange(policy.FirstStoneDisposerIdentity, stoneCount),
        };
        if (reservedActorIdentities.Count == 0 || reservedActorIdentities.Any(actor => actor <= 0))
            throw new InvalidDataException("ExtremeRacing requires the complete positive actor identity reservation.");
        var actorNamespaceStart = checked((uint)reservedActorIdentities.Min());
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.Count == 0) continue;
            if (range.Last >= actorNamespaceStart)
                throw new InvalidDataException("ExtremeRacing object identities overlap the reserved actor namespace.");
            for (var other = index + 1; other < ranges.Length; other++)
                if (ranges[other].Count > 0 && range.First <= ranges[other].Last && ranges[other].First <= range.Last)
                    throw new InvalidDataException("ExtremeRacing cannon, coin and stone identity ranges overlap.");
        }

        var helpers = WorldBlockFile.LoadHelpers(store, $"Maps/{stageDirectory}/track.wb",
            limits.MaximumFileBytes, limits.MaximumHelpers);
        // kr-1256 sub_67B920 looks up numeric suffixes; WB record order never assigns the native identities.
        _ = OrderedHelpers(helpers, "#eff_cannon_in", cannonCount);
        _ = OrderedHelpers(helpers, "#eff_cannon_out", cannonCount);
        var coinHelpers = OrderedHelpers(helpers, "#eff_coin", coinCount);
        if (stoneStage > 0) _ = OrderedHelpers(helpers, "stone_disappear", stoneCount);
        var coins = new RoundExtremeRacingCoin[coinCount];
        for (var index = 0; index < coins.Length; index++)
            coins[index] = new(checked(policy.FirstCoinIdentity + (uint)index), policy.CoinVariant, policy.CoinRounds[index]);
        return new(stageCount, cannonCount, Array.AsReadOnly(coinHelpers.Select(helper => helper.Position).ToArray()),
            new(policy.FirstCannonIdentity, policy.FirstCoinIdentity, Array.AsReadOnly(coins),
                policy.FirstStoneDisposerIdentity, stoneCount));
    }

    private static (uint First, uint Last, long Count) IdentityRange(uint first, long count)
    {
        if (first == 0 || count < 0 || count > uint.MaxValue || (ulong)first + (ulong)Math.Max(0, count - 1) > uint.MaxValue)
            throw new InvalidDataException("ExtremeRacing object identities must be nonzero and must not overflow.");
        return (first, checked(first + (uint)Math.Max(0, count - 1)), count);
    }

    private static WorldBlockHelper[] OrderedHelpers(IReadOnlyList<WorldBlockHelper> helpers, string prefix, int count)
    {
        var indexed = new Dictionary<int, WorldBlockHelper>();
        foreach (var helper in helpers.Where(helper => helper.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var suffix = helper.Name.AsSpan(prefix.Length);
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= count || suffix.Length < 2
                || helper.Name != prefix + index.ToString("D2", CultureInfo.InvariantCulture)
                || !indexed.TryAdd(index, helper))
                throw new InvalidDataException($"ExtremeRacing has an unexpected or duplicate '{prefix}' helper.");
        }
        if (indexed.Count != count)
            throw new InvalidDataException($"ExtremeRacing requires every numeric '{prefix}' helper from zero through {count - 1}.");
        return Enumerable.Range(0, count).Select(index => indexed[index]).ToArray();
    }

    private static Dictionary<string, string> ReadOptions(byte[] bytes, int maximumBytes)
    {
        if (bytes.Length > maximumBytes) throw new InvalidDataException("ExtremeRacing options exceed their resource byte limit.");
        // Only ASCII section/key names and integer values are consumed; the native file's legacy comment bytes are preserved.
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.Latin1.GetString(bytes.AsSpan(hasBom ? 3 : 0));
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Split(';', 2)[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new InvalidDataException("ExtremeRacing option section is malformed.");
                section = line[1..^1].Trim();
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals < 1) throw new InvalidDataException("ExtremeRacing option field is malformed.");
            var key = section + "/" + line[..equals].Trim();
            if (!IsRequiredField(key)) continue;
            if (!fields.TryAdd(key, line[(equals + 1)..].Trim()))
                throw new InvalidDataException($"ExtremeRacing option '{key}' is duplicated.");
        }
        return fields;
    }

    private static bool IsRequiredField(string key) => key.Equals("Cannon/PairCount", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Coin/PairCount", StringComparison.OrdinalIgnoreCase)
        || key.Equals("SavePoint/StageCount", StringComparison.OrdinalIgnoreCase)
        || key.Equals("GrenadeStoneArea/ProcessStage", StringComparison.OrdinalIgnoreCase)
        || key.Equals("GrenadeStoneArea/DisappearCount", StringComparison.OrdinalIgnoreCase);

    private static int Number(IReadOnlyDictionary<string, string> fields, string key, int? fallback, int minimum, int maximum)
    {
        var value = fields.TryGetValue(key, out var text)
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1
            : fallback ?? -1;
        return value >= minimum && value <= maximum ? value
            : throw new InvalidDataException($"ExtremeRacing option '{key}' is missing, invalid or exceeds its bound.");
    }
}
