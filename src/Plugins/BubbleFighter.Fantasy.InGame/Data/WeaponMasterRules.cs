using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>An accepted score event of the shared round that can raise a WeaponMaster level.</summary>
internal enum WeaponMasterEvent
{
    /// <summary>A finishing blow the round credited.</summary>
    Kill,

    /// <summary>A knock-down of an opponent the round credited.</summary>
    Knockdown,

    /// <summary>A completed rescue the round credited.</summary>
    Rescue,
}

/// <summary>The names of <see cref="WeaponMasterEvent"/> in configuration.</summary>
internal static class WeaponMasterEvents
{
    /// <summary>Parses one configured event name.</summary>
    internal static bool TryParse(string? name, out WeaponMasterEvent kind)
    {
        switch (name)
        {
            case "kill": kind = WeaponMasterEvent.Kill; return true;
            case "knockdown": kind = WeaponMasterEvent.Knockdown; return true;
            case "rescue": kind = WeaponMasterEvent.Rescue; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>Why the client is shown a weapon change; the client picks the popup text by this word.</summary>
internal enum WeaponMasterReason
{
    // kr-1256 sub_D24CB0 shows the "game start" text for 1, the "level up" text for 2 and the "loser revival" text
    // for every other word; kr-1256 sub_4996C0 names 2 and 3 together for the mode, and 0 is the word of an
    // ordinary weapon pickup (kr-1256 sub_6ED460), so the revival uses 3.

    /// <summary>The weapon a seat starts the round with.</summary>
    Start = 1,

    /// <summary>The weapon of a seat's new level.</summary>
    LevelUp = 2,

    /// <summary>The new weapon the client asked for after its number of deaths since the last weapon.</summary>
    Revival = 3,
}

/// <summary>One weapon of a level and its weight.</summary>
internal sealed record WeaponMasterWeapon(int Item, int Weight);

/// <summary>The validated WeaponMaster rules of a deployment: weapon pools and experience thresholds by level, experience by event.</summary>
internal sealed class WeaponMasterRules
{
    // kr-1256 sub_4996C0 treats equipment slot 0 as the one a mode-26 weapon change targets: only that entry resets the
    // character's death counter and shows the weapon-change popup. The weapon the player fights with is that slot.
    internal const int WeaponSlot = 0;

    /// <summary>Client path of the option sheet; the key below is the only tunable of the mode.</summary>
    internal const string GameOptionPath = "Config/GameOption.xml";

    private const string NoScoreDeathKey = "WeaponMasterNoPangDeadCount";

    private readonly IReadOnlyList<IReadOnlyList<WeaponMasterWeapon>> _levels;
    private readonly int[] _totals;
    private readonly int[] _thresholds;
    private readonly Dictionary<WeaponMasterEvent, int> _points;

    internal WeaponMasterRules(IReadOnlyList<IReadOnlyList<WeaponMasterWeapon>> levels, IReadOnlyList<int> thresholds,
        IReadOnlyDictionary<WeaponMasterEvent, int> points, int noScoreDeathCount)
    {
        if (levels.Count == 0 || levels.Any(level => level.Count == 0 || level.Any(weapon => weapon.Item <= 0 || weapon.Weight <= 0)))
            throw new ConfigException("WeaponMaster levels need weapons with a positive item id and weight.");
        if (thresholds.Count != levels.Count || thresholds[0] != 0
            || thresholds.Zip(thresholds.Skip(1)).Any(pair => pair.Second <= pair.First) || thresholds[^1] >= ushort.MaxValue)
            throw new ConfigException("WeaponMaster experience thresholds must start at 0 and rise with every level.");
        if (points.Count == 0 || points.Values.Any(value => value <= 0))
            throw new ConfigException("WeaponMaster needs at least one event with positive experience.");
        if (noScoreDeathCount < 1)
            throw new ConfigException("The WeaponMaster no-score death count must be at least 1.");
        _levels = levels;
        _totals = [.. levels.Select(level => checked(level.Sum(weapon => weapon.Weight)))];
        _thresholds = [.. thresholds];
        _points = new(points);
        NoScoreDeathCount = noScoreDeathCount;
    }

    /// <summary>Gets how many levels exist; reaching the last one wins the round.</summary>
    internal int LevelCount => _levels.Count;

    /// <summary>Gets the deaths since the last weapon after which the client asks for a new one, and the round grants it.</summary>
    internal int NoScoreDeathCount { get; }

    /// <summary>Gets the experience <paramref name="kind"/> gives; zero for an event the deployment does not reward.</summary>
    internal int Points(WeaponMasterEvent kind) => _points.GetValueOrDefault(kind);

    /// <summary>Gets the experience at which a one-based level starts.</summary>
    internal int Threshold(int level) => _thresholds[Math.Clamp(level, 1, _levels.Count) - 1];

    /// <summary>Gets the highest level whose threshold <paramref name="experience"/> reaches.</summary>
    internal int LevelFor(int experience)
    {
        var level = 1;
        while (level < _thresholds.Length && experience >= _thresholds[level]) level++;
        return level;
    }

    /// <summary>
    /// Gets the experience the next level starts at; past the last level it is one more than the last level's start,
    /// so the gauge reads full.
    /// </summary>
    internal int NextThreshold(int level) => level >= _levels.Count ? _thresholds[^1] + 1 : _thresholds[level];

    /// <summary>Draws one weapon of a one-based level by weight; a level beyond the last uses the last.</summary>
    internal int Draw(int level, Random random)
    {
        var index = Math.Clamp(level, 1, _levels.Count) - 1;
        var ticket = random.Next(_totals[index]);
        foreach (var weapon in _levels[index])
        {
            if (ticket < weapon.Weight) return weapon.Item;
            ticket -= weapon.Weight;
        }
        throw new InvalidOperationException("The weapon draw ran past its pool.");
    }

    /// <summary>Builds the rules from the row's options and the client's own tunable.</summary>
    /// <param name="options">The in-game row's options.</param>
    /// <param name="store">The client data the option sheet is read from.</param>
    /// <exception cref="ConfigException">The options or the client's value are unusable.</exception>
    internal static WeaponMasterRules Create(InGameOptions options, IClientDataStore store)
    {
        if (options.ValidateWeaponMasterOptions().FirstOrDefault() is { } invalid)
            throw new ConfigException(invalid.ErrorMessage!);
        var levels = options.WeaponMasterLevels.Select(level =>
            (IReadOnlyList<WeaponMasterWeapon>)[.. (level.Weapons.Length > 0 ? level.Weapons : options.WeaponMasterWeapons)
                .Select(weapon => new WeaponMasterWeapon(weapon.Item, weapon.Weight))]).ToArray();
        var points = options.WeaponMasterExperience.ToDictionary(entry => WeaponMasterEvents.TryParse(entry.Event, out var kind)
            ? kind : throw new ConfigException($"Unknown WeaponMaster event '{entry.Event}'."), entry => entry.Points);
        return new WeaponMasterRules(levels, [.. options.WeaponMasterLevels.Select(level => level.Experience)], points,
            ReadNoScoreDeathCount(store));
    }

    /// <summary>
    /// Reads <c>WeaponMasterNoPangDeadCount</c> from the client's option sheet: the number of deaths since its last
    /// weapon at which a WeaponMaster client asks for a new one (kr-1256 sub_4A73A0 reads the same key). The server
    /// checks each request against the same value, so it is read from the sheet rather than assumed.
    /// </summary>
    /// <param name="store">The client data the option sheet is read from.</param>
    /// <exception cref="ConfigException">The sheet is missing, repeats the key, or holds no positive integer.</exception>
    internal static int ReadNoScoreDeathCount(IClientDataStore store)
    {
        IReadOnlyList<IReadOnlyList<string>> rows;
        try
        {
            var file = WorkbookFile.FromStore(store, GameOptionPath);
            rows = SpreadsheetWorkbook.Load(file, file.Length, "weapon_master_levels", ["GameOption"])["GameOption"];
        }
        catch (Exception failure) when (failure is FileNotFoundException or InvalidOperationException or System.Xml.XmlException)
        {
            throw new ConfigException($"WeaponMaster cannot read '{GameOptionPath}': {failure.Message}");
        }
        var matches = rows.Where(row => row.Count > 0 && row[0] == NoScoreDeathKey).ToArray();
        if (matches.Length != 1)
            throw new ConfigException($"'{GameOptionPath}' must hold {NoScoreDeathKey} exactly once; it holds {matches.Length}.");
        var match = matches[0];
        if (match.Count < 3 || !int.TryParse(match[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
            throw new ConfigException($"'{GameOptionPath}' {NoScoreDeathKey} must be a positive integer.");
        return count;
    }
}
