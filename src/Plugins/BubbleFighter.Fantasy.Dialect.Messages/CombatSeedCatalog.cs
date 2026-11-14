using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Pets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Resolves ordinary critical assignment and cancellation accumulation independently of weapon resources.</summary>
internal sealed class CombatSeedCatalog(
    IReadOnlyDictionary<int, PetCombatEffect[]> staticEffects, PetSeedEffects? pets = null)
{
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    internal static CombatSeedCatalog Load(IClientDataStore data, string path, int maxBytes,
        bool critical, bool cancellation, PetSeedEffects? pets)
    {
        IReadOnlyList<IReadOnlyList<string>> rows;
        try
        {
            rows = SpreadsheetWorkbook.Load(WorkbookFile.FromStore(data, path, maxBytes), maxBytes,
                "combat_workbook_max_bytes", ["ShopItemSpec"])["ShopItemSpec"];
        }
        catch (Exception failure) when (failure is FileNotFoundException or InvalidDataException
            or InvalidOperationException or System.Xml.XmlException)
        {
            throw new ConfigException($"Combat seeds cannot read '{path}' ShopItemSpec: {failure.Message}");
        }
        var header = rows.FirstOrDefault(row => row.Contains("shopItemId") && row.Contains("buffName"))
            ?? throw new ConfigException($"'{path}' ShopItemSpec lacks the combat effect columns.");
        int Column(string name)
        {
            var columns = header.Select((cell, index) => (cell, index)).Where(pair => pair.cell == name).ToArray();
            return columns.Length == 1 ? columns[0].index
                : throw new ConfigException($"'{path}' ShopItemSpec requires one '{name}' column.");
        }
        var itemColumn = Column("shopItemId");
        var nameColumn = Column("buffName");
        var valueColumn = Column("value");
        var channelColumn = Column("channel");
        var groupColumn = Column("buffGroup");
        var effects = new Dictionary<int, List<PetCombatEffect>>();
        foreach (var row in rows)
        {
            PetCombatEffectKind? kind = Cell(row, nameColumn) switch
            {
                "criFactorIncAmount" when critical => PetCombatEffectKind.AssignCriticalChancePercent,
                "criticalCancelRatio" when cancellation => PetCombatEffectKind.AddCriticalCancellationRatio,
                _ => null,
            };
            if (kind is null) continue;
            if (!int.TryParse(Cell(row, itemColumn), NumberStyles.None, CultureInfo.InvariantCulture, out var item) || item <= 0)
                throw new ConfigException($"'{path}' combat row has no positive item id.");
            if (Cell(row, channelColumn) is not ("" or "0") || Cell(row, groupColumn) is not ("" or "0"))
                throw new ConfigException($"'{path}' item {item} combat effect requires an audited channel/group selector.");
            if (!double.TryParse(Cell(row, valueColumn), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !float.IsFinite((float)value))
                throw new ConfigException($"'{path}' item {item} has a non-finite combat effect.");
            if (!effects.TryGetValue(item, out var list)) effects.Add(item, list = []);
            list.Add(new(kind.Value, (float)value));
        }
        return new(effects.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), pets);
    }

    internal (double Critical, float Cancellation) Contributions(MatchSeedEquipment? equipment)
    {
        float ordinary = 0, cancellation = 0;
        // kr-1256 sub_818AA0 excludes only 300000..300999 from the ordered worn set after applying the character.
        foreach (var item in (equipment?.WornItemIds ?? []).OrderBy(id => id is >= 300000 and < 301000 ? 0 : 1).ThenBy(id => id))
        {
            foreach (var effect in staticEffects.GetValueOrDefault(item) ?? []) Apply(effect);
        }
        // kr-1256 sub_817500 supplies the scene vector to kr-1256 sub_818AA0 after all static item rows.
        if (pets is not null)
            foreach (var effect in pets.ForEquipment(equipment)) Apply(effect);
        if (!float.IsFinite(ordinary) || !float.IsFinite(cancellation))
            throw new InvalidOperationException("Combat seed attributes exceed the native float range.");
        return (ordinary / 100.0, Math.Clamp(cancellation, 0, 1));

        void Apply(PetCombatEffect effect)
        {
            // kr-1256 sub_A04850 assigns effect3 and float-adds effect21; cancellation is already a ratio.
            if (effect.Kind == PetCombatEffectKind.AssignCriticalChancePercent) ordinary = effect.Amount;
            else if (effect.Kind == PetCombatEffectKind.AddCriticalCancellationRatio) cancellation += effect.Amount;
        }
    }

    internal uint[] CancellationTable(int roomId, int generation, int entityId, MatchSeedEquipment? equipment)
    {
        var chance = Contributions(equipment).Cancellation;
        var words = new uint[SceneSeedAuthority.WordsPerTable];
        if (chance == 0) return words;
        // kr-1256 sub_483540 installs 1000 bits; kr-1256 sub_6F3200 consumes the victim's table on every recipient.
        // A keyed actor/round draw keeps those recipients in agreement without retaining an unbounded actor cache.
        Span<byte> input = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(input, roomId);
        BinaryPrimitives.WriteInt32LittleEndian(input[4..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(input[8..], entityId);
        Span<byte> draw = stackalloc byte[32];
        for (var block = 0; block < (SceneSeedAuthority.BitsPerTable + 3) / 4; block++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(input[12..], block);
            HMACSHA256.HashData(_secret, input, draw);
            for (var index = 0; index < 4 && block * 4 + index < SceneSeedAuthority.BitsPerTable; index++)
            {
                var value = BinaryPrimitives.ReadUInt64LittleEndian(draw[(index * 8)..]) >> 11;
                if (value * (1.0 / (1UL << 53)) >= chance) continue;
                var bit = block * 4 + index;
                words[bit >> 5] |= 1u << (bit & 31);
            }
        }
        return words;
    }

    private static string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column].Trim() : "";
}
