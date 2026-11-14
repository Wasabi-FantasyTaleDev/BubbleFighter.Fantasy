using System.Diagnostics.CodeAnalysis;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// The gifts of one workbook by Stock or StockSet id. Every Stock with an item is a gift of itself, so its gift is
/// built from the Stock when it is looked up instead of stored: a workbook has thousands of Stocks, and storing a
/// gift and a one-element list for each was a large share of an idle server's heap. StockSet gifts are stored; a
/// Stock id wins over a StockSet with the same id.
/// </summary>
internal sealed class ShopGiftIndex : IReadOnlyDictionary<int, ShopGift>
{
    private readonly IReadOnlyDictionary<int, ShopStockRow> _stocks;
    private readonly Dictionary<int, ShopGift> _sets;

    /// <summary>Creates the index over a workbook's Stocks and its resolved StockSet gifts.</summary>
    /// <param name="stocks">Every Stock by id, including those without an item, which are not gifts.</param>
    /// <param name="sets">The StockSet gifts whose ids are not Stock gifts.</param>
    public ShopGiftIndex(IReadOnlyDictionary<int, ShopStockRow> stocks, Dictionary<int, ShopGift> sets)
    {
        _stocks = stocks;
        _sets = sets;
        Count = sets.Count + stocks.Values.Count(stock => stock.ItemId > 0);
    }

    /// <summary>Gets whether a Stock id names a gift: the Stock exists and grants an item.</summary>
    /// <param name="key">The Stock id.</param>
    /// <param name="stock">The Stock when it is a gift.</param>
    /// <returns><see langword="true"/> when the Stock is a gift.</returns>
    public bool IsStockGift(int key, [MaybeNullWhen(false)] out ShopStockRow stock) =>
        _stocks.TryGetValue(key, out stock) && stock.ItemId > 0;

    /// <inheritdoc />
    public ShopGift this[int key] => TryGetValue(key, out var gift) ? gift : throw new KeyNotFoundException($"No shop gift {key}.");

    /// <inheritdoc />
    public IEnumerable<int> Keys => this.Select(pair => pair.Key);

    /// <inheritdoc />
    public IEnumerable<ShopGift> Values => this.Select(pair => pair.Value);

    /// <inheritdoc />
    public int Count { get; }

    /// <inheritdoc />
    public bool ContainsKey(int key) => IsStockGift(key, out _) || _sets.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(int key, [MaybeNullWhen(false)] out ShopGift value)
    {
        if (IsStockGift(key, out var stock))
        {
            value = new ShopGift(stock.StockId, 0, 0, [stock]);
            return true;
        }
        return _sets.TryGetValue(key, out value);
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<int, ShopGift>> GetEnumerator()
    {
        foreach (var stock in _stocks.Values)
            if (stock.ItemId > 0)
                yield return new(stock.StockId, new ShopGift(stock.StockId, 0, 0, [stock]));
        foreach (var pair in _sets)
            yield return pair;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
