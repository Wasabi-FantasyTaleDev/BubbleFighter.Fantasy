using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Shop.Data;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>Bounds the native workbook and spray lookup published by the paint-catalog row.</summary>
public sealed class PaintItemCatalogOptions
{
    /// <summary>Gets or sets the native workbook path resolved by the client-data service.</summary>
    [Required, MinLength(1)]
    public string ShopItemClientPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the inclusive native workbook byte limit.</summary>
    [Range(1, 268435456)]
    public long MaxShopItemBytes { get; set; } = 134217728;

    /// <summary>Gets or sets the maximum spray definitions retained by this build's lookup.</summary>
    [Range(1, 65535)]
    public int MaxPaintItems { get; set; } = 256;
}

/// <summary>Publishes fixed spray colors independently of shop opens or purchase persistence.</summary>
[Plugin("inventory.paint-catalog", Description = "Native fixed-color spray definitions for shared inventory use.",
    Requires = [typeof(IClientDataStore)], Provides = [typeof(IPaintItemCatalog)])]
public sealed class PaintItemCatalogPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<PaintItemCatalogOptions>();
        var data = context.Services.Get<IClientDataStore>();
        var workbook = WorkbookFile.FromBytes(options.ShopItemClientPath, data.Read(options.ShopItemClientPath));
        var rows = SpreadsheetWorkbook.Load(workbook, options.MaxShopItemBytes, ["ShopItem"])["ShopItem"];
        var catalog = PaintItemCatalog.Parse(rows, options.MaxPaintItems);
        context.Effect(context.Services.Register<IPaintItemCatalog>(catalog));
        return ValueTask.CompletedTask;
    }
}

internal sealed class PaintItemCatalog(IReadOnlyDictionary<int, ushort> colors) : IPaintItemCatalog
{
    public bool TryGetColorIndex(int itemId, out ushort colorIndex) => colors.TryGetValue(itemId, out colorIndex);

    internal static PaintItemCatalog Parse(IReadOnlyList<IReadOnlyList<string>> rows, int maximum)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (rows.Count == 0) throw new InvalidOperationException("Spray definitions have no ShopItem header.");
        int Column(string name)
        {
            var indices = rows[0].Select((value, index) => (value, index)).Where(cell => cell.value.Trim() == name).ToArray();
            return indices.Length == 1 ? indices[0].index : throw new InvalidOperationException($"Spray definitions require exactly one {name} column.");
        }
        var itemColumn = Column("itemId");
        var functionColumn = Column("function");
        var parameterColumn = Column("parameter");
        var subCategoryColumn = Column("subCategory");
        string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column].Trim() : "";
        var colors = new Dictionary<int, ushort>();
        foreach (var row in rows.Skip(1))
        {
            var idText = Cell(row, itemColumn);
            if (idText.Length == 0 || idText.StartsWith('#')) continue;
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 1)
                throw new InvalidOperationException("Spray definitions contain an invalid item identity.");
            if (!int.TryParse(Cell(row, functionColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out var function))
                throw new InvalidOperationException($"Item {id} has an invalid native function.");
            // kr-812 sub_9A04D0 loads function at +292 and parameter at +300; sub_981BA0 uses function 1 for spray.
            // Modern ShopItem2 has unrelated function-1 rows (for example an All_Etc item).
            // The client enters the spray branch only for the native Spray sub-category.
            if (function != 1 || !string.Equals(Cell(row, subCategoryColumn), "Spray", StringComparison.Ordinal)) continue;
            if (!ushort.TryParse(Cell(row, parameterColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out var color) || color == 0)
                throw new InvalidOperationException($"Spray {id} has an invalid color index.");
            if (colors.Count >= maximum) throw new InvalidOperationException("Spray definitions exceed max_paint_items.");
            if (!colors.TryAdd(id, color)) throw new InvalidOperationException($"Spray definitions repeat item {id}.");
        }
        return new(colors);
    }
}
