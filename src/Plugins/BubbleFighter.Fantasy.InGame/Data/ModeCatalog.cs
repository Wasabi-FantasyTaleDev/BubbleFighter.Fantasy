namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// Optional client mode-selector table. When a path is configured the file must exist
/// and list at least one <c>u16</c> id (one per line). Those ids are the values the
/// create-room <c>MapMeta.unknown4</c> slot may carry. An empty or missing file is a
/// load error. When no path is configured the catalog is unused and C2S 402 supplies
/// the mode blob.
/// </summary>
public sealed class ModeCatalog
{
    private readonly HashSet<ushort> _ids;

    /// <summary>Creates an unused catalog (no configured table).</summary>
    public ModeCatalog() => _ids = [];

    /// <summary>Creates a catalog from already-parsed ids.</summary>
    /// <param name="ids">Mode selector values.</param>
    public ModeCatalog(IEnumerable<ushort> ids)
    {
        _ids = [.. ids];
        if (_ids.Count == 0)
            throw new InvalidOperationException("Mode catalog parsed zero rows.");
    }

    /// <summary>Gets whether a table was loaded.</summary>
    public bool HasRows => _ids.Count > 0;

    /// <summary>Gets the loaded selector ids.</summary>
    public IReadOnlyCollection<ushort> Ids => _ids;

    /// <summary>
    /// Loads <paramref name="path"/> when it is non-empty. Missing, empty, or zero-row
    /// files throw. Lines are one unsigned 16-bit id; <c>#</c> starts a comment.
    /// </summary>
    /// <param name="path">Configured table path, or empty to skip.</param>
    public static ModeCatalog Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ModeCatalog();
        if (!File.Exists(path))
            throw new InvalidOperationException($"Mode table '{path}' is missing.");
        var ids = new List<ushort>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (!ushort.TryParse(line, out var id))
                throw new InvalidOperationException($"Mode table '{path}' has a non-u16 row '{line}'.");
            ids.Add(id);
        }

        if (ids.Count == 0)
            throw new InvalidOperationException($"Mode table '{path}' has no mode ids.");
        return new ModeCatalog(ids);
    }

    /// <summary>Requires <paramref name="unknown4"/> to be listed when a table is loaded.</summary>
    /// <param name="unknown4"><c>MapMeta.unknown4</c> from C2S 402.</param>
    public void RequireKnown(ushort unknown4)
    {
        if (!HasRows)
            return;
        if (!_ids.Contains(unknown4))
        {
            throw new InvalidOperationException(
                $"MapMeta.unknown4={unknown4} is not in the loaded mode table ({_ids.Count} row(s)).");
        }
    }
}
