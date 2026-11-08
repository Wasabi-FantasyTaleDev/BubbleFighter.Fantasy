using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Native map-specific tsunami intervals, independent of the scripted cube fog.</summary>
internal sealed record FinalZoneWaterRules(bool Enabled, int StartMs, int AlarmMs, int ShortenMs,
    int MinimumAlarmMs, int EscapeMs, int DownMs, int UpMs, int StopMs)
{
    internal const string Path = "Config/SurvivalMode.xml";
    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    internal static FinalZoneWaterRules Load(IClientDataStore store, string stage, FinalZoneDataLimits limits)
        => FromFields(LoadFields(store, stage, limits));

    internal static IReadOnlyDictionary<string, string> LoadFields(IClientDataStore store, string stage, FinalZoneDataLimits limits)
    {
        var bytes = FinalZoneItemCatalog.ReadBounded(store, Path, limits);
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limits.MaximumFileBytes,
        });
        var sheet = XDocument.Load(reader).Descendants(Ss + "Worksheet").FirstOrDefault()
            ?? throw new InvalidDataException($"{Path}: missing map worksheet.");
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in sheet.Descendants(Ss + "Row"))
        {
            if (rows.Count >= limits.MaximumRows) throw new InvalidDataException($"{Path}: too many map rows.");
            var cells = new List<string>();
            foreach (var cell in row.Elements(Ss + "Cell"))
            {
                var indexText = (string?)cell.Attribute(Ss + "Index");
                var index = indexText is null ? cells.Count + 1 : Number(indexText, 1, limits.MaximumRows);
                if (index <= cells.Count || index > limits.MaximumRows)
                    throw new InvalidDataException($"{Path}: invalid or excessive column index.");
                while (cells.Count < index - 1) cells.Add(string.Empty);
                cells.Add(cell.Element(Ss + "Data")?.Value.Trim() ?? string.Empty);
            }
            rows.Add(cells);
        }
        var header = rows.SingleOrDefault(row => row.Count > 0 && row[0] == "[Field]")
            ?? throw new InvalidDataException($"{Path}: missing unique [Field] header.");
        var columns = Enumerable.Range(2, Math.Max(0, header.Count - 2)).Where(index => header[index] == stage).ToArray();
        if (columns.Length != 1) throw new InvalidDataException($"{Path}: missing unique map '{stage}'.");
        var column = columns[0];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(row => row.Count > column && row[0].Length > 0 && !row[0].StartsWith('#')))
        {
            if (!fields.TryAdd(row[0], row[column]))
                throw new InvalidDataException($"{Path}: duplicate map field '{row[0]}'.");
        }
        return fields;
    }

    internal static int Field(IReadOnlyDictionary<string, string> fields, string key, int minimum = 0, int maximum = int.MaxValue) =>
        fields.TryGetValue(key, out var value) ? Number(value, minimum, maximum)
            : throw new InvalidDataException($"{Path}: missing map field '{key}'.");

    internal static FinalZoneWaterRules FromFields(IReadOnlyDictionary<string, string> fields)
    {
        int Value(string key, int minimum = 0) => Field(fields, key, minimum);
        var enabled = Value("TsunamiUse");
        if (enabled is not (0 or 1)) throw new InvalidDataException($"{Path}: TsunamiUse must be boolean.");
        if (enabled == 0) return new(false, 0, 0, 0, 0, 0, 0, 0, 0);
        var result = new FinalZoneWaterRules(true, Value("TsunamiStartTime"), Value("TsunamiAlarmTime", 1),
            Value("TsunamiAlarmShortenTime"), Value("TsunamiAlarmMinTime"), Value("TsunamiEscapeTime", 1),
            Value("TsunamiWaterDownTime", 1), Value("TsunamiWaterUpTime", 1), Value("TsunamiAllStopTime", 1));
        if (result.MinimumAlarmMs > result.AlarmMs || result.ShortenMs > 0 && result.MinimumAlarmMs <= 0
            || result.StartMs >= result.StopMs)
            throw new InvalidDataException($"{Path}: inconsistent tsunami timing.");
        return result;
    }

    private static int Number(string text, int minimum, int maximum) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= minimum && value <= maximum ? value
            : throw new InvalidDataException($"{Path}: invalid bounded timing or column value '{text}'.");
}
