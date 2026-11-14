using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository
{
    private static ItemModifications? DecodeModifications(string? json)
    {
        if (json is null) return null;
        var modifications = JsonSerializer.Deserialize<ItemModifications>(json)
            ?? throw new InvalidDataException("Stored equipment modifications cannot be JSON null.");
        modifications.Validate();
        return modifications;
    }

    private static string? EncodeModifications(ItemModifications? modifications)
    {
        if (modifications is null) return null;
        modifications.Validate();
        return JsonSerializer.Serialize(modifications);
    }
}
