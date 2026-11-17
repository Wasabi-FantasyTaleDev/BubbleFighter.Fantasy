using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>Where the creation-card row reads the client's item table and how much of it it keeps.</summary>
public sealed class ClanCreationCardsOptions
{
    /// <summary>Gets or sets the client path of the item workbook whose ShopItem sheet lists the cards.</summary>
    [Required, MinLength(1)]
    public string ShopItemClientPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the inclusive byte limit of that workbook.</summary>
    [Range(1, 268435456)]
    public long MaxShopItemBytes { get; set; } = 134217728;

    /// <summary>Gets or sets how many creation cards the row keeps at most.</summary>
    [Range(1, 4096)]
    public int MaxCards { get; set; } = 64;

    /// <summary>
    /// Gets or sets the member capacity of a creation card whose ShopItem row has no positive parameter, or 0 to treat
    /// such a card as a table error. kr-1256 names its cards for 30 and 10 members and states that number as the
    /// parameter; kr-812's single card 100819 is a plain one-use ticket, so its capacity is the server's choice.
    /// </summary>
    [Range(0, 65535)]
    public int CapacityWithoutParameter { get; set; }
}

/// <summary>Publishes the build's clan creation cards, read from the client's own item table.</summary>
[Plugin("clan.creation-cards", Category = "social", Names = ["en=Clan creation cards", "zh-Hans=公会创建卡", "zh-Hant=公會建立卡", "ko=클랜 생성권"],
    Description = "The items that found a clan and the member capacity each gives, from the client's ShopItem table.",
    Requires = [typeof(IClientDataStore)], Provides = [typeof(IClanCreationCards)])]
public sealed class ClanCreationCardsPlugin : IPlugin
{
    /// <summary>The ShopItem function of a clan creation card: the item kind kr-1256 sub_51BA40 opens the create dialog for.</summary>
    internal const int CreationCardFunction = 22;

    private const string LimitKey = "max_shop_item_bytes";

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<ClanCreationCardsOptions>();
        var workbook = WorkbookFile.FromStore(context.Services.Get<IClientDataStore>(), options.ShopItemClientPath);
        var rows = SpreadsheetWorkbook.Load(workbook, options.MaxShopItemBytes, LimitKey, ["ShopItem"])["ShopItem"];
        context.Effect(context.Services.Register<IClanCreationCards>(Parse(rows, options.MaxCards, options.CapacityWithoutParameter)));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads the cards from the ShopItem sheet: every row of function 22, whose parameter is the capacity
    /// (in kr-1256's table the cards 100819 and 101979, named for 30 and 10 members, carry 30 and 10). A row without a
    /// positive parameter takes <paramref name="capacityWithoutParameter"/>, and is an error when that is 0.
    /// </summary>
    internal static IClanCreationCards Parse(IReadOnlyList<IReadOnlyList<string>> rows, int maximum, int capacityWithoutParameter = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityWithoutParameter);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        if (rows.Count == 0) throw new InvalidOperationException("Clan creation cards: the ShopItem sheet has no header.");
        int Column(string name)
        {
            var indices = rows[0].Select((value, index) => (value, index)).Where(cell => cell.value.Trim() == name).ToArray();
            return indices.Length == 1 ? indices[0].index
                : throw new InvalidOperationException($"Clan creation cards: the ShopItem sheet needs exactly one {name} column.");
        }
        var itemColumn = Column("itemId");
        var functionColumn = Column("function");
        var parameterColumn = Column("parameter");
        string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column].Trim() : "";
        var cards = new Dictionary<int, int>();
        foreach (var row in rows.Skip(1))
        {
            var idText = Cell(row, itemColumn);
            if (idText.Length == 0 || idText.StartsWith('#')) continue;
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 1)
                throw new InvalidOperationException($"Clan creation cards: invalid item id '{idText}'.");
            if (!int.TryParse(Cell(row, functionColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out var function)
                || function != CreationCardFunction)
                continue;
            if (!int.TryParse(Cell(row, parameterColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity) || capacity < 1)
                capacity = capacityWithoutParameter > 0 ? capacityWithoutParameter
                    : throw new InvalidOperationException(
                        $"Clan creation card {id} has no positive capacity parameter; set capacity_without_parameter for a build whose card states none.");
            if (cards.Count >= maximum) throw new InvalidOperationException("Clan creation cards exceed max_cards.");
            if (!cards.TryAdd(id, capacity)) throw new InvalidOperationException($"Clan creation cards repeat item {id}.");
        }
        return new Cards(cards);
    }

    private sealed class Cards(IReadOnlyDictionary<int, int> capacities) : IClanCreationCards
    {
        public bool TryGetCapacity(int itemId, out int capacity) => capacities.TryGetValue(itemId, out capacity);
    }
}
