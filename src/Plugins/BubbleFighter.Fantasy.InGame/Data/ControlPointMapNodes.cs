using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Indexes just the released Control Point stages. Rooms share their build's verified masks.</summary>
internal sealed class ControlPointStageCatalog(IEnumerable<string> releasedStages, Func<string, int> readMask,
    Action<string, Exception> unavailable)
{
    private readonly Dictionary<string, Lazy<int>> _stages = releasedStages.Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(stage => stage, stage => new Lazy<int>(() =>
        {
            try { return readMask(stage); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                unavailable(stage, ex);
                return 0;
            }
        }), StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns zero without opening any resource for a stage outside the build's menu.</summary>
    internal int Nodes(string stage) => _stages.TryGetValue(stage, out var nodes) ? nodes.Value : 0;
}

/// <summary>Resolves the objectives available on a selected map from its native mesh material properties.</summary>
internal static class ControlPointMapNodes
{
    /// <summary>Reads the stage's world block into a bit mask: bit zero is node 1, even on a map whose dialect has more snapshot records.</summary>
    internal static int Load(IClientDataStore store, string stageDirectory, int maximumNodes, int maximumFileBytes = 8 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumNodes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumNodes, 31);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 8);
        if (string.IsNullOrEmpty(stageDirectory) || stageDirectory.Length > 255 ||
            stageDirectory.Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
                part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))))
            throw new InvalidDataException("Control Point requires a relative stage directory with valid map-name segments.");

        var nodes = 0;
        var bytes = store.Read($"Maps/{stageDirectory}/track.wb", maximumFileBytes);
        WorldBlockFile.VisitMeshMaterialProperties(bytes, maximumFileBytes, properties =>
        {
            // kr-1256 sub_1093350 checks the case-sensitive nodePoint prefix on a material property element.
            const string prefix = "nodePoint";
            if (!properties.Contains("<" + prefix, StringComparison.Ordinal)) return;
            XElement root;
            try
            {
                using var text = new StringReader(properties);
                using var xml = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                root = XElement.Load(xml);
            }
            catch (XmlException ex) { throw new InvalidDataException("Malformed Control Point material property XML.", ex); }
            if (root.Name != "property") return;
            foreach (var element in root.Elements())
            {
                var name = element.Name.LocalName;
                if (element.Name.Namespace != XNamespace.None || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var suffix = name.AsSpan(prefix.Length);
                if (suffix.IsEmpty || suffix.IndexOfAnyExceptInRange('0', '9') >= 0 ||
                    !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var node) || node < 1 || node > maximumNodes)
                    throw new InvalidDataException($"Control Point map node '{name}' is outside 1..{maximumNodes} or has an invalid suffix.");
                nodes |= 1 << (node - 1);
            }
        });
        return nodes != 0 ? nodes : throw new InvalidDataException("Control Point map contains no supported node material properties.");
    }
}
