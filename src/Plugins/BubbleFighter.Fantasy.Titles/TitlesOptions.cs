using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Titles;

/// <summary>Configures native title data and bounds one build's medal exchange and equipment routes.</summary>
public sealed class TitlesOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether the row accepts title requests; native-client acceptance is required before enabling it.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the connection protocol whose title bindings this row owns.</summary>
    [Required] public string Protocol { get; set; } = "game";

    /// <summary>Gets or sets the canonical inventory and medal catalogue scope.</summary>
    public string BuildKey { get; set; } = "";

    /// <summary>Gets or sets the native title workbook path in the build's client data.</summary>
    [Required] public string TitleCatalogPath { get; set; } = "Config/BF_title.xml";

    /// <summary>Gets or sets the item and stock workbook used to resolve the title's actual ownership grant.</summary>
    [Required] public string ItemCatalogPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the audited native catalogue schema, Legacy or MedalFlag.</summary>
    public string CatalogSchema { get; set; } = "";

    /// <summary>Gets or sets the largest workbook accepted before XML parsing.</summary>
    [Range(1024, 134217728)] public int MaxWorkbookBytes { get; set; } = 134217728;

    /// <summary>Gets or sets the combined native worksheet row limit.</summary>
    [Range(1, 65536)] public int MaxCatalogRows { get; set; } = 65536;

    /// <summary>Gets or sets the largest sparse worksheet column index accepted.</summary>
    [Range(4, 1024)] public int MaxCatalogColumns { get; set; } = 256;

    /// <summary>Gets or sets the largest text cell retained while loading native catalogues.</summary>
    [Range(1, 65536)] public int MaxCellCharacters { get; set; } = 16384;

    /// <summary>Gets or sets the maximum accounts with an in-flight title operation and ordered publication.</summary>
    [Range(1, 65536)] public int MaxInFlightAccounts { get; set; } = 128;

    /// <summary>Gets or sets whether accepted terminal matches produce configured catalogue medals.</summary>
    public bool RoundMedalsEnabled { get; set; }

    /// <summary>Gets or sets explicit medal thresholds and counts; an empty policy awards no medals.</summary>
    public TitleRoundMedalRule[] RoundMedalRules { get; set; } = [];

    /// <summary>Gets or sets the maximum configured round medal rules retained by this row.</summary>
    [Range(1, int.MaxValue)] public int MaxRoundMedalRules { get; set; } = 256;

    /// <summary>Gets or sets the maximum explicit mode identities retained by one round medal rule.</summary>
    [Range(1, int.MaxValue)] public int MaxModesPerRoundMedalRule { get; set; } = 128;

    /// <summary>Gets or sets the explicit credit count for a native medal-bonus reference; absent leaves those bonus claims closed.</summary>
    [Range(1, int.MaxValue)] public int? BonusMedalQuantity { get; set; }

    /// <summary>
    /// Gets or sets whether the Favorite challenge titles are granted automatically when enough weapons reach Master and their
    /// progress is shown; needs the build's Favorite weapon progress and its progress packet. Off by default.
    /// </summary>
    public bool FavoriteTitles { get; set; }

    /// <summary>Gets or sets the timezone for native annual title acquisition windows; empty leaves timed claims closed.</summary>
    public string AcquisitionTimeZone { get; set; } = "";

    /// <summary>Rejects an enabled row without an explicit catalogue identity and schema.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Enabled) yield break;
        if (string.IsNullOrWhiteSpace(BuildKey)) yield return new("titles requires build_key.");
        if (CatalogSchema is not ("Legacy" or "MedalFlag"))
            yield return new("titles requires an audited Legacy or MedalFlag catalog_schema.");
        if (MaxRoundMedalRules <= 0 || MaxModesPerRoundMedalRule <= 0)
            yield return new("Round medal resource budgets must be positive.");
        if (RoundMedalRules is null || RoundMedalRules.Length > MaxRoundMedalRules
            || RoundMedalRules.Any(rule => rule is null || rule.MedalId <= 0 || rule.Count < 1
                || rule.ModeIds is null || rule.ModeIds.Length == 0 || rule.ModeIds.Length > MaxModesPerRoundMedalRule
                || rule.ModeIds.Any(mode => mode < 0)
                || rule.ModeIds.Distinct().Count() != rule.ModeIds.Length || rule.Minimum < 0
                || rule.Maximum < rule.Minimum || rule.MinimumParticipants < 1
                || rule.Metric is not ("Completed" or "Wins" or "Kills" or "Knockdowns" or "Rescues" or "Score" or "Deaths" or "Stars"))
            || RoundMedalRules.Select(rule => rule.MedalId).Distinct().Count() != RoundMedalRules.Length)
            yield return new("round_medal_rules requires distinct native medals and explicit terminal-counter rules within the configured resource budgets.");
        if (RoundMedalsEnabled && RoundMedalRules is { Length: 0 })
            yield return new("round_medals_enabled requires an explicit nonempty round_medal_rules policy.");
        if (AcquisitionTimeZone is null) yield return new("acquisition_time_zone must be an explicit string or empty.");
        else if (AcquisitionTimeZone.Length != 0)
        {
            string? error = null;
            try { _ = TimeZones.Resolve(AcquisitionTimeZone); }
            catch (TimeZoneNotFoundException) { error = "acquisition_time_zone is not installed on this host."; }
            catch (InvalidTimeZoneException) { error = "acquisition_time_zone has invalid timezone data."; }
            if (error is not null) yield return new(error);
        }

    }
}
