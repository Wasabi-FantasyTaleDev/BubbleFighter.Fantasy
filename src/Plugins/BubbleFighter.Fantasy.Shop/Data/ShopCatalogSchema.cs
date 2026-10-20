namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>Selects the verified column set of a client's ShopItem2 workbook.</summary>
public enum ShopCatalogSchema
{
    /// <summary>The 1256 workbook uses displayName and includes secondary cash-price columns.</summary>
    Kr1256,

    /// <summary>
    /// The shared legacy workbook: the 812 / 717 / 400 / 324 clients ship the same
    /// column set (nameKr display column, prices in sellPrice, no secondary cash
    /// columns) with only per-version column-order and currency variations - the
    /// CN workbooks additionally carry cashCN money types. Verified against the
    /// real Config.bfz of all four clients.
    /// </summary>
    Legacy,
}
