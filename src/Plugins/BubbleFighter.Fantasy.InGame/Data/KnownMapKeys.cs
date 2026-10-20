namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// The map keys the shipped client data exposes, so a room create carrying any
/// other key can be reported rather than silently accepted.
/// </summary>
/// <remarks>
/// <para>
/// Source: <c>BabyWater/Test/Config/GameStageLoadingPage.csv</c>, the client's
/// mode-widget table. Its <c>GameType</c> column binds a
/// <c>wgtFinalZone-rel_*</c> widget to a map key, and those eight keys are the
/// only <c>rel_*</c> strings the shipped data contains - the file is UTF-16LE
/// with a BOM and tab-separated, and the key sits in column 3, not the
/// <c>Ui</c> column that holds the loading-screen path. The training ground's
/// <c>rel_trainingRoom01</c> is a ninth key and comes from the training entry's
/// own map record rather than this file.
/// </para>
/// <para>
/// This set exists for diagnostics, not for validation. The client resolves a
/// key through its own table before it will build a create request at all
/// (<c>sub_9A0880</c> and <c>sub_9A0C70</c> both abort on an
/// unresolved key), so a key outside this set reaching the server means the
/// client resolved it against something it was given elsewhere - which is
/// exactly the case worth logging. Nothing here rejects a create.
/// </para>
/// </remarks>
public static class KnownMapKeys
{
    /// <summary>All eight keys, ascending, as the CSV lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "rel_Desert00_beguy",
        "rel_Forest00_floopy",
        "rel_pirate08_island",
        "rel_space04_64room",
        "rel_Sudden00_Survival",
        "rel_Sudden07_Apartment",
        "rel_Village00_boomhill",
        "rel_zombieSurvival01_Apartment",
    ];

    /// <summary>The training ground's own map key, from the training entry's map record.</summary>
    public const string Training = "rel_trainingRoom01";

    /// <summary>The set form of <see cref="All"/> plus <see cref="Training"/>.</summary>
    public static readonly IReadOnlySet<string> Set = new HashSet<string>(All) { Training };

    /// <summary>Reports whether a key is one the shipped client data binds.</summary>
    /// <param name="key">The map key to test; may be null or empty.</param>
    public static bool Contains(string? key) =>
        !string.IsNullOrEmpty(key) && Set.Contains(key);
}
