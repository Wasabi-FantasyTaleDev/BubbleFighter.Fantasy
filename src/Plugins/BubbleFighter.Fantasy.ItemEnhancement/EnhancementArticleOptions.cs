using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Configures native equipment metadata independently of the policy for spending enhancement materials.</summary>
public class EnhancementArticleOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether this row resolves and presents persisted native equipment Articles.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets whether metadata preparation allocates Article identities for eligible ordinary equipment; existing identities remain usable when false.</summary>
    public bool InitializeArticles { get; set; }

    /// <summary>Gets or sets the game connection scope served by this row.</summary>
    [Required] public string Protocol { get; set; } = "game";

    /// <summary>Gets or sets the canonical inventory catalog and equipment loadout key.</summary>
    public string BuildKey { get; set; } = "";

    /// <summary>Gets or sets the native workbook path used to validate item and attribute identities.</summary>
    [Required] public string CatalogPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the maximum workbook bytes accepted before parsing.</summary>
    [Range(1024, 134217728)] public int MaxWorkbookBytes { get; set; } = 134217728;

    /// <summary>Gets or sets the combined row limit of the selected native worksheets.</summary>
    [Range(1, 65536)] public int MaxCatalogRows { get; set; } = 65536;

    /// <summary>Gets or sets the largest nonempty sparse column accepted by the workbook reader.</summary>
    [Range(4, 1024)] public int MaxCatalogColumns { get; set; } = 256;

    /// <summary>Gets or sets the largest text value retained from one native cell.</summary>
    [Range(1, 65536)] public int MaxCellCharacters { get; set; } = 16384;

    /// <summary>Gets or sets the native ItemAttr selector for weapon enhancement.</summary>
    [Range(0, int.MaxValue)] public int WeaponAttributeId { get; set; }

    /// <summary>Gets or sets the native RandomTable definition referenced by the weapon selector.</summary>
    [Range(0, int.MaxValue)] public int WeaponBuffId { get; set; }

    /// <summary>Gets or sets the native ItemAttr selector for clothing enhancement.</summary>
    [Range(0, int.MaxValue)] public int ClothingAttributeId { get; set; }

    /// <summary>Gets or sets the native RandomTable definition referenced by the clothing selector.</summary>
    [Range(0, int.MaxValue)] public int ClothingBuffId { get; set; }

    /// <summary>Gets or sets the Favorite weapon mastery points shown by this row and, on the enhancement row, awarded by it.</summary>
    public FavoriteWeaponOptions Favorite { get; set; } = new();

    /// <summary>Requires the catalog scope and distinct native presence selectors before preparing equipment metadata.</summary>
    public virtual IEnumerable<ValidationResult> Validate()
    {
        if (InitializeArticles && !Enabled)
            yield return new("initialize_articles requires the equipment Article row to be enabled.");
        if (Favorite is null) { yield return new("favorite configuration cannot be null."); yield break; }
        if (Favorite.Enabled && !Enabled) yield return new("Favorite requires the owning row to be enabled.");
        foreach (var result in Favorite.Validate()) yield return result;
        if (!Enabled) yield break;
        if (string.IsNullOrWhiteSpace(BuildKey)) yield return new("build_key is required for equipment Articles.");
        if (WeaponAttributeId <= 0 || WeaponBuffId <= 0 || ClothingAttributeId <= 0 || ClothingBuffId <= 0)
            yield return new("Enabled equipment Articles require positive native attribute and buff identities.");
        if (WeaponAttributeId == ClothingAttributeId || WeaponBuffId == ClothingBuffId)
            yield return new("Weapon and clothing enhancement definitions must be distinct.");
    }
}
