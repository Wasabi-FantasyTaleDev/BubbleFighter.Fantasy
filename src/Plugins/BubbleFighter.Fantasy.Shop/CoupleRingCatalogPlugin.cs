using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Shop.Data;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>Bounds the selected client's couple invitation and display-ring definitions.</summary>
public sealed class CoupleRingCatalogOptions
{
    /// <summary>Gets or sets the native workbook path resolved by the client-data service.</summary>
    [Required, MinLength(1)]
    public string ShopItemClientPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the inclusive workbook byte limit.</summary>
    [Range(1, 268435456)]
    public long MaxShopItemBytes { get; set; } = 134217728;

    /// <summary>Gets or sets the maximum invitation-ring definitions retained for this build.</summary>
    [Range(1, 65535)]
    public int MaxCoupleRings { get; set; } = 256;

    /// <summary>Gets or sets native stock identities known to be absent in this client's workbook; their display ring remains zero.</summary>
    [MaxLength(256)]
    public int[] AllowedMissingDisplayStockIds { get; set; } = [];
}

/// <summary>Publishes couple-ring definitions independently of shop purchases or lobby persistence.</summary>
[Plugin("inventory.couple-catalog", Category = "items", Names = ["en=Couple ring catalog", "zh-Hans=情侣戒指目录", "zh-Hant=情侶戒指目錄", "ko=커플링 목록"],
    Description = "Native invitation items and their display rings.", Requires = [typeof(IClientDataStore)], Provides = [typeof(ICoupleRingCatalog)])]
public sealed class CoupleRingCatalogPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<CoupleRingCatalogOptions>();
        var workbook = SpreadsheetWorkbook.Load(WorkbookFile.FromStore(context.Services.Get<IClientDataStore>(), options.ShopItemClientPath),
            options.MaxShopItemBytes, ShopCatalogLoader.LimitKey, ["ShopItem", "Stock"]);
        var catalog = CoupleRingCatalog.Parse(workbook["ShopItem"], workbook["Stock"], options.MaxCoupleRings,
            options.AllowedMissingDisplayStockIds);
        context.Effect(context.Services.Register<ICoupleRingCatalog>(catalog));
        return ValueTask.CompletedTask;
    }
}

internal sealed class CoupleRingCatalog(IReadOnlyDictionary<int, int> rings) : ICoupleRingCatalog
{
    public bool TryGetDisplayRing(int invitationItemId, out int displayRingItemId) => rings.TryGetValue(invitationItemId, out displayRingItemId);

    internal static CoupleRingCatalog Parse(IReadOnlyList<IReadOnlyList<string>> items, IReadOnlyList<IReadOnlyList<string>> stocks,
        int maximum, IReadOnlyCollection<int>? allowedMissingDisplayStockIds = null)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var missing = (allowedMissingDisplayStockIds ?? []).ToHashSet();
        if (missing.Count > 256 || missing.Any(id => id <= 0) || missing.Count != (allowedMissingDisplayStockIds?.Count ?? 0))
            throw new InvalidOperationException("Allowed missing couple display stocks must be unique positive identities, at most 256.");
        static int Column(IReadOnlyList<IReadOnlyList<string>> rows, string sheet, string name)
        {
            if (rows.Count == 0) throw new InvalidOperationException($"Couple definitions have no {sheet} header.");
            var indices = rows[0].Select((value, index) => (value, index)).Where(cell => cell.value.Trim() == name).ToArray();
            return indices.Length == 1 ? indices[0].index
                : throw new InvalidOperationException($"Couple definitions require exactly one {sheet}.{name} column.");
        }
        static string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column].Trim() : "";
        static int Positive(string value, string field) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : throw new InvalidOperationException($"Couple definitions contain an invalid {field}.");
        var itemId = Column(items, "ShopItem", "itemId");
        var function = Column(items, "ShopItem", "function");
        var parameter = Column(items, "ShopItem", "parameter");
        var stockId = Column(stocks, "Stock", "stockId");
        var stockItemId = Column(stocks, "Stock", "itemId");
        var invitations = new Dictionary<int, int>();
        var nativeItems = new HashSet<int>();
        foreach (var row in items.Skip(1))
        {
            var idText = Cell(row, itemId);
            if (idText.Length == 0 || idText.StartsWith('#')) continue;
            if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var knownId) && knownId > 0)
                nativeItems.Add(knownId);
            // kr-1256 sub_D407C0 selects function 17; sub_E807F0 resolves ShopItem.parameter through Stock.
            // kr-812 sub_981BA0 selects function 17; sub_9F93F0 resolves its parameter at +300 through the stock tree.
            if (!int.TryParse(Cell(row, function), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind) || kind != 17) continue;
            var id = Positive(idText, "invitation item identity");
            if (invitations.Count >= maximum) throw new InvalidOperationException("Couple definitions exceed max_couple_rings.");
            if (!invitations.TryAdd(id, Positive(Cell(row, parameter), "invitation stock identity")))
                throw new InvalidOperationException($"Couple definitions repeat invitation item {id}.");
        }
        var requestedStocks = invitations.Values.ToHashSet();
        var displayByStock = new Dictionary<int, int>();
        foreach (var row in stocks.Skip(1))
        {
            if (!int.TryParse(Cell(row, stockId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || !requestedStocks.Contains(id)) continue;
            if (!displayByStock.TryAdd(id, Positive(Cell(row, stockItemId), "display ring item identity")))
                throw new InvalidOperationException($"Couple definitions repeat referenced stock {id}.");
        }
        var rings = new Dictionary<int, int>();
        foreach (var invitation in invitations)
        {
            if (!displayByStock.TryGetValue(invitation.Value, out var display))
            {
                // id-400 sub_97A6F0 / cn-408 sub_9B9800 select function 17 without a stock lookup;
                // id-400 sub_805C10 / cn-408 sub_83A950 guard the profile's absent display-ring lookup.
                if (!missing.Contains(invitation.Value))
                    throw new InvalidOperationException($"Couple invitation {invitation.Key} references missing stock {invitation.Value}.");
                rings.Add(invitation.Key, 0);
                continue;
            }
            if (!nativeItems.Contains(display))
                throw new InvalidOperationException($"Couple invitation {invitation.Key} references display ring {display} outside this build's item catalog.");
            rings.Add(invitation.Key, display);
        }
        return new(rings);
    }
}
