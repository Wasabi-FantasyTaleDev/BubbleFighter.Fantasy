namespace BubbleFighter.Fantasy.Shop;

/// <summary>
/// Gifts for one attained level, resolved from the native guide or an explicit <c>shop.level_rewards</c> entry.
/// </summary>
public sealed class ShopLevelReward
{
    /// <summary>Gets or sets the attained level eligible for this once-only reward; at least 2.</summary>
    public int Level { get; set; }

    /// <summary>
    /// Gets or sets the Stock or StockSet keys of the build's shop workbook, one gift each. The amount or term a gift
    /// carries is the one its stock row sells, so a larger quantity is another stock key.
    /// </summary>
    public int[] StockKeys { get; set; } = [];
}
