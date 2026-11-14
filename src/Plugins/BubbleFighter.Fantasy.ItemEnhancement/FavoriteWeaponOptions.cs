using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>
/// Configures the Favorite weapon mastery points that successful enhancements add and that the item presentation shows. The grade
/// thresholds and award values come only from the client's own workbook; no economy value is configured here.
/// </summary>
public sealed class FavoriteWeaponOptions
{
    /// <summary>Gets or sets whether Favorite points are awarded and published; false leaves the feature absent.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the native workbook holding the Base and Upgrade sheets.</summary>
    public string WorkbookPath { get; set; } = "Config/FavoriteWeaponSystem.xml";

    /// <summary>Validates the workbook path of an enabled feature.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(WorkbookPath)) yield return new("favorite.workbook_path is required when favorite is enabled.");
    }
}