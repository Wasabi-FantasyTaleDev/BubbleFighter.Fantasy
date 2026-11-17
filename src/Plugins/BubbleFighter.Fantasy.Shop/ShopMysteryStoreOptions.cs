using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>
/// The Mysterystore of a shop row: which owned item pays, which Stocks or StockSets can appear and what they cost.
/// The client shows six cards and reads every price and the refresh cost from the reply, so these are the server's
/// own economy, not values recovered from the client.
/// </summary>
public sealed class ShopMysteryStoreOptions
{
    /// <summary>
    /// Gets or sets the inventory item whose owned count pays for the store (kr-1256 sub_E61DC0 reads that count); zero
    /// leaves the store unmounted on this row.
    /// </summary>
    public int CoinItemId { get; set; }

    /// <summary>
    /// Gets or sets the Stocks or StockSets the cards are drawn from, as single keys (<c>"2001494"</c>) or inclusive
    /// ranges (<c>"2001494-2001580"</c>). Every key must exist in the build's workbook, grant items only, and be one
    /// of the build's supported items.
    /// </summary>
    public string[] Offers { get; set; } = [];

    /// <summary>Gets or sets the coin price of every card; the client draws prices as signed 16-bit numbers.</summary>
    public ushort OfferCoinPrice { get; set; } = 100;

    /// <summary>Gets or sets the coins one refresh of the cards costs.</summary>
    public ushort RefreshCoinCost { get; set; } = 10;

    /// <summary>
    /// Gets or sets how long one selection of cards lasts before it is replaced free of charge, in seconds; the client
    /// counts down from a 16-bit value.
    /// </summary>
    public ushort RotationSeconds { get; set; } = 21600;

    /// <summary>Gets or sets whether the client groups Stock variants of one item into one card.</summary>
    public bool GroupStockVariants { get; set; }

    /// <summary>Checks the settings and expands the offer keys in their configured order.</summary>
    /// <returns>The distinct Stock or StockSet keys.</returns>
    /// <exception cref="ConfigException">A value is out of range or a key is malformed or repeated.</exception>
    internal IReadOnlyList<int> Validate()
    {
        if (CoinItemId <= 0)
            throw new ConfigException("shop.mystery_store.coin_item_id must name an inventory item.");
        if (OfferCoinPrice > short.MaxValue || RefreshCoinCost > short.MaxValue)
            throw new ConfigException("shop.mystery_store prices must fit the client's signed 16-bit display (0-32767).");
        if (RotationSeconds == 0)
            throw new ConfigException("shop.mystery_store.rotation_seconds must be positive.");
        var keys = new List<int>();
        foreach (var text in Offers ?? throw new ConfigException("shop.mystery_store.offers is null."))
        {
            var parts = (text ?? "").Split('-');
            if (parts.Length is < 1 or > 2 || !parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var key) && key > 0))
                throw new ConfigException($"shop.mystery_store.offers entry '{text}' is not a key or an inclusive key range.");
            var first = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var last = int.Parse(parts[^1], CultureInfo.InvariantCulture);
            if (last < first)
                throw new ConfigException($"shop.mystery_store.offers range '{text}' ends before it starts.");
            if ((long)last - first + 1 > 4096 - keys.Count)
                throw new ConfigException("shop.mystery_store.offers exceeds the 4096 key limit.");
            for (var key = (long)first; key <= last; key++) keys.Add((int)key);
        }
        // The client lays out six cards (kr-1256 sub_E33390), and a selection needs six distinct ones.
        if (keys.Count < 6)
            throw new ConfigException("shop.mystery_store.offers needs at least six Stocks or StockSets for the six cards.");
        if (keys.Distinct().Count() != keys.Count)
            throw new ConfigException("shop.mystery_store.offers names a key twice.");
        return keys;
    }
}
