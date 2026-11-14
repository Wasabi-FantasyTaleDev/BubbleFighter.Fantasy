using System.Globalization;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.ItemEnhancement.Data;

/// <summary>The native Favorite weapon grades; the numbering is the client's enum (kr-1256 sub_ED2F90).</summary>
internal enum FavoriteGrade
{
    /// <summary>The starting grade of every weapon.</summary>
    Normal = 0,
    /// <summary>The first promotion.</summary>
    Expert = 1,
    /// <summary>The second promotion.</summary>
    Special = 2,
    /// <summary>The top grade, whose Upgrade row has no next threshold.</summary>
    Master = 3,
}

/// <summary>
/// Reads the client's own <c>Config/FavoriteWeaponSystem.xml</c> sheets "Base" and "Upgrade" (kr-1256 sub_10EE6A0 parses exactly those
/// two) and derives the displayed grade from stored points. The grade is never stored: the client computes the bar from the points and
/// the Upgrade rows (kr-1256 sub_5EE200), so a changed sheet re-grades every weapon without migration.
/// </summary>
internal sealed class FavoriteWeaponCatalog
{
    /// <summary>The ItemAttr selector whose value vector carries grade then points (kr-1256 sub_5EE200 and sub_E03FE0 compare entry[1] with 164).</summary>
    internal const uint AttributeSelector = 164;

    private static readonly string[] GradeLetters = ["S", "A", "B"];
    private static readonly string[] Sheets = ["Base", "Upgrade"];
    private readonly long[] _promotionPoints;

    private FavoriteWeaponCatalog(long[] promotionPoints, int enchantPoints, IReadOnlyList<int> modificationPoints)
    {
        _promotionPoints = promotionPoints;
        EnchantPoints = enchantPoints;
        ModificationPoints = modificationPoints;
    }

    /// <summary>Gets the points a successful enhancement adds (sheet key IncreasePointEnchant).</summary>
    internal int EnchantPoints { get; }

    /// <summary>
    /// Gets the points a successful modification adds, indexed by the client's grade index S=0, A=1, B=2 (sheet key
    /// IncreasePointModification; kr-1256 sub_10EE880 maps the letters to these indices, and an installed attribute's first
    /// category is the same index).
    /// </summary>
    internal IReadOnlyList<int> ModificationPoints { get; }

    /// <summary>Gets the points at which a Normal weapon promotes, then Expert, then Special; Master has no next row.</summary>
    internal IReadOnlyList<long> PromotionPoints => _promotionPoints;

    /// <summary>Derives the grade: a total at or above a grade's Upgrade value promotes out of that grade.</summary>
    internal FavoriteGrade GradeOf(long points)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(points);
        var grade = FavoriteGrade.Normal;
        foreach (var threshold in _promotionPoints)
        {
            if (points < threshold) break;
            grade++;
        }
        return grade;
    }

    /// <summary>Loads and validates both sheets, refusing duplicate, missing, malformed or non-ascending rows.</summary>
    internal static FavoriteWeaponCatalog Load(IClientDataStore store, string path, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sheets = SpreadsheetWorkbook.Load(WorkbookFile.FromStore(store, path), maxBytes, "max_workbook_bytes", Sheets);
        return Parse(sheets["Base"], sheets["Upgrade"], path);
    }

    internal static FavoriteWeaponCatalog Parse(IReadOnlyList<IReadOnlyList<string>> baseRows,
        IReadOnlyList<IReadOnlyList<string>> upgradeRows, string path)
    {
        RequireHeader(baseRows, path, "Base", "Key", "Value");
        RequireHeader(upgradeRows, path, "Upgrade", "FavoriteGrade", "Point");

        var upgrades = new Dictionary<FavoriteGrade, long>();
        foreach (var row in upgradeRows.Skip(1))
        {
            var name = Cell(row, 0);
            if (name.Length == 0) continue;
            if (!Enum.TryParse<FavoriteGrade>(name, ignoreCase: false, out var grade) || !Enum.IsDefined(grade))
                throw new InvalidDataException($"Favorite workbook '{path}' Upgrade has unknown grade '{name}'.");
            if (!long.TryParse(Cell(row, 1), NumberStyles.None, CultureInfo.InvariantCulture, out var points))
                throw new InvalidDataException($"Favorite workbook '{path}' Upgrade grade {name} has no non-negative integer Point.");
            if (!upgrades.TryAdd(grade, points))
                throw new InvalidDataException($"Favorite workbook '{path}' Upgrade repeats grade {name}.");
        }
        foreach (var grade in Enum.GetValues<FavoriteGrade>())
            if (!upgrades.ContainsKey(grade))
                throw new InvalidDataException($"Favorite workbook '{path}' Upgrade lacks grade {grade}.");
        // The Master row's value 0 means no next grade (kr-1256 sub_10EF320 stores it like any other pair).
        if (upgrades[FavoriteGrade.Master] != 0)
            throw new InvalidDataException($"Favorite workbook '{path}' Upgrade Master must be 0 (no next grade).");
        var promotions = new[] { upgrades[FavoriteGrade.Normal], upgrades[FavoriteGrade.Expert], upgrades[FavoriteGrade.Special] };
        if (promotions[0] <= 0 || promotions[1] <= promotions[0] || promotions[2] <= promotions[1] || promotions[2] > uint.MaxValue)
            throw new InvalidDataException($"Favorite workbook '{path}' Upgrade thresholds must be positive and strictly ascending.");

        int? enchant = null;
        var modification = new int?[3];
        foreach (var row in baseRows.Skip(1))
        {
            var key = Cell(row, 0);
            if (key == "IncreasePointEnchant")
            {
                if (enchant is not null) throw new InvalidDataException($"Favorite workbook '{path}' Base repeats IncreasePointEnchant.");
                enchant = Points(Cell(row, 1), path, key);
            }
            else if (key == "IncreasePointModification")
            {
                var parts = Cell(row, 1).Split(',', StringSplitOptions.TrimEntries);
                // kr-1256 sub_10EE880 compares the letter cell with "S", "A" and "B" and stores them as 0, 1 and 2; any other text is -1.
                var index = parts.Length == 2 ? Array.IndexOf(GradeLetters, parts[0]) : -1;
                if (index < 0)
                    throw new InvalidDataException($"Favorite workbook '{path}' Base IncreasePointModification must be 'B|A|S,points'.");
                if (modification[index] is not null)
                    throw new InvalidDataException($"Favorite workbook '{path}' Base repeats IncreasePointModification grade {parts[0]}.");
                modification[index] = Points(parts[1], path, key);
            }
        }
        if (enchant is not { } enchantPoints)
            throw new InvalidDataException($"Favorite workbook '{path}' Base lacks IncreasePointEnchant.");
        if (modification.Any(points => points is null))
            throw new InvalidDataException($"Favorite workbook '{path}' Base must state IncreasePointModification for B, A and S.");
        return new(promotions, enchantPoints, modification.Select(points => points!.Value).ToArray());
    }

    private static void RequireHeader(IReadOnlyList<IReadOnlyList<string>> rows, string path, string sheet, string first, string second)
    {
        if (rows.Count == 0 || Cell(rows[0], 0) != first || Cell(rows[0], 1) != second)
            throw new InvalidDataException($"Favorite workbook '{path}' sheet {sheet} must start with columns {first} and {second}.");
    }

    private static int Points(string text, string path, string key) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value
            : throw new InvalidDataException($"Favorite workbook '{path}' Base {key} must be a positive integer.");

    private static string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column].Trim() : string.Empty;
}