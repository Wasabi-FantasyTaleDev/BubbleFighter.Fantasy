namespace BubbleFighter.Fantasy.Shop;

/// <summary>Offers an existing workbook item or package while retaining its native grant terms.</summary>
public sealed class ShopSupplementOffer
{
    /// <summary>Gets or sets the client profile identities permitted to use this offer; an empty array permits all versions.</summary>
    public string[] ClientProfiles { get; set; } = [];

    /// <summary>Gets or sets an explicit server sale identity used only when the native item has no Stock rows.</summary>
    public int StockId { get; set; }

    /// <summary>Gets or sets the positive day duration for an explicitly configured server sale.</summary>
    public int DurationDays { get; set; }

    /// <summary>Gets or sets the item identity from the client's ShopItem worksheet.</summary>
    public int ItemId { get; set; }

    /// <summary>Gets or sets a native StockSet identity instead of an individual item identity.</summary>
    public int StockSetId { get; set; }

    /// <summary>Gets or sets whether absent native item, stock, or package definitions are skipped with a load diagnostic.</summary>
    public bool SkipIfMissing { get; set; }

    /// <summary>Gets or sets the price in the configured sale currency.</summary>
    public int Price { get; set; }

    /// <summary>Gets or sets lucci, cash, or client to retain the workbook's native currency.</summary>
    public string MoneyType { get; set; } = "cash";

    /// <summary>Gets or sets an optional operator description that does not alter catalog behavior.</summary>
    public string Note { get; set; } = "";
}
