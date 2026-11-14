using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.Pets.Data;

/// <summary>Reads the installed feeding duration from the client's bounded PetConfig constant worksheet.</summary>
internal static class PetNativeConfiguration
{
    internal static int ReadFeedCooldownSeconds(IClientDataStore store, string path, int maxBytes,
        int maxRows, int maxColumns, int maxCellCharacters)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColumns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCellCharacters, 1);
        var bytes = store.Read(path, maxBytes);
        if (bytes.Length == 0 || bytes.Length > maxBytes)
            throw new InvalidDataException($"Native PetConfig '{path}' is empty or exceeds max_workbook_bytes.");
        // tw-454 sub_DCDBB0 selects ConstValue; tw-454 sub_DCDD40 reads its named PetSystem JSON object.
        var rows = PetWorkbook.Read(bytes, path, maxRows, maxColumns, maxCellCharacters, ["ConstValue"])["ConstValue"];
        if (rows.Count == 0) throw new InvalidDataException($"Native PetConfig '{path}' has no ConstValue header.");
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < rows[0].Count; index++)
        {
            var name = rows[0][index].Trim();
            if (name.Length != 0 && !columns.TryAdd(name, index))
                throw new InvalidDataException($"Native PetConfig '{path}' repeats column '{name}'.");
        }
        if (!columns.TryGetValue("ConstName", out var nameColumn) || !columns.TryGetValue("ConstValue", out var valueColumn))
            throw new InvalidDataException($"Native PetConfig '{path}' requires ConstName and ConstValue columns.");
        string? json = null;
        foreach (var row in rows.Skip(1))
        {
            if (nameColumn >= row.Count || row[nameColumn].Trim() != "PetSystem") continue;
            if (json is not null) throw new InvalidDataException($"Native PetConfig '{path}' repeats PetSystem.");
            json = valueColumn < row.Count ? row[valueColumn] : string.Empty;
        }
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"Native PetConfig '{path}' requires a nonempty PetSystem value.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Native PetConfig '{path}' PetSystem must be a flat JSON object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            int? minutes = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Native PetConfig '{path}' repeats PetSystem property '{property.Name}'.");
                if (property.Name != "FeedCoolTime") continue;
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value)
                    || value < 0 || value > PetsOptions.MaximumFeedCooldownSeconds / 60)
                    throw new InvalidDataException($"Native PetConfig '{path}' FeedCoolTime must be nonnegative integer minutes within the server cooldown bound.");
                minutes = value;
            }
            if (minutes is null) throw new InvalidDataException($"Native PetConfig '{path}' has no FeedCoolTime.");
            // tw-454 sub_DCDD40 divides minutes by 1440.0f; tw-454 sub_DD2440 adds the resulting OA days.
            return checked(minutes.Value * 60);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Native PetConfig '{path}' has malformed PetSystem JSON.", exception);
        }
    }
}
