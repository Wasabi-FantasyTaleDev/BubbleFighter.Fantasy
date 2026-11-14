using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>One weapon entry of a cannon: the bullet, how many the entry names and the scale it asks for.</summary>
/// <param name="WeaponId">The weapon, from 50000 through 50008.</param>
/// <param name="Count">The entry's COUNT value; the client code that consumes it has not been located.</param>
/// <param name="Scale">The entry's SCALE value, finite and positive.</param>
internal sealed record ExtremeRacingCannonWeapon(int WeaponId, int Count, float Scale);

/// <summary>One cannon of a map's <c>cannon.ini</c>.</summary>
/// <param name="IntervalMs">The INTERVAL value in milliseconds; whether it is a period is not established by the client.</param>
/// <param name="Stage">The STAGE value, from one through the map's stage count; its meaning is not established.</param>
/// <param name="Weapons">The cannon's weapon entries in file order.</param>
internal sealed record ExtremeRacingCannonEntry(int IntervalMs, int Stage, IReadOnlyList<ExtremeRacingCannonWeapon> Weapons);

/// <summary>
/// A racing map's validated cannon data, read from <c>Maps/&lt;stage&gt;/cannon.ini</c> beside the <c>option.ini</c> that
/// <see cref="ExtremeRacingMapData"/> reads. Entry order is the cannon number, which matches the numeric cannon pair
/// order of the initialization. The data is server input only: the client reads no INTERVAL or STAGE key that has been
/// found, so no cadence here is native.
/// </summary>
/// <param name="Cannons">One entry per initialized cannon pair, in cannon number order.</param>
internal sealed record ExtremeRacingCannonSchedule(IReadOnlyList<ExtremeRacingCannonEntry> Cannons)
{
    /// <summary>The lowest weapon the client accepts for a cannon command (kr-1256 sub_67B6E0).</summary>
    internal const int FirstWeapon = 50000;

    /// <summary>The highest weapon the client accepts for a cannon command (kr-1256 sub_67B6E0).</summary>
    internal const int LastWeapon = 50008;

    /// <summary>Returns whether the cannon's resource lists the weapon.</summary>
    /// <param name="cannon">The zero-based cannon number.</param>
    /// <param name="weapon">The weapon to look for.</param>
    internal bool Lists(int cannon, long weapon) => cannon >= 0 && cannon < Cannons.Count
        && Cannons[cannon].Weapons.Any(entry => entry.WeaponId == weapon);

