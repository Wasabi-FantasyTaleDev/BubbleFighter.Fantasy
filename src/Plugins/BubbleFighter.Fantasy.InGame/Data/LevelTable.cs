using System.Globalization;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// The cumulative experience each level starts at, read from the shared CSV or inline deployment
/// thresholds. A configured cap limits awarded levels; the next-level display retains the existing
/// terminal-step boundary when the table contains no higher level.
///
/// </summary>
internal sealed class LevelTable
{
    private readonly long[] _starts;
    private readonly int _maxLevel;

    /// <summary>Builds the table; <paramref name="starts"/>[0] must be 0 and the list strictly
    /// increasing.</summary> <param name="starts">Total experience at which levels 1, 2, 3,... start.</param>
    /// <param name="maxLevel">Highest awarded level, or zero to retain uncapped inline behavior.</param>
    internal LevelTable(IReadOnlyList<long> starts, int maxLevel = 0)
    {
        if (starts.Count < 2 || starts[0] != 0)
            throw new ArgumentException("level_exp_thresholds needs at least two entries, the first 0.", nameof(starts));
        for (var i = 1; i < starts.Count; i++)
        {
            if (starts[i] <= starts[i - 1])
                throw new ArgumentException($"level_exp_thresholds must increase; entry {i} is {starts[i]}.", nameof(starts));
        }
        _starts = [.. starts];
        if (maxLevel < 0 || maxLevel > starts.Count)
            throw new ArgumentOutOfRangeException(nameof(maxLevel), "The level cap must be covered by the configured table.");
        _maxLevel = maxLevel == 0 ? int.MaxValue : maxLevel;
    }

    internal static void LoadConfiguredFile(InGameOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LevelExpTablePath)) return;
        var file = new FileInfo(options.LevelExpTablePath);
        if (!file.Exists || file.Length > 65536)
            throw new InvalidOperationException("The configured level experience CSV is missing or exceeds 64 KiB.");
        using var reader = file.OpenText();
        if (reader.ReadLine() != "level,total_exp")
            throw new InvalidOperationException("The level experience CSV header must be level,total_exp.");
        var values = new List<long>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(',');
            if (fields.Length != 2 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var level)
                || level != values.Count + 1 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exp))
                throw new InvalidOperationException($"Invalid level experience row {values.Count + 2}.");
            if (values.Count == 10000) throw new InvalidOperationException("The level experience CSV exceeds 10000 levels.");
            values.Add(exp);
        }
        var maximum = options.MaxLevel == 0 ? values.Count : options.MaxLevel;
        _ = new LevelTable(values, maximum);
        options.LevelExpThresholds = values;
        options.MaxLevel = maximum;
    }

    /// <summary>The experience at which <paramref name="level"/> starts.</summary>
    internal long Start(int level)
    {
        if (level < 1)
            throw new ArgumentOutOfRangeException(nameof(level), level, "Levels start at 1.");
        if (level <= _starts.Length)
            return _starts[level - 1];
        var step = _starts[^1] - _starts[^2];
        return checked(_starts[^1] + step * (level - _starts.Length));
    }

    /// <summary>The level a player with <paramref name="exp"/> total experience has.</summary>
    internal int LevelFor(long exp)
    {
        if (exp < 0)
            throw new ArgumentOutOfRangeException(nameof(exp), exp, "Experience cannot be negative.");
        var index = Array.BinarySearch(_starts, exp);
        if (index < 0)
            index = ~index - 1;
        if (index < _starts.Length - 1)
            return Math.Min(_maxLevel, index + 1);
        var step = _starts[^1] - _starts[^2];
        if (_maxLevel <= _starts.Length) return _maxLevel;
        return checked(_starts.Length + (int)((exp - _starts[^1]) / step));
    }
}
