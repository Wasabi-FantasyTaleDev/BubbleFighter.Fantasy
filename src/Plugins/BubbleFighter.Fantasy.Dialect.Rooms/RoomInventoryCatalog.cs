using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Validates native spec selectors against this client's own workbooks, without treating them as bag item IDs.</summary>
internal sealed class RoomInventoryCatalog(HashSet<int> characters, IReadOnlyList<HashSet<int>> weapons)
{
    internal static RoomInventoryCatalog Load(IClientDataStore data)
    {
        var fighters = Sheets(data, "Config/FighterConfig.xml", ["FighterConfig"])["FighterConfig"];
        var fields = fighters.SingleOrDefault(row => row.Count > 0 && row[0] == "[Field]")
            ?? throw new ConfigException("Room inventory selection requires FighterConfig field identities.");
        var characters = Ids(fields.Skip(2));
        var sheets = Sheets(data, "Config/WeaponSpecDetail.xml",
            ["Pistol", "Rifle", "Shotgun", "SniperRifle", "Near", "Grenade"]);
        HashSet<int> WeaponIds(string sheet) => Ids((sheets[sheet].SingleOrDefault(row => row.Count > 0 && row[0] == "id")
            ?? throw new ConfigException($"Room inventory selection requires the {sheet} weapon identities.")).Skip(2));
        var primary = WeaponIds("Rifle");
        primary.UnionWith(WeaponIds("Shotgun"));
        primary.UnionWith(WeaponIds("SniperRifle"));
        return new(characters, [primary, WeaponIds("Pistol"), WeaponIds("Near"), WeaponIds("Grenade")]);
    }

    internal void Validate(RoomInventorySelection selection)
    {
        // cn-279 sub_881D40 and cn-324 sub_598700 copy the four dialog choices in this order;
        // cn-324 copies one additional existing seat short without editing it in the dialog.
        if (selection.CharacterSelection != 0 && !characters.Contains(selection.CharacterSelection))
            throw new ArgumentException("The room character selector does not exist in this client's FighterConfig.");
        for (var index = 0; index < 4; index++)
            if (!weapons[index].Contains(selection.WeaponSelections[index]))
                throw new ArgumentException($"The room weapon selector at position {index} does not exist in this client's weapon family.");
        if (selection.WeaponSelections.Count == 5 && selection.WeaponSelections[4] is not (-1 or 0))
            throw new ArgumentException("The unaudited trailing room selector must retain its empty value.");
    }

    private static HashSet<int> Ids(IEnumerable<string> cells) => cells
        .Select(cell => int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
        .Where(id => id is > 0 and <= short.MaxValue).ToHashSet();

    private static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Sheets(
        IClientDataStore data, string path, string[] names)
    {
        var bytes = data.Read(path);
        // A fixed parse bound prevents oversized native configuration input from expanding without limit.
        const int maxWorkbookBytes = 64 * 1024 * 1024;
        return SpreadsheetWorkbook.Load(new WorkbookFile(path, bytes.Length,
            () => new MemoryStream(bytes, writable: false)), maxWorkbookBytes, "native room selection workbook size bound", names);
    }
}