    /// <summary>
    /// Reads and validates a map's cannon data. A malformed file, a missing cannon or weapon section, an out-of-range
    /// value or a nonzero cannon count that differs from the initialized pair count fails; nothing falls back to a default.
    /// A declared count of zero is a map without scheduled cannons.
    /// </summary>
    /// <param name="store">The client data the map's resources come from.</param>
    /// <param name="stageDirectory">The checked relative map directory under <c>Maps</c>.</param>
    /// <param name="initializedCannonCount">The map's initialized cannon pair count (option.ini <c>Cannon/PairCount</c>).</param>
    /// <param name="stageCount">The map's stage count; a cannon's STAGE must name one of them.</param>
    /// <param name="maximumFileBytes">The inclusive file size limit.</param>
    /// <param name="maximumObjects">The deployment bound on cannons and on weapons per cannon.</param>
    /// <returns>The validated schedule.</returns>
    /// <exception cref="InvalidDataException">The file is malformed or disagrees with the initialization.</exception>
    internal static ExtremeRacingCannonSchedule Load(IClientDataStore store, string stageDirectory, int initializedCannonCount,
        int stageCount, int maximumFileBytes, int maximumObjects)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(initializedCannonCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(stageCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumObjects, 1);
        if (string.IsNullOrWhiteSpace(stageDirectory) || stageDirectory.Contains(':') || stageDirectory.Contains('\\')
            || stageDirectory.Any(char.IsControl) || stageDirectory.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("ExtremeRacing cannon data requires a relative map Directory without traversal.");
        var path = $"Maps/{stageDirectory}/cannon.ini";
        return Parse(store.Read(path, maximumFileBytes), maximumFileBytes, initializedCannonCount, stageCount, maximumObjects);
    }

    /// <summary>Parses and validates the bytes of one <c>cannon.ini</c>; see <see cref="Load"/>.</summary>
    internal static ExtremeRacingCannonSchedule Parse(byte[] bytes, int maximumFileBytes, int initializedCannonCount,
        int stageCount, int maximumObjects)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > maximumFileBytes)
            throw new InvalidDataException("ExtremeRacing cannon data exceeds its resource byte limit.");
        var sections = ReadSections(bytes);
        if (!sections.TryGetValue("Count", out var count))
            throw new InvalidDataException("ExtremeRacing cannon data has no [Count] section.");
        var declared = Number(count, "CANNONCOUNT", 0, maximumObjects);
        // A map that declares no cannons schedules none: its initialized pairs stay idle and the ledger admits no shot.
        // Any other disagreement with the initialized pair count is inconsistent data.
        if (declared != 0 && declared != initializedCannonCount)
            throw new InvalidDataException(
                $"ExtremeRacing cannon data declares {declared} cannons but the map initializes {initializedCannonCount}.");

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Count" };
        var cannons = new ExtremeRacingCannonEntry[declared];
        for (var index = 0; index < declared; index++)
        {
            var name = $"Cannon{index.ToString(CultureInfo.InvariantCulture)}";
            if (!sections.TryGetValue(name, out var cannon))
                throw new InvalidDataException($"ExtremeRacing cannon data has no [{name}] section.");
            known.Add(name);
            var weaponCount = Number(cannon, "WEAPONCOUNT", 1, maximumObjects);
            var interval = Number(cannon, "INTERVAL", 1, int.MaxValue);
            var stage = Number(cannon, "STAGE", 1, stageCount);
            var weapons = new ExtremeRacingCannonWeapon[weaponCount];
            for (var slot = 0; slot < weaponCount; slot++)
            {
                var weaponName = $"{name}_Weapon{slot.ToString(CultureInfo.InvariantCulture)}";
                if (!sections.TryGetValue(weaponName, out var weapon))
                    throw new InvalidDataException($"ExtremeRacing cannon data has no [{weaponName}] section.");
                known.Add(weaponName);
                weapons[slot] = new(Number(weapon, "WEAPONID", FirstWeapon, LastWeapon),
                    Number(weapon, "COUNT", 1, int.MaxValue), Scale(weapon));
            }
            cannons[index] = new(interval, stage, Array.AsReadOnly(weapons));
        }
        // A cannon or weapon section the declared counts do not cover is data this reader would silently ignore.
        if (sections.Keys.FirstOrDefault(section => !known.Contains(section) && IsCannonSection(section)) is { } extra)
            throw new InvalidDataException($"ExtremeRacing cannon data has the undeclared section [{extra}].");
        return new(Array.AsReadOnly(cannons));
    }

    private static bool IsCannonSection(string section) =>
        section.StartsWith("Cannon", StringComparison.OrdinalIgnoreCase) && section.Length > 6 && char.IsAsciiDigit(section[6]);

    private static Dictionary<string, Dictionary<string, string>> ReadSections(byte[] bytes)
    {
        // Only ASCII section and key names are consumed; legacy comment bytes after ';' are preserved unread.
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.Latin1.GetString(bytes.AsSpan(hasBom ? 3 : 0));
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Split(';', 2)[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new InvalidDataException("ExtremeRacing cannon section is malformed.");
                var name = line[1..^1].Trim();
                if (name.Length == 0 || !sections.TryAdd(name, current = new(StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"ExtremeRacing cannon section [{name}] is empty-named or duplicated.");
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals < 1 || current is null) throw new InvalidDataException("ExtremeRacing cannon field is malformed.");
            if (!current.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim()))
                throw new InvalidDataException("ExtremeRacing cannon field is duplicated within its section.");
        }
        return sections;
    }

    private static int Number(Dictionary<string, string> section, string key, int minimum, int maximum) =>
        section.TryGetValue(key, out var text)
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= minimum && value <= maximum
            ? value : throw new InvalidDataException($"ExtremeRacing cannon field '{key}' is missing, invalid or out of range.");

    private static float Scale(Dictionary<string, string> section) =>
        section.TryGetValue("SCALE", out var text)
        && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && float.IsFinite(value) && value > 0
            ? value : throw new InvalidDataException("ExtremeRacing cannon field 'SCALE' is missing, invalid or not positive.");
}
