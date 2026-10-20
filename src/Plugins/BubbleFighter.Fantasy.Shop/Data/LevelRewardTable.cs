using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>Reads the native level guide's reward previews and resolves their Stock or StockSet keys.</summary>
internal static class LevelRewardTable
{
    internal const int MaxBytes = 1_048_576;

    internal static ShopLevelReward[] Read(byte[] bytes, ShopCatalog catalog, Action<string> diagnostic)
    {
        if (bytes.Length > MaxBytes)
            throw new ConfigException("The level reward guide exceeds its one-megabyte bound.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var header = (reader.ReadLine() ?? "").Split('\t');
        var lv = Array.IndexOf(header, "Lv");
        var item = Array.IndexOf(header, "ItemID");
        var msg = Array.IndexOf(header, "Msg");
        if (lv < 0 || item < 0 || msg < 0)
            throw new ConfigException("The level reward guide needs Lv, ItemID and Msg columns.");
        var highestColumn = Math.Max(lv, Math.Max(item, msg));
        var rows = new List<(int Level, int Key, string Message)>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var columns = line.Split('\t');
            if (columns.Length <= highestColumn || rows.Count >= ShopOptions.MaxRewardLevel
                || !int.TryParse(columns[lv], NumberStyles.None, CultureInfo.InvariantCulture, out var level)
                || level != rows.Count + 1)
                throw new ConfigException("The level reward guide needs bounded, consecutive levels beginning with 1.");
            // cn-324 sub_6A2380 uses StrToIntW; id-400 sub_72C720 -> sub_9D2370 accepts the numeric prefix with wcstol.
            var keyText = columns[item].Trim();
            if (keyText.EndsWith('_'))
                keyText = keyText[..^1];
            if (!int.TryParse(keyText, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key <= 0)
                throw new ConfigException($"The level reward guide has an invalid ItemID at level {level}.");
            rows.Add((level, key, columns[msg]));
        }

        var rewards = new List<ShopLevelReward>();
        var unresolved = new HashSet<int>();
        for (var start = 0; start < rows.Count;)
        {
            var end = start;
            while (end + 1 < rows.Count && rows[end + 1].Key == rows[start].Key
                && rows[end + 1].Message == rows[start].Message)
                end++;
            var row = rows[end];
            // Repeated previews end one level before the reward. The resource's first number independently states it.
            var number = new string(row.Message.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var target)
                || target != row.Level + 1 || target > ShopOptions.MaxRewardLevel)
                throw new ConfigException($"The level guide preview ending at {row.Level} does not establish its reward level.");
            if (catalog.Gifts.ContainsKey(row.Key))
                rewards.Add(new ShopLevelReward { Level = target, StockKeys = [row.Key] });
            else
                unresolved.Add(row.Key);
            start = end + 1;
        }
        if (rewards.Count > ShopOptions.MaxLevelRewardEntries)
            throw new ConfigException("The level reward guide exceeds the configured reward entry bound.");
        if (unresolved.Count > 0)
            diagnostic($"Level guide keys without grant contents are not rewards: {string.Join(", ", unresolved.Order())}.");
        return rewards.ToArray();
    }
}
