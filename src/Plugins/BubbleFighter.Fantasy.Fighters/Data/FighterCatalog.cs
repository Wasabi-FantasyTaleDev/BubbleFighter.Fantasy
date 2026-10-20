namespace BubbleFighter.Fantasy.Fighters.Data;

/// <summary>
/// One HorzTable column from worksheet <c>FighterConfig</c>. <see cref="ItemId"/> is
/// <c>300000 + FieldId</c> when <see cref="FieldId"/> is in the class-5 window 1..31
/// (<c>sub_1135760</c> / <c>sub_EF12E0</c> / <c>sub_F46930</c>); otherwise 0.
/// </summary>
/// <param name="FieldId">HorzTable <c>[Field]</c> column key.</param>
/// <param name="Name">HorzTable <c>name</c> cell for that column.</param>
/// <param name="ChrTypeIdx">HorzTable <c>chrTypeIdx</c> cell.</param>
/// <param name="IsSelectable">HorzTable <c>isSelectable</c> cell (nonzero / true).</param>
/// <param name="ItemId">Bag character-item id, or 0 when the Field is outside 1..31.</param>
public sealed record FighterItemRow(
    int FieldId,
    string Name,
    int ChrTypeIdx,
    bool IsSelectable,
    int ItemId);

/// <summary>
/// In-memory fighter table built from <c>Config/FighterConfig.xml</c> sheet
/// <c>FighterConfig</c>. Every HorzTable Field column is kept; the plugin never
/// answers with an empty catalog when that sheet has columns.
/// </summary>
public sealed class FighterCatalog
{
    /// <summary>Class-5 character-item window used by <c>sub_F46930</c>.</summary>
    public const int MinPlayableFieldId = 1;

    /// <summary>Inclusive end of the class-5 window.</summary>
    public const int MaxPlayableFieldId = 31;

    /// <summary>Inferred bag key formula: <c>id = type + 300000</c> (<c>sub_EF12E0</c>).</summary>
    public const int CharacterItemBase = 300000;

    /// <summary>Hard cap on HorzTable Field columns (bounded catalog).</summary>
    public const int MaxFieldColumns = 512;

    /// <summary>Creates a catalog. <paramref name="rows"/> must already be bounded and non-empty.</summary>
    /// <param name="source">The client path the workbook was read from.</param>
    /// <param name="rows">Every Field column from the HorzTable.</param>
    public FighterCatalog(string source, IReadOnlyList<FighterItemRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new InvalidOperationException($"Fighter catalog {source} has no Field columns.");
        if (rows.Count > MaxFieldColumns)
            throw new InvalidOperationException(
                $"Fighter catalog {source} has {rows.Count} Field columns; max is {MaxFieldColumns}.");

        Source = source;
        All = rows;
        Playable = rows.Where(r => IsPlayableField(r.FieldId) && r.ItemId != 0).ToArray();
        foreach (var row in rows)
        {
            ByFieldId[row.FieldId] = row;
            if (row.ItemId != 0)
                ByItemId[row.ItemId] = row;
        }

        if (!ByFieldId.TryGetValue(MinPlayableFieldId, out var dao) || dao.ItemId != DefaultItemId)
        {
            throw new InvalidOperationException(
                $"Fighter catalog {source} is missing Field 1 (Dao / item {DefaultItemId}).");
        }

        Default = dao;
    }

    /// <summary>Gets the default login Field (Dao / 1) and its bag item 300001.</summary>
    public static int DefaultItemId => CharacterItemBase + MinPlayableFieldId;

    /// <summary>Gets the client path the workbook was read from.</summary>
    public string Source { get; }

    /// <summary>Gets every HorzTable Field column, including costumes outside 1..31.</summary>
    public IReadOnlyList<FighterItemRow> All { get; }

    /// <summary>Gets Field columns whose ids are in 1..31 and therefore have a 300000-range item.</summary>
    public IReadOnlyList<FighterItemRow> Playable { get; }

    /// <summary>Gets the required Field 1 (Dao) row.</summary>
    public FighterItemRow Default { get; }

    /// <summary>Gets rows keyed by Field id.</summary>
    public Dictionary<int, FighterItemRow> ByFieldId { get; } = new();

    /// <summary>Gets playable rows keyed by bag item id.</summary>
    public Dictionary<int, FighterItemRow> ByItemId { get; } = new();

    /// <summary>Returns whether <paramref name="fieldId"/> is in the class-5 1..31 window.</summary>
    /// <param name="fieldId">HorzTable Field id.</param>
    public static bool IsPlayableField(int fieldId) =>
        fieldId is >= MinPlayableFieldId and <= MaxPlayableFieldId;

    /// <summary>Maps a playable Field id to its bag item. Throws when the Field is outside 1..31.</summary>
    /// <param name="fieldId">HorzTable Field id.</param>
    public static int ItemIdForField(int fieldId)
    {
        if (!IsPlayableField(fieldId))
            throw new InvalidOperationException($"Field {fieldId} is outside the 1..31 character-item window.");
        return CharacterItemBase + fieldId;
    }

    /// <summary>Finds a row by Field id or bag item id.</summary>
    /// <param name="key">Field id or 300000-range item id.</param>
    public FighterItemRow? Find(int key)
    {
        if (ByFieldId.TryGetValue(key, out var byField))
            return byField;
        return ByItemId.TryGetValue(key, out var byItem) ? byItem : null;
    }

    /// <summary>Returns the row for <paramref name="fieldId"/> or throws.</summary>
    /// <param name="fieldId">HorzTable Field id.</param>
    public FighterItemRow RequireField(int fieldId)
    {
        if (!ByFieldId.TryGetValue(fieldId, out var row))
            throw new InvalidOperationException($"Fighter catalog {Source} has no Field {fieldId}.");
        return row;
    }
}
